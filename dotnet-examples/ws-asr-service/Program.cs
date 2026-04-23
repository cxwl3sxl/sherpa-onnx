using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SherpaOnnx;

namespace WsAsrService;

/// <summary>
/// 识别器包装类，用于追踪实例来源
/// </summary>
internal readonly struct RecognizerHandle
{
  public OfflineRecognizer Recognizer { get; }
  public bool IsEmergency { get; }

  public RecognizerHandle(OfflineRecognizer recognizer, bool isEmergency)
  {
    Recognizer = recognizer;
    IsEmergency = isEmergency;
  }
}

class Program
{
  private const string EndMarker = "1049712a-2b0c-4be5-8c36-573e8a40f6d5";

  private static AppConfig? _config;
  private static VadModelConfig _vadConfig = new();

  // 优化：模型池化 - 使用有界 Channel 防止内存泄漏
  private static Channel<OfflineRecognizer> _recognizerPool = null!;
  private static int _poolSize = 4;
  private static int _acquireTimeoutSeconds = 30;
  private static SemaphoreSlim _connectionSemaphore = null!;
  // 活跃连接数（用于监控）
  private static int _activeConnections;
  // 总处理请求数（用于监控）
  private static long _totalRequests;
  // 当前活跃的紧急实例数（用于限制紧急创建）
  private static int _emergencyInstances;
  // 紧急实例最大数量（基于内存计算）
  private static int _maxEmergencyInstances = 4;

  static async Task Main(string[] args)
  {
    var configPath = args.Length > 0 ? args[0] : "config.json";
    Console.WriteLine($"Loading config from: {configPath}");

    if (!File.Exists(configPath))
    {
      Console.WriteLine($"Config file not found: {configPath}");
      return;
    }

    var jsonOptions = new JsonSerializerOptions
    {
      PropertyNameCaseInsensitive = true
    };

    var json = await File.ReadAllTextAsync(configPath);
    _config = JsonSerializer.Deserialize<AppConfig>(json, jsonOptions);
    if (_config == null)
    {
      Console.WriteLine("Failed to parse config.json");
      return;
    }

    Console.WriteLine($"Server config: {_config.Server.Host}:{_config.Server.Port}");
    Console.WriteLine($"Auth token: {_config.Auth.Token}");

    // Initialize ASR
    if (!InitAsr()) return;

    // Start WebSocket server
    var listener = new HttpListener();
    var prefixHost = _config.Server.Host == "0.0.0.0" ? "+" : _config.Server.Host;
    listener.Prefixes.Add($"http://{prefixHost}:{_config.Server.Port}/");
    try
    {
      listener.Start();
    }
    catch (HttpListenerException ex)
    {
      Console.WriteLine($"Failed to start listener: {ex.Message}");
      Console.WriteLine("On Windows, you may need to register the URL with:");
      Console.WriteLine($"  netsh http add urlacl url=http://+:{_config.Server.Port}/ user=<username>");
      return;
    }

    Console.WriteLine($"WebSocket server listening on ws://{_config.Server.Host}:{_config.Server.Port}/");

    while (true)
    {
      var context = await listener.GetContextAsync();
      if (context.Request.IsWebSocketRequest)
      {
        _ = HandleWebSocketAsync(context);
      }
      else
      {
        context.Response.StatusCode = 400;
        context.Response.Close();
      }
    }
  }

