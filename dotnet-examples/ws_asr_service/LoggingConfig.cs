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
  /// 保留最近几天的日志文件
  /// </summary>
  public int RetainedDayCount { get; set; } = 7;
}
