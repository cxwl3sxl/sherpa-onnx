using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;
using SherpaOnnx;

namespace WsAsrService;

/// <summary>
/// WebSocket 服务器封装
/// </summary>
public class WebSocketServer
{
  private readonly AppConfig _config;
  private readonly OfflineRecognizerConfig _recognizerConfig;
  private readonly VadModelConfig _vadConfig;
  private HttpListener? _listener;
  private CancellationTokenSource? _cts;
  private readonly Channel<OfflineRecognizer> _recognizerPool;
  private readonly SemaphoreSlim _connectionSemaphore;
  private readonly int _poolSize;
  private readonly int _acquireTimeoutSeconds;
  private readonly int _maxEmergencyInstances;
  private int _emergencyInstances;
  private int _activeConnections;
  private long _totalRequests;
  private readonly int _sampleRate;

  private const string EndMarker = "1049712a-2b0c-4be5-8c36-573e8a40f6d5";

  /// <summary>
  /// 当前活动连接数
  /// </summary>
  public int ActiveConnections => _activeConnections;

  /// <summary>
  /// 总请求数
  /// </summary>
  public long TotalRequests => _totalRequests;

  /// <summary>
  /// 识别引擎池大小
  /// </summary>
  public int PoolSize => _poolSize;

  /// <summary>
  /// 池中可用实例数
  /// </summary>
  public int AvailableInPool => _recognizerPool.Reader.Count;

  /// <summary>
  /// 紧急实例数
  /// </summary>
  public int EmergencyInstances => _emergencyInstances;

  public WebSocketServer(AppConfig config)
  {
    _config = config;
    _recognizerConfig = CreateRecognizerConfig(config);
    _vadConfig = CreateVadConfig(config);
    _poolSize = config.Server.MaxConcurrency > 0 ? config.Server.MaxConcurrency : 4;
    _acquireTimeoutSeconds = config.Server.AcquireTimeoutSeconds > 0 ? config.Server.AcquireTimeoutSeconds : 30;
    _maxEmergencyInstances = Math.Max(1, _poolSize / 2);
    _sampleRate = config.Audio.SampleRate;

    _connectionSemaphore = new SemaphoreSlim(_poolSize, _poolSize);
    _recognizerPool = Channel.CreateBounded<OfflineRecognizer>(new BoundedChannelOptions(_poolSize)
    {
      SingleReader = false,
      SingleWriter = false
    });
    _cts = new CancellationTokenSource();
    Task.Run(() => ListenerLoopAsync(_cts.Token));
  }

  private static OfflineRecognizerConfig CreateRecognizerConfig(AppConfig config)
  {
    var recognizerConfig = new OfflineRecognizerConfig();
    recognizerConfig.ModelConfig.Paraformer.Model = config.Model.Paraformer;
    recognizerConfig.ModelConfig.Tokens = config.Model.Tokens;
    recognizerConfig.ModelConfig.Debug = 0;
    return recognizerConfig;
  }

  private static VadModelConfig CreateVadConfig(AppConfig config)
  {
    var vadConfig = new VadModelConfig();
    vadConfig.SileroVad.Model = config.Model.Vad;
    vadConfig.SileroVad.Threshold = 0.3f;
    vadConfig.SileroVad.MinSilenceDuration = 0.5f;
    vadConfig.SileroVad.MinSpeechDuration = 0.25f;
    vadConfig.SileroVad.MaxSpeechDuration = 5.0f;
    vadConfig.SileroVad.WindowSize = 512;
    vadConfig.Debug = 0;
    return vadConfig;
  }

  public Task StartAsync(CancellationToken cancellationToken)
  {
    // 初始化模型池
    for (int i = 0; i < _poolSize; i++)
    {
      var recognizer = new OfflineRecognizer(_recognizerConfig);
      _recognizerPool.Writer.TryWrite(recognizer);
      Log.Debug("Recognizer instance {Index}/{PoolSize} initialized", i + 1, _poolSize);
    }
    Log.Information("ASR model pool initialized with {PoolSize} instances", _poolSize);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _cts?.Cancel();
    Log.Information("Listener stopped");
    return Task.CompletedTask;
  }