  private static bool InitAsr()
  {
    if (_config == null) return false;

    // Check model files exist
    if (!File.Exists(_config.Model.Paraformer))
    {
      Console.WriteLine($"Model not found: {_config.Model.Paraformer}");
      Console.WriteLine("Please download from https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models");
      return false;
    }

    if (!File.Exists(_config.Model.Tokens))
    {
      Console.WriteLine($"Tokens not found: {_config.Model.Tokens}");
      return false;
    }

    if (!File.Exists(_config.Model.Vad))
    {
      Console.WriteLine($"VAD not found: {_config.Model.Vad}");
      Console.WriteLine("Please download silero_vad.onnx or ten-vad.onnx");
      return false;
    }

    // 并发配置：从配置读取，默认 4
    _poolSize = _config.Server.MaxConcurrency > 0 ? _config.Server.MaxConcurrency : 4;
    _acquireTimeoutSeconds = _config.Server.AcquireTimeoutSeconds > 0 ? _config.Server.AcquireTimeoutSeconds : 30;

    // 基于内存计算安全上限
    var (safePoolSize, emergencyLimit) = CalculateSafePoolSize(_poolSize);
    if (safePoolSize < _poolSize)
    {
      Console.WriteLine($"[Pool] Warning: Configured pool size {_poolSize} exceeds memory limit, using {safePoolSize}");
      _poolSize = safePoolSize;
    }
    _maxEmergencyInstances = emergencyLimit;

    _connectionSemaphore = new SemaphoreSlim(_poolSize, _poolSize);

    // 创建有界 Channel，容量等于 poolSize，防止无限增长
    // 注意：SingleReader = false 因为多个连接会并发调用 AcquireRecognizerAsync
    _recognizerPool = Channel.CreateBounded<OfflineRecognizer>(new BoundedChannelOptions(_poolSize)
    {
      SingleReader = false,  // 多并发连接同时获取
      SingleWriter = false   // 多生产者安全
    });

    Console.WriteLine($"Initializing ASR model pool with {_poolSize} instances (timeout: {_acquireTimeoutSeconds}s, max emergency: {_maxEmergencyInstances})...");
    var recognizerConfig = new OfflineRecognizerConfig();
    recognizerConfig.ModelConfig.Paraformer.Model = _config.Model.Paraformer;
    recognizerConfig.ModelConfig.Tokens = _config.Model.Tokens;
    recognizerConfig.ModelConfig.Debug = 0;

    // 预热模型池
    for (int i = 0; i < _poolSize; i++)
    {
      var recognizer = new OfflineRecognizer(recognizerConfig);
      _recognizerPool.Writer.TryWrite(recognizer);
      Console.WriteLine($"  [Pool] Instance {i + 1}/{_poolSize} ready");
    }

    _vadConfig = new VadModelConfig();
    _vadConfig.SileroVad.Model = _config.Model.Vad;
    _vadConfig.SileroVad.Threshold = 0.3f;
    _vadConfig.SileroVad.MinSilenceDuration = 0.5f;
    _vadConfig.SileroVad.MinSpeechDuration = 0.25f;
    _vadConfig.SileroVad.MaxSpeechDuration = 5.0f;
    _vadConfig.SileroVad.WindowSize = 512;
    _vadConfig.Debug = 0;

    Console.WriteLine($"ASR model pool initialized: {_poolSize} instances available");
    return true;
  }

  /// <summary>
  /// 基于内存计算安全的池大小和紧急实例上限
  /// </summary>
  private static (int safePoolSize, int emergencyLimit) CalculateSafePoolSize(int configuredSize)
  {
    // 获取系统总内存
    var gcMemoryInfo = GC.GetGCMemoryInfo();
    long totalMemoryMb = gcMemoryInfo.TotalAvailableMemoryBytes / (1024 * 1024);

    // 如果无法获取，使用默认值 4096MB
    if (totalMemoryMb <= 0) totalMemoryMb = 4096;

    // 每个 recognizer 约占用 100-200MB，按保守估计 200MB 计算
    // 使用总内存的 30% 作为安全上限
    const int memoryPerInstanceMb = 200;
    var safeByMemory = (int)(totalMemoryMb * 0.3 / memoryPerInstanceMb);
    var safePoolSize = Math.Max(2, Math.Min(configuredSize, safeByMemory));

    // 紧急实��上限为基础池大小的 50%
    var emergencyLimit = Math.Max(1, safePoolSize / 2);

    Console.WriteLine($"[Pool] System memory: {totalMemoryMb}MB, memory-safe pool size: {safeByMemory}");

    return (safePoolSize, emergencyLimit);
  }

  /// <summary>
  /// 从池中获取 recognizer，超时则排队等待
  /// </summary>
  private static async Task<RecognizerHandle?> AcquireRecognizerAsync(CancellationToken ct)
  {
    try
    {
      // 尝试非阻塞获取
      if (_recognizerPool.Reader.TryRead(out var recognizer))
      {
        Interlocked.Increment(ref _activeConnections);
        Console.WriteLine($"[Pool] Acquired. Active: {_activeConnections}");
        return new RecognizerHandle(recognizer, isEmergency: false);
      }

      // 等待可用 recognizer（带超时保护）
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      cts.CancelAfter(TimeSpan.FromSeconds(_acquireTimeoutSeconds));

      var rec = await _recognizerPool.Reader.ReadAsync(cts.Token);
      Interlocked.Increment(ref _activeConnections);
      Console.WriteLine($"[Pool] Acquired (waited). Active: {_activeConnections}");
      return new RecognizerHandle(rec, isEmergency: false);
    }
    catch (OperationCanceledException)
    {
      Console.WriteLine("[Pool] Acquire timeout, creating emergency instance");
      return TryCreateEmergencyRecognizerAsync();
    }
  }

