namespace WsAsrService;

public class LoggingConfig
{
  /// <summary>
  /// 日志级别: Debug, Info, Warning, Error
  /// </summary>
  public string Level { get; set; } = "Info";

  /// <summary>
  /// 日志目录（相对于应用程序目录）
  /// </summary>
  public string LogDirectory { get; set; } = "logs";

  /// <summary>
  /// 日志文件名前缀
  /// </summary>
  public string LogFileNamePrefix { get; set; } = "ws-asr-service";

  /// <summary>
  /// 日志滚动间隔: Day（每天一个文件）
  /// </summary>
  public string RollingInterval { get; set; } = "Day";

  /// <summary>
  /// 保留最近几天的日志文件
  /// </summary>
  public int RetainedDayCount { get; set; } = 7;
}