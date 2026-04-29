using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;
using SherpaOnnx;

namespace WsAsrService;

/// <summary>
/// WebSocket 服务器封装 (Kestrel)
///
/// </summary>
public class WebSocketServer
{
  private readonly AppConfig _config;
  private readonly OfflineRecognizerConfig _recognizerConfig;
  private readonly VadModelConfig _vadConfig;
  private readonly Channel<OfflineRecognizer> _recognizerPool;
  private readonly SemaphoreSlim _connectionSemaphore;
  private readonly int _poolSize;
  private readonly int _acquireTimeoutSeconds;
  private readonly int _maxEmergencyInstances;
  private readonly int _sampleRate;
  private readonly byte[] _token;

  private IHost? _host;
  private int _emergencyInstances;
  private int _activeConnections;
  private long _totalRequests;

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
    _token = Encoding.UTF8.GetBytes($"Bearer {config.Auth.Token}");
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

  public async Task StartAsync(CancellationToken cancellationToken)
  {
    // 初始化模型池
    for (int i = 0; i < _poolSize; i++)
    {
      var recognizer = new OfflineRecognizer(_recognizerConfig);
      _recognizerPool.Writer.TryWrite(recognizer);
      Log.Debug("Recognizer instance {Index}/{PoolSize} initialized", i + 1, _poolSize);
    }

    Log.Information("ASR model pool initialized with {PoolSize} instances", _poolSize);

// 创建 WebApplication
    var builder = WebApplication.CreateBuilder();

    // 配置 Kestrel
    builder.WebHost.ConfigureKestrel(webHostOptions =>
    {
      if (_config.Server.SslEnabled
          && !string.IsNullOrEmpty(_config.Server.SslCertPath)
          && File.Exists(_config.Server.SslCertPath))
      {
        var cert = new X509Certificate2(
          _config.Server.SslCertPath,
          _config.Server.SslCertPassword,
          X509KeyStorageFlags.MachineKeySet);

        webHostOptions.Listen(IPAddress.Any, _config.Server.Port, listenOptions => listenOptions.UseHttps(cert));
        Log.Information("Kestrel listening on https://{Host}:{Port}/", _config.Server.Host, _config.Server.Port);
      }
      else
      {
        webHostOptions.Listen(IPAddress.Any, _config.Server.Port);
        Log.Information("Kestrel listening on http://{Host}:{Port}/", _config.Server.Host, _config.Server.Port);
      }
    });

    _host = builder.Build();
    var app = (WebApplication)_host;

    app.UseWebSockets();

    // 处理 HTTP 请求路由
    app.MapGet("/stats", HandleStatsAsync);
    app.MapGet("/health", HandleHealthAsync);

// 其他请求默认处理 WebSocket
    app.Use(async (context, next) =>
    {
      if (context.WebSockets.IsWebSocketRequest)
      {
        await HandleWebSocketAsync(context);
      }
      else
      {
        await next(context); // 传递给其他中间件 (如 MapGet)
      }
    });

    await app.RunAsync(cancellationToken);
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    Log.Information("WebSocket server stopped");
    return Task.CompletedTask;
  }

  // ==================== HTTP Handlers ====================