  /// <summary>
  /// 紧急情况下创建新的 recognizer（后备方案）
  /// </summary>
  private static RecognizerHandle? TryCreateEmergencyRecognizerAsync()
  {
    // 检查紧急实例上限
    var currentEmergency = Interlocked.Increment(ref _emergencyInstances);
    if (currentEmergency > _maxEmergencyInstances)
    {
      Interlocked.Decrement(ref _emergencyInstances);
      Console.WriteLine($"[Pool] Emergency limit reached ({_maxEmergencyInstances}), refusing to create more");
      return null;
    }

    try
    {
      var recognizerConfig = new OfflineRecognizerConfig();
      recognizerConfig.ModelConfig.Paraformer.Model = _config?.Model.Paraformer ?? "";
      recognizerConfig.ModelConfig.Tokens = _config?.Model.Tokens ?? "";
      recognizerConfig.ModelConfig.Debug = 0;

      var recognizer = new OfflineRecognizer(recognizerConfig);
      Interlocked.Increment(ref _activeConnections);
      Console.WriteLine($"[Pool] Emergency created ({currentEmergency}/{_maxEmergencyInstances}). Active: {_activeConnections}");
      return new RecognizerHandle(recognizer, isEmergency: true);
    }
    catch (Exception ex)
    {
      Interlocked.Decrement(ref _emergencyInstances);
      Console.WriteLine($"[Pool] Emergency creation failed: {ex.Message}");
      return null;
    }
  }