  private async Task ListenerLoopAsync(CancellationToken cancellationToken)
  {
    _listener = new HttpListener();
    var prefixHost = _config.Server.Host == "0.0.0.0" ? "+" : _config.Server.Host;
    _listener.Prefixes.Add($"http://{prefixHost}:{_config.Server.Port}/");

    try
    {
      _listener.Start();
      Log.Information("WebSocket server listening on ws://{Host}:{Port}/", _config.Server.Host, _config.Server.Port);
    }
    catch (HttpListenerException ex)
    {
      Log.Error(ex, "Failed to start WebSocket listener");
      Log.Information("On Windows, you may need to register the URL with: netsh http add urlacl url=http://+:{Port}/ user=<username>", _config.Server.Port);
      return;
    }

    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        var context = await _listener.GetContextAsync();
        if (context.Request.IsWebSocketRequest)
        {
          _ = HandleWebSocketAsync(context, cancellationToken);
        }
        else
        {
          // 处理 HTTP 请求 (如 /stats)
          await HandleHttpRequestAsync(context);
        }
      }
      catch (ObjectDisposedException)
      {
        break;
      }
      catch (HttpListenerException ex) when (ex.ErrorCode == 995)
      {
        // Listener stopped
        break;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Listener error");
      }
    }
  }

  private async Task HandleHttpRequestAsync(HttpListenerContext context)
  {
    var path = context.Request.Url?.AbsolutePath ?? "/";

    // 支持 CORS
    context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
    context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
    context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

    if (context.Request.HttpMethod == "OPTIONS")
    {
      context.Response.StatusCode = 204;
      context.Response.Close();
      return;
    }

    if (path == "/stats" && context.Request.HttpMethod == "GET")
    {
      await SendStatsAsync(context.Response);
    }
    else if (path == "/health" && context.Request.HttpMethod == "GET")
    {
      await SendHealthAsync(context.Response);
    }
    else
    {
      context.Response.StatusCode = 404;
      var body = Encoding.UTF8.GetBytes("{\"error\":\"Not Found\"}");
      context.Response.ContentType = "application/json";
      await context.Response.OutputStream.WriteAsync(body);
      context.Response.Close();
    }
  }

  private async Task SendStatsAsync(HttpListenerResponse response)
  {
    var process = Process.GetCurrentProcess();
    var statsData = new
    {
      timestamp = DateTime.UtcNow.ToString("o"),
      server = new
      {
        host = _config.Server.Host,
        port = _config.Server.Port,
        uptime = (DateTime.Now - process.StartTime).ToString(@"dd\:hh\:mm\:ss"),
      },
      connections = new
      {
        active = _activeConnections,
        totalRequests = _totalRequests,
        maxConcurrency = _poolSize,
        availableSlots = _connectionSemaphore.CurrentCount,
      },
      recognizer = new
      {
        poolSize = _poolSize,
        availableInPool = _recognizerPool.Reader.Count,
        emergencyInstances = _emergencyInstances,
        maxEmergency = _maxEmergencyInstances,
      },
      performance = new
      {
        processMemoryMb = process.WorkingSet64 / 1024 / 1024,
        threadCount = process.Threads.Count,
        gcHeapSizeMb = GC.GetTotalMemory(false) / 1024 / 1024,
      }
    };

    var json = JsonSerializer.Serialize(statsData);
    var body = Encoding.UTF8.GetBytes(json);
    response.ContentType = "application/json";
    response.StatusCode = 200;
    await response.OutputStream.WriteAsync(body);
    response.Close();
  }

  private async Task SendHealthAsync(HttpListenerResponse response)
  {
    var process = Process.GetCurrentProcess();
    var health = new
    {
      status = "healthy",
      timestamp = DateTime.UtcNow.ToString("o"),
      processUptime = (DateTime.Now - process.StartTime).ToString(@"dd\:hh\:mm\:ss"),
    };

    var json = JsonSerializer.Serialize(health);
    var body = Encoding.UTF8.GetBytes(json);
    response.ContentType = "application/json";
    response.StatusCode = 200;
    await response.OutputStream.WriteAsync(body);
    response.Close();
  }

  private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken cancellationToken)
  {
    WebSocket? ws = null;
    try
    {
      var wsContext = await context.AcceptWebSocketAsync(null);
      ws = wsContext.WebSocket;

      Log.Debug("New WebSocket connection from {RemoteEndPoint}", context.Request.RemoteEndPoint);

      var acquired = await _connectionSemaphore.WaitAsync(TimeSpan.FromSeconds(_acquireTimeoutSeconds), cancellationToken);
      if (!acquired)
      {
        await SendMessageAsync(ws, new WsMessage
        {
          Type = "error",
          Success = false,
          Error = "Server at capacity, please retry later"
        }, cancellationToken);
        await ws.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "Capacity limit", CancellationToken.None);
        return;
      }

      try
      {
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
          }, cancellationToken);
          await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, authError, CancellationToken.None);
          return;
        }
        else
        {
          await SendMessageAsync(ws, new WsMessage
          {
            Type = "auth",
            Success = true,
          }, cancellationToken);
        }

        Log.Debug("Client authenticated");
        await ProcessAudioAsync(ws, cancellationToken);
      }
      finally
      {
        _connectionSemaphore.Release();
      }
    }
    catch (Exception ex)
    {
      Log.Error(ex, "WebSocket error");
    }
    finally
    {
      if (ws != null && ws.State != WebSocketState.Closed)
      {
        try
        {
          await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        // ReSharper disable once EmptyGeneralCatchClause
        catch { }
      }
      Log.Debug("WebSocket connection closed");
    }
  }

  private string? ValidateToken(string token)
  {
    if (string.IsNullOrEmpty(token))
      return "Missing token";
    if (token != _config.Auth.Token)
      return "Invalid token";
    return null;
  }

  private async Task ProcessAudioAsync(WebSocket ws, CancellationToken cancellationToken)
  {
    var handle = await AcquireRecognizerAsync(cancellationToken);
    if (handle == null)
    {
      await SendMessageAsync(ws, new WsMessage
      {
        Type = "error",
        Success = false,
        Error = "Failed to acquire ASR engine"
      }, cancellationToken);
      return;
    }

    var recognizer = handle.Value.Recognizer;
    var isEmergency = handle.Value.IsEmergency;

    try
    {
      var vad = new VoiceActivityDetector(_vadConfig, 60);
      var buffer = new byte[4096];
      var endMarker = ParseEndMarker();
      var sampleRate = _vadConfig.SampleRate;

      while (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent)
      {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
          break;

        // 验证数据类型 - 只接受二进制音频数据
        if (result.MessageType != WebSocketMessageType.Binary)
        {
          Log.Warning("Received non-binary message type: {MessageType}, closing connection", result.MessageType);
          await ws.CloseOutputAsync(WebSocketCloseStatus.ProtocolError, "Binary data required", CancellationToken.None);
          return;
        }

        var data = buffer.Take(result.Count).ToArray();
        if (data.Length >= endMarker.Length && data.TakeLast(endMarker.Length).SequenceEqual(endMarker))
        {
          Log.Debug("Received end marker, processing audio...");
          break;
        }

        var samples = ConvertToFloat(data);
        vad.AcceptWaveform(samples);

        while (!vad.IsEmpty())
        {
          var segment = vad.Front();
          var text = RecognizeSegment(recognizer, segment.Samples);
          if (!string.IsNullOrEmpty(text))
          {
            var startMs = (long)(segment.Start * 1000.0 / sampleRate);
            var endMs = (long)((segment.Start + segment.Samples.Length) * 1000.0 / sampleRate);
            Log.Debug("Recognition result: {Text} [{StartMs}-{EndMs}]ms", text, startMs, endMs);
            await SendMessageAsync(ws, new WsMessage
            {
              Type = "result",
              Success = true,
              Content = text,
              StartMs = startMs,
              EndMs = endMs
            }, cancellationToken);
          }

          vad.Pop();
        }
      }

      vad.Flush();
      while (!vad.IsEmpty())
      {
        var segment = vad.Front();
        var text = RecognizeSegment(recognizer, segment.Samples);
        if (!string.IsNullOrEmpty(text))
        {
          var startMs = (long)(segment.Start * 1000.0 / sampleRate);
          var endMs = (long)((segment.Start + segment.Samples.Length) * 1000.0 / sampleRate);
          Log.Debug("Recognition result (flush): {Text} [{StartMs}-{EndMs}]ms", text, startMs, endMs);
          await SendMessageAsync(ws, new WsMessage
          {
            Type = "result",
            Success = true,
            Content = text,
            StartMs = startMs,
            EndMs = endMs
          }, cancellationToken);
        }

        vad.Pop();
      }

      await SendMessageAsync(ws, new WsMessage
      {
        Type = "done",
        Success = true
      }, cancellationToken);
    }
    finally
    {
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

  private string RecognizeSegment(OfflineRecognizer recognizer, float[] samples)
  {
    if (samples.Length == 0) return "";
    var stream = recognizer.CreateStream();
    stream.AcceptWaveform(_sampleRate, samples);
    recognizer.Decode(stream);
    return stream.Result.Text ?? "";
  }

  private static async Task SendMessageAsync(WebSocket ws, WsMessage msg, CancellationToken ct)
  {
    var json = JsonSerializer.Serialize(msg);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
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

  private async Task<RecognizerHandle?> AcquireRecognizerAsync(CancellationToken ct)
  {
    if (_recognizerPool.Reader.TryRead(out var recognizer))
    {
      Interlocked.Increment(ref _activeConnections);
      Log.Debug("Recognizer acquired from pool. Active: {Active}", _activeConnections);
      return new RecognizerHandle(recognizer, isEmergency: false);
    }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(_acquireTimeoutSeconds));

    try
    {
      var rec = await _recognizerPool.Reader.ReadAsync(cts.Token);
      Interlocked.Increment(ref _activeConnections);
      Log.Debug("Recognizer acquired (waited). Active: {Active}", _activeConnections);
      return new RecognizerHandle(rec, isEmergency: false);
    }
    catch (OperationCanceledException)
    {
      Log.Warning("Recognizer acquire timeout, creating emergency instance");
      return TryCreateEmergencyRecognizer();
    }
  }

  private RecognizerHandle? TryCreateEmergencyRecognizer()
  {
    var currentEmergency = Interlocked.Increment(ref _emergencyInstances);
    if (currentEmergency > _maxEmergencyInstances)
    {
      Interlocked.Decrement(ref _emergencyInstances);
      Log.Warning("Emergency limit reached ({Max}), refusing to create more", _maxEmergencyInstances);
      return null;
    }

    try
    {
      var recognizer = new OfflineRecognizer(_recognizerConfig);
      Interlocked.Increment(ref _activeConnections);
      Log.Warning("Emergency recognizer created ({Current}/{Max}). Active: {Active}",
        currentEmergency, _maxEmergencyInstances, _activeConnections);
      return new RecognizerHandle(recognizer, isEmergency: true);
    }
    catch (Exception ex)
    {
      Interlocked.Decrement(ref _emergencyInstances);
      Log.Error(ex, "Emergency recognizer creation failed");
      return null;
    }
  }

  private void ReleaseRecognizer(OfflineRecognizer recognizer, bool isEmergency)
  {
    Interlocked.Decrement(ref _activeConnections);
    Interlocked.Increment(ref _totalRequests);

    if (_recognizerPool.Writer.TryWrite(recognizer))
    {
      if (isEmergency) Interlocked.Decrement(ref _emergencyInstances);
      Log.Debug("Recognizer released to pool. Active: {Active}, Total: {Total}", _activeConnections, _totalRequests);
    }
    else
    {
      try
      {
        recognizer.Dispose();
        if (isEmergency) Interlocked.Decrement(ref _emergencyInstances);
        Log.Debug("Recognizer released (pool full, disposed). Active: {Active}", _activeConnections);
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Failed to dispose recognizer");
      }
    }
  }
}
