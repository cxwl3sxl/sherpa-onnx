namespace WsAsrService;

public class ServerConfig
{
  public string Host { get; set; } = "0.0.0.0";
  public int Port { get; set; } = 8080;
  /// <summary>
  /// 最大并发识别数，默认为 4
  /// 建议：4-8 核 CPU 设置 4-8，16+ 核可设置 8-16
  /// </summary>
  public int MaxConcurrency { get; set; } = 4;
  /// <summary>
  /// 获取识别器超时时间（秒），默认 30
  /// </summary>
  public int AcquireTimeoutSeconds { get; set; } = 30;
}