  /// <summary>
  /// 归还 recognizer 到池中
  /// </summary>
  private static void ReleaseRecognizer(OfflineRecognizer recognizer, bool isEmergency)
  {
    Interlocked.Decrement(ref _activeConnections);
    Interlocked.Increment(ref _totalRequests);

    // 尝试放回池中
    if (_recognizerPool.Writer.TryWrite(recognizer))
    {
      // 如果是紧急实例，成功放回后恢复紧急计数（下次紧急创建时仍可用）
      if (isEmergency)
      {
        Interlocked.Decrement(ref _emergencyInstances);
      }
      Console.WriteLine($"[Pool] Released to pool. Active: {_activeConnections}, Total: {_totalRequests}");
      return;
    }

    // 池已满，释放资源
    try
    {
      recognizer.Dispose();
      // 紧急实例需要递减计数
      if (isEmergency)
      {
        Interlocked.Decrement(ref _emergencyInstances);
      }
      Console.WriteLine($"[Pool] Released (pool full, disposed). Active: {_activeConnections}");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Pool] Warning: Failed to dispose: {ex.Message}");
    }
  }

  private static async Task HandleWebSocketAsync(HttpListenerContext context)
  {
    WebSocket? ws = null;
    try
    {
      var wsContext = await context.AcceptWebSocketAsync(null);
      ws = wsContext.WebSocket;

      Console.WriteLine("New WebSocket connection");

      // 并发控制：等待可用槽位（使用配置的超时时间）
      var acquired = await _connectionSemaphore.WaitAsync(TimeSpan.FromSeconds(_acquireTimeoutSeconds));
      if (!acquired)
      {
        await SendMessageAsync(ws, new WsMessage
        {
          Type = "error",
          Success = false,
          Error = "Server at capacity, please retry later"
        });
        await ws.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "Capacity limit", CancellationToken.None);
        return;
      }

      try
      {
        // Authenticate via URL query parameter
        var query = context.Request.QueryString;
        var token = query["token"] ?? "";
        var authError = ValidateToken(token);
        if (authError != null)
        {
          await SendMessageAsync(ws, new WsMessage
          {
            Type = "auth",
            Success = false,
            Error = authError
          });
          await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, authError, CancellationToken.None);
          return;
        }

        Console.WriteLine("Client authenticated");
        await ProcessAudioAsync(ws);
      }
      finally
      {
        _connectionSemaphore.Release();
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"WebSocket error: {ex.Message}");
    }
    finally
    {
      if (ws != null && ws.State != WebSocketState.Closed)
      {
        try
        {
          await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        catch
        {
          // ignored
        }
      }

      Console.WriteLine("WebSocket connection closed");
    }
  }

  private static string? ValidateToken(string token)
  {
    if (string.IsNullOrEmpty(token))
    {
      return "Missing token";
    }

    if (token != _config?.Auth.Token)
    {
      return "Invalid token";
    }

    return null;
  }

  private static async Task ProcessAudioAsync(WebSocket ws)
  {
    if (_config == null)
    {
      await SendMessageAsync(ws, new WsMessage
      {
        Type = "error",
        Success = false,
        Error = "Server not initialized"
      });
      return;
    }

    // 从池中获取 recognizer
    var handle = await AcquireRecognizerAsync(CancellationToken.None);
    if (handle == null)
    {
      await SendMessageAsync(ws, new WsMessage
      {
        Type = "error",
        Success = false,
        Error = "Failed to acquire ASR engine"
      });
      return;
    }

    var recognizer = handle.Value.Recognizer;
    var isEmergency = handle.Value.IsEmergency;

    try
    {
      // Create VAD for this session
      var vad = new VoiceActivityDetector(_vadConfig, 60);
      var buffer = new byte[4096];
      var endMarker = ParseEndMarker();
      var sampleRate = _vadConfig.SampleRate;

      while (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent)
      {
        try
        {
          var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

          if (result.MessageType == WebSocketMessageType.Close)
          {
            break;
          }

          var data = buffer.Take(result.Count).ToArray();

          // Check for end marker
          if (data.Length >= endMarker.Length &&
              data.TakeLast(endMarker.Length).SequenceEqual(endMarker))
          {
            Console.WriteLine("Received end marker, processing audio...");
            break;
          }

          // Process through VAD
          var samples = ConvertToFloat(data);
          vad.AcceptWaveform(samples);

          if (vad.IsSpeechDetected())
          {
            while (!vad.IsEmpty())
            {
              var segment = vad.Front();
              var startMs = (long)(segment.Start * 1000.0 / sampleRate);
              var endMs = (long)((segment.Start + segment.Samples.Length) * 1000.0 / sampleRate);
              var text = RecognizeSegment(recognizer, segment.Samples);
              if (!string.IsNullOrEmpty(text))
              {
                Console.WriteLine($"Result: {text} [{startMs}-{endMs}ms]");
                await SendMessageAsync(ws, new WsMessage
                {
                  Type = "result",
                  Success = true,
                  Content = text,
                  StartMs = startMs,
                  EndMs = endMs
                });
              }

              vad.Pop();
            }
          }
        }
        catch (WebSocketException ex)
        {
          Console.WriteLine($"Receive error: {ex.Message}");
          break;
        }
      }

      // Flush VAD
      vad.Flush();

      while (!vad.IsEmpty())
      {
        var segment = vad.Front();
        var startMs = (long)(segment.Start * 1000.0 / sampleRate);
        var endMs = (long)((segment.Start + segment.Samples.Length) * 1000.0 / sampleRate);
        var text = RecognizeSegment(recognizer, segment.Samples);
        if (!string.IsNullOrEmpty(text))
        {
          Console.WriteLine($"Result: {text} [{startMs}-{endMs}ms]");
          await SendMessageAsync(ws, new WsMessage
          {
            Type = "result",
            Success = true,
            Content = text,
            StartMs = startMs,
            EndMs = endMs
          });
        }

        vad.Pop();
      }

      await SendMessageAsync(ws, new WsMessage
      {
        Type = "done",
        Success = true,
        Content = "Recognition completed"
      });
    }
    finally
    {
      // 归还 recognizer 到池中
      ReleaseRecognizer(recognizer, isEmergency);
    }
  }

  private static float[] ConvertToFloat(byte[] data)
  {
    var samples = new float[data.Length / 2];
    for (int i = 0; i < samples.Length; i++)
    {
      samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
    }

    return samples;
  }

  private static string RecognizeSegment(OfflineRecognizer recognizer, float[] samples)
  {
    if (samples.Length == 0) return "";

    var stream = recognizer.CreateStream();
    stream.AcceptWaveform(_config?.Audio.SampleRate ?? 16000, samples);
    recognizer.Decode(stream);
    return stream.Result.Text ?? "";
  }

  private static async Task SendMessageAsync(WebSocket ws, WsMessage msg)
  {
    Console.WriteLine($"send {msg.Type} message");
    var json = JsonSerializer.Serialize(msg);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
  }

  private static byte[] ParseEndMarker()
  {
    var hex = EndMarker.Replace("-", "");
    var bytes = new byte[hex.Length / 2];
    for (var i = 0; i < bytes.Length; i++)
    {
      bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
    }

    return bytes;

  }
}