  private async Task HandleStatsAsync(HttpContext context)
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

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(statsData));
  }

  private async Task HandleHealthAsync(HttpContext context)
  {
    var process = Process.GetCurrentProcess();
    var health = new
    {
      status = "healthy",
      timestamp = DateTime.UtcNow.ToString("o"),
      processUptime = (DateTime.Now - process.StartTime).ToString(@"dd\:hh\:mm\:ss"),
    };

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(health));
  }

  private async Task HandleWebSocketAsync(HttpContext context)
  {
    var ws = await context.WebSockets.AcceptWebSocketAsync();

    // IP 白名单检查
    var clientIp = GetClientIp(context);
    if (!IsIpAllowed(clientIp))
    {
      Log.Warning("Blocked WebSocket request from unauthorized IP: {ClientIp}", clientIp);
      return;
    }

    // 认证检查
    var accessKey = context.Request.Headers["Authorization"].ToString();
    var authError = ValidateToken(accessKey);
    if (authError != null)
    {
      await SendMessageAsync(ws, new WsMessage
      {
        Type = "auth",
        Success = false,
        Error = authError
      }, CancellationToken.None);
      await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, authError, CancellationToken.None);
      return;
    }

    await SendMessageAsync(ws, new WsMessage
    {
      Type = "auth",
      Success = true,
    }, CancellationToken.None);

    Log.Debug("Client authenticated from {RemoteEndPoint}", clientIp);

    await ProcessAudioAsync(ws, CancellationToken.None);
  }

  private static string GetClientIp(HttpContext context)
  {
    var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrEmpty(forwarded))
    {
      var ip = forwarded.Split(',')[0].Trim();
      if (!string.IsNullOrEmpty(ip))
        return ip;
    }

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
  }

  private string? ValidateToken(string? token)
  {
    if (string.IsNullOrWhiteSpace(token))
      return "Missing token";
    var tokenByte = Encoding.UTF8.GetBytes(token);
    if (!CryptographicOperations.FixedTimeEquals(tokenByte, _token))
      return "Invalid token";
    return null;
  }

  private bool IsIpAllowed(string clientIp)
  {
    var allowedIps = _config.Security?.AllowedIps;
    if (allowedIps == null || allowedIps.Count == 0)
      return true;

    foreach (var allowed in allowedIps)
    {
      if (MatchIp(clientIp, allowed))
        return true;
    }

    return false;
  }

  private bool MatchIp(string clientIp, string pattern)
  {
    if (clientIp == pattern) return true;

    if (pattern.Contains('/'))
    {
      try
      {
        var parts = pattern.Split('/');
        var network = IPAddress.Parse(parts[0]);
        var prefixLength = int.Parse(parts[1]);
        if (IPAddress.TryParse(clientIp, out var clientAddr))
          return IsInSubnet(clientAddr, network, prefixLength);
      }
      catch
      {
        // ignored
      }
    }

    if (pattern.Contains('*'))
    {
      var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
      if (System.Text.RegularExpressions.Regex.IsMatch(clientIp, regex))
        return true;
    }

    return false;
  }

  private bool IsInSubnet(IPAddress clientIp, IPAddress network, int prefixLength)
  {
    var clientBytes = clientIp.GetAddressBytes();
    var networkBytes = network.GetAddressBytes();
    if (clientBytes.Length != networkBytes.Length)
      return false;

    int fullBytes = prefixLength / 8;
    int remainingBits = prefixLength % 8;

    for (int i = 0; i < fullBytes; i++)
    {
      if (clientBytes[i] != networkBytes[i])
        return false;
    }

    if (remainingBits > 0 && fullBytes < clientBytes.Length)
    {
      var mask = (byte)(0xFF << (8 - remainingBits));
      if ((clientBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
        return false;
    }

    return true;
  }

  private async Task ProcessAudioAsync(WebSocket ws, CancellationToken cancellationToken)
  {
    var acquired =
      await _connectionSemaphore.WaitAsync(TimeSpan.FromSeconds(_acquireTimeoutSeconds), cancellationToken);
    if (!acquired)
    {
      await SendMessageAsync(ws, new WsMessage
      {
        Type = "error",
        Success = false,
        Error = "Server at capacity, please retry later"
      }, CancellationToken.None);
      await ws.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "Capacity limit", CancellationToken.None);
      return;
    }

    var recognizerHandle = await AcquireRecognizerAsync(cancellationToken);
    if (recognizerHandle == null)
    {
      _connectionSemaphore.Release();
      await SendMessageAsync(ws, new WsMessage
      {
        Type = "error",
        Success = false,
        Error = "Failed to acquire ASR engine"
      }, CancellationToken.None);
      return;
    }

    var recognizer = recognizerHandle.Value.Recognizer;
    var isEmergency = recognizerHandle.Value.IsEmergency;

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

      Log.Debug("send done flag");
      await SendMessageAsync(ws, new WsMessage
      {
        Type = "done",
        Success = true
      }, cancellationToken);
    }
    finally
    {
      ReleaseRecognizer(recognizer, isEmergency);
      _connectionSemaphore.Release();
    }
  }

  private static float[] ConvertToFloat(byte[] data)
  {
    var samples = new float[data.Length / 2];
    for (int i = 0; i < samples.Length; i++)
      samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
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
      bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
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
