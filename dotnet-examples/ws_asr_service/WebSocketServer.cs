using System.Collections.Generic;
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
  #region private fileds

  private readonly AppConfig _config;
  private readonly OfflineRecognizerConfig _recognizerConfig;
  private readonly VadModelConfig _vadConfig;
  private readonly Channel<OfflineRecognizer> _recognizerPool;
  private readonly SemaphoreSlim _connectionSemaphore;
  private readonly int _poolSize;
  private readonly int _acquireTimeoutSeconds;
  private readonly int _maxEmergencyInstances;
  private readonly byte[] _token;

  private IHost? _host;
  private int _emergencyInstances;
  private int _activeConnections;
  private long _totalRequests;

  // 识别器登记表：所有创建过的实例都注册在此，StopAsync 统一释放。
  // _allRecognizers 会被并发连接（紧急实例）与关机线程同时访问，必须加锁。
  private readonly object _recognizerRegistryLock = new();
  private readonly List<OfflineRecognizer> _allRecognizers = new();
  // 已释放实例集合：保证同一识别器只 Dispose 一次，
  // 避免关机清理与在途的 fire-and-forget 释放并发时对原生对象双重释放。
  private readonly HashSet<OfflineRecognizer> _disposedRecognizers = new();

  private const string EndMarker = "1049712a-2b0c-4be5-8c36-573e8a40f6d5";
  private static readonly byte[] EndMarkerBytes = ParseEndMarker();

  /// <summary>单条 WebSocket 消息的最大字节数，防止异常客户端耗尽内存。</summary>
  private const int MaxMessageBytes = 8 * 1024 * 1024;

  #endregion

  #region public props


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

  #endregion

  #region ctor

  public WebSocketServer(AppConfig config)
  {
    _token = Encoding.UTF8.GetBytes($"Bearer {config.Auth.Token}");
    _config = config;
    _recognizerConfig = CreateRecognizerConfig(config);
    _vadConfig = CreateVadConfig(config);
    _poolSize = config.Server.MaxConcurrency > 0 ? config.Server.MaxConcurrency : 4;
    _acquireTimeoutSeconds = config.Server.AcquireTimeoutSeconds > 0 ? config.Server.AcquireTimeoutSeconds : 30;
    _maxEmergencyInstances = Math.Max(1, _poolSize / 2);

    _connectionSemaphore = new SemaphoreSlim(_poolSize, _poolSize);
    _recognizerPool = Channel.CreateBounded<OfflineRecognizer>(new BoundedChannelOptions(_poolSize)
    {
      SingleReader = false,
      SingleWriter = false
    });
  }

  #endregion

  #region load configs

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
    // Silero VAD 模型 native 层固定要求 16000 Hz
    var vadConfig = new VadModelConfig
    {
      SampleRate = 16000
    };
    vadConfig.SileroVad.Model = config.Model.Vad;
    vadConfig.SileroVad.Threshold = 0.3f;
    vadConfig.SileroVad.MinSilenceDuration = 0.5f;
    vadConfig.SileroVad.MinSpeechDuration = 0.25f;
    vadConfig.SileroVad.MaxSpeechDuration = 5.0f;
    vadConfig.SileroVad.WindowSize = 512;
    vadConfig.Debug = 0;
    return vadConfig;
  }

  #endregion

  #region start&stop

  public async Task StartAsync(CancellationToken cancellationToken)
  {
    // 初始化模型池
    for (int i = 0; i < _poolSize; i++)
    {
      var recognizer = new OfflineRecognizer(_recognizerConfig);
      RegisterRecognizer(recognizer);
      _recognizerPool.Writer.TryWrite(recognizer);
      Log.Debug("Recognizer instance {Index}/{PoolSize} initialized", i + 1, _poolSize);
    }

    Log.Information("ASR model pool initialized with {PoolSize} instances", _poolSize);

    // 创建 WebApplication
    var builder = WebApplication.CreateBuilder();

    // 配置 Kestrel
    builder.WebHost.ConfigureKestrel(webHostOptions =>
    {
      // 遵循 server.host 配置的实际监听地址（0.0.0.0/*/空 → 全网卡）
      var listenAddress = ResolveListenAddress(_config.Server.Host);

      if (_config.Server.SslEnabled
          && !string.IsNullOrEmpty(_config.Server.SslCertPath)
          && File.Exists(_config.Server.SslCertPath))
      {
        var cert = new X509Certificate2(
          _config.Server.SslCertPath,
          _config.Server.SslCertPassword,
          X509KeyStorageFlags.MachineKeySet);

        webHostOptions.Listen(listenAddress, _config.Server.Port, listenOptions => listenOptions.UseHttps(cert));
        Log.Information("Kestrel listening on https://{ListenAddress}:{Port}/", listenAddress, _config.Server.Port);
      }
      else
      {
        webHostOptions.Listen(listenAddress, _config.Server.Port);
        Log.Information("Kestrel listening on http://{ListenAddress}:{Port}/", listenAddress, _config.Server.Port);
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

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    Log.Information("WebSocket server stopped");

    // 先关闭池：此后在途连接的释放路径无法再把识别器写回池中，只能走 Dispose 路径，
    // 从而保证所有识别器最终都会被 DisposeRecognizerOnce 释放
    _recognizerPool.Writer.TryComplete();

    List<OfflineRecognizer> snapshot;
    lock (_recognizerRegistryLock)
    {
      snapshot = new List<OfflineRecognizer>(_allRecognizers);
      _allRecognizers.Clear();
    }

    foreach (var recognizer in snapshot)
    {
      DisposeRecognizerOnce(recognizer);
    }

    Log.Information("All {Count} recognizer resources cleaned up", snapshot.Count);
    await Task.CompletedTask;
  }

  #endregion

  #region web api -> stats

  private async Task HandleStatsAsync(HttpContext context)
  {
    if (!await IsValidRequestIp(context)) return;

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

    await WriteJson(context, statsData);
  }

  #endregion

  #region web api -> health

  private async Task HandleHealthAsync(HttpContext context)
  {
    if (!await IsValidRequestIp(context)) return;

    var process = Process.GetCurrentProcess();
    var health = new
    {
      status = "healthy",
      timestamp = DateTime.UtcNow.ToString("o"),
      processUptime = (DateTime.Now - process.StartTime).ToString(@"dd\:hh\:mm\:ss"),
    };
    await WriteJson(context, health);
  }

  #endregion

  #region web api -> share

  async ValueTask<bool> IsValidRequestIp(HttpContext context)
  {
    // IP 白名单检查
    var clientIp = GetClientIp(context);
    if (!IsIpAllowed(clientIp))
    {
      Log.Warning("Blocked web api request from unauthorized IP: {ClientIp}", clientIp);
      context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
      await context.Response.CompleteAsync();
      return false;
    }

    return true;
  }

  async ValueTask WriteJson<T>(HttpContext context, T data)
  {
    context.Response.ContentType = "application/json";
    var jsonString = JsonSerializer.Serialize(data);
    var buffer = Encoding.UTF8.GetBytes(jsonString);
    context.Response.ContentLength = buffer.Length;
    await context.Response.Body.WriteAsync(buffer);
    await context.Response.CompleteAsync();
  }

  #endregion

  #region ws core

  private async Task HandleWebSocketAsync(HttpContext context)
  {
    var ws = await context.WebSockets.AcceptWebSocketAsync();

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

    Log.Debug("Client authenticated from {RemoteEndPoint}", GetClientIp(context));

    // 从连接参数中获取采样率，默认 16000
    var sampleRate = 16000;
    var sampleRateStr = context.Request.Query["sample_rate"].FirstOrDefault();
    if (!string.IsNullOrEmpty(sampleRateStr))
    {
      int.TryParse(sampleRateStr, out sampleRate);
    }

    // 校验采样率是否在支持范围内
    if (sampleRate is < 8000 or > 48000)
    {
      Log.Warning("Client specified unsupported sample rate: {SampleRate}", sampleRate);
      await SendMessageAsync(ws, new WsMessage
      {
        Type = "auth",
        Success = false,
        Error = $"Unsupported sample rate: {sampleRate}. Supported range: 8000-48000 Hz"
      }, CancellationToken.None);
      await ws.CloseAsync(WebSocketCloseStatus.ProtocolError,
        $"Unsupported sample rate: {sampleRate}", CancellationToken.None);
      return;
    }

    Log.Debug("Client sample rate: {SampleRate} Hz", sampleRate);

    // 使用请求中止 token：客户端断开时能及时取消等待与收发，而不是白等满超时时长
    await ProcessAudioAsync(ws, sampleRate, context.RequestAborted);
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

    private async Task ProcessAudioAsync(WebSocket ws, int sampleRate, CancellationToken cancellationToken)
    {
      RecognizerHandle? recognizerHandle = null;
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

      try
      {
        recognizerHandle = await AcquireRecognizerAsync(cancellationToken);
        if (recognizerHandle == null)
        {
          // 获取失败（引擎不足且紧急实例配额耗尽）：向客户端明确报错后再结束
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
        var connectionClosed = false;

        // VAD 模型固定 16000 Hz，客户端音频按需重采样。
        // using 确保 VAD 的原生内存（约 60s 缓冲）随连接结束立即释放，而不是等 GC finalizer
        using var vad = new VoiceActivityDetector(_vadConfig, 60);
        // 非 16kHz 输入时使用流式抗混叠重采样器（跨块保持相位连续）
        var resampler = sampleRate != 16000 ? new StreamingResampler(sampleRate, 16000) : null;
        var buffer = new byte[4096];
        // 注意：中转服务器/真实客户端可能以 endOfMessage=false 的连续分片流式发送音频
        //（结束标记作为最后一个分片），因此不能等待 EndOfMessage 才处理音频。
        // 方案：逐分片立即送入 VAD；同时保留末尾 EndMarkerBytes.Length 个字节
        // （偶数，不破坏 PCM 16-bit 采样对齐），用于检测跨分片的结束标记。
        var message = new List<byte>(EndMarkerBytes.Length * 2);

        while (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent)
        {
          var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
          if (result.MessageType == WebSocketMessageType.Close)
          {
            // 客户端主动关闭连接，完成关闭握手
            Log.Debug("Client initiated close, completing handshake");
            if (ws.State == WebSocketState.CloseSent)
            {
              await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }

            connectionClosed = true;
            break;
          }

          if (result.MessageType != WebSocketMessageType.Binary)
          {
            Log.Warning("Received non-binary message type: {MessageType}, closing connection", result.MessageType);
            await ws.CloseOutputAsync(WebSocketCloseStatus.ProtocolError, "Binary data required", CancellationToken.None);
            return;
          }

          if (message.Count + result.Count > MaxMessageBytes)
          {
            Log.Warning("Received message exceeds {MaxBytes} bytes, closing connection", MaxMessageBytes);
            await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
            return;
          }

          message.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

          // 累积窗口的尾部（含跨分片拼接）是否为结束标记
          if (message.Count >= EndMarkerBytes.Length
              && message.GetRange(message.Count - EndMarkerBytes.Length, EndMarkerBytes.Length)
                        .SequenceEqual(EndMarkerBytes))
          {
            Log.Debug("Received end marker, processing audio...");
            // 标记前的残留音频（跨分片场景）先送入 VAD，再退出循环触发 flush
            FeedAudioToVad(vad, resampler, message.GetRange(0, message.Count - EndMarkerBytes.Length).ToArray());
            break;
          }

          // 保留末尾 EndMarkerBytes.Length 字节（可能是标记前缀），其余立即送入 VAD
          var feedCount = message.Count - EndMarkerBytes.Length;
          if (feedCount > 0)
          {
            var audioBytes = message.GetRange(0, feedCount).ToArray();
            message.RemoveRange(0, feedCount);
            FeedAudioToVad(vad, resampler, audioBytes);
          }

          while (!vad.IsEmpty())
          {
            var segment = vad.Front();
            // VAD 输出已为 16000 Hz，识别器同样使用 16000 Hz
            var text = RecognizeSegment(recognizer, segment.Samples, 16000);
            if (!string.IsNullOrEmpty(text))
            {
              var startMs = (long)(segment.Start * 1000.0 / 16000);
              var endMs = (long)((segment.Start + segment.Samples.Length) * 1000.0 / 16000);
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

        // 客户端已主动关闭：剩余未成段的音频随连接终止，跳过识别与发送，
        // 避免向已关闭的 socket 写数据导致异常
        if (connectionClosed || ws.State != WebSocketState.Open)
        {
          return;
        }

        vad.Flush();
        while (!vad.IsEmpty())
        {
          var segment = vad.Front();
          var text = RecognizeSegment(recognizer, segment.Samples, 16000);
          if (!string.IsNullOrEmpty(text))
          {
            var startMs = (long)(segment.Start * 1000.0 / 16000);
            var endMs = (long)((segment.Start + segment.Samples.Length) * 1000.0 / 16000);
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
        // 先释放信号量，确保新连接不会被阻塞
        _connectionSemaphore.Release();
        // 异步清理识别器资源，不阻塞 finally 块
        if (recognizerHandle != null)
        {
          _ = ReleaseRecognizerAsync(recognizerHandle.Value.Recognizer, recognizerHandle.Value.IsEmergency);
        }
      }
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
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        // 连接已被取消（如客户端断开）：直接放弃，不为已断开的连接创建紧急实例
        Log.Debug("Recognizer acquire cancelled by client disconnect");
        return null;
      }
      catch (OperationCanceledException)
      {
        Log.Warning("Recognizer acquire timeout, creating emergency instance");
        return TryCreateEmergencyRecognizer();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Recognizer acquire failed");
        return null;
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
      RegisterRecognizer(recognizer);
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

  private static byte[] ParseEndMarker()
  {
    var hex = EndMarker.Replace("-", "");
    var bytes = new byte[hex.Length / 2];
    for (var i = 0; i < bytes.Length; i++)
      bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
    return bytes;
  }

  /// <summary>
  /// 解析 server.host 为实际监听地址。
  /// 0.0.0.0/*/空 → 全网卡；:: → IPv6 全网卡；合法 IP → 指定地址；
  /// localhost → 仅回环；其余回退全网卡并告警。
  /// </summary>
  private static IPAddress ResolveListenAddress(string? host)
  {
    if (string.IsNullOrWhiteSpace(host) || host is "0.0.0.0" or "*" or "+")
    {
      return IPAddress.Any;
    }

    if (host == "::")
    {
      return IPAddress.IPv6Any;
    }

    if (IPAddress.TryParse(host, out var address))
    {
      return address;
    }

    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
    {
      return IPAddress.Loopback;
    }

    Log.Warning("Unsupported server.host '{Host}', falling back to 0.0.0.0", host);
    return IPAddress.Any;
  }

  private void RegisterRecognizer(OfflineRecognizer recognizer)
  {
    // 紧急实例在多个并发连接中创建，_allRecognizers 必须加锁访问
    lock (_recognizerRegistryLock)
    {
      _allRecognizers.Add(recognizer);
    }
  }

  /// <summary>
  /// 释放识别器，保证同一实例只会被 Dispose 一次，
  /// 避免关机清理（StopAsync）与在途的异步释放并发时对原生对象双重释放。
  /// </summary>
  private void DisposeRecognizerOnce(OfflineRecognizer recognizer)
  {
    lock (_recognizerRegistryLock)
    {
      if (!_disposedRecognizers.Add(recognizer))
      {
        return;
      }
    }

    try
    {
      recognizer.Dispose();
      Log.Debug("Disposed recognizer");
    }
    catch (Exception ex)
    {
      Log.Warning(ex, "Failed to dispose recognizer");
    }
  }

  private Task ReleaseRecognizerAsync(OfflineRecognizer recognizer, bool isEmergency)
  {
    Interlocked.Decrement(ref _activeConnections);
    Interlocked.Increment(ref _totalRequests);

    if (isEmergency)
    {
      Interlocked.Decrement(ref _emergencyInstances);
    }

    if (_recognizerPool.Writer.TryWrite(recognizer))
    {
      Log.Debug("Recognizer released to pool. Active: {Active}, Total: {Total}", _activeConnections, _totalRequests);
      return Task.CompletedTask;
    }

    // 池不可写（已满或服务停止中）：异步释放资源，不阻塞 finally 块。
    // DisposeRecognizerOnce 保证与 StopAsync 的清理不会双重释放同一实例
    return Task.Run(() => DisposeRecognizerOnce(recognizer));
  }

  private static float[] ConvertToFloat(byte[] data)
  {
    var samples = new float[data.Length / 2];
    for (int i = 0; i < samples.Length; i++)
      samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
    return samples;
  }

  /// <summary>
  /// 将一段 PCM 字节送入 VAD（按需先重采样到 16000 Hz）。
  /// </summary>
  private static void FeedAudioToVad(VoiceActivityDetector vad, StreamingResampler? resampler, byte[] data)
  {
    if (data.Length == 0) return;

    var samples = ConvertToFloat(data);
    if (resampler != null)
      samples = resampler.Process(samples);
    vad.AcceptWaveform(samples);
  }

  private string RecognizeSegment(OfflineRecognizer recognizer, float[] samples, int sampleRate)
  {
    if (samples.Length == 0) return "";
    // using 确保流的原生内存在识别后立即释放，而不是等 GC finalizer
    using var stream = recognizer.CreateStream();
    stream.AcceptWaveform(sampleRate, samples);
    recognizer.Decode(stream);
    return stream.Result.Text ?? "";
  }

  private static async Task SendMessageAsync(WebSocket ws, WsMessage msg, CancellationToken ct)
  {
    var json = JsonSerializer.Serialize(msg);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
  }

  #endregion

  #region check ip

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

  #endregion

}
