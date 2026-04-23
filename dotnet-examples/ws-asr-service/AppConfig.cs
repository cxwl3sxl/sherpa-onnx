namespace WsAsrService;

public class AppConfig
{
  public ServerConfig Server { get; set; } = new();
  public AuthConfig Auth { get; set; } = new();
  public ModelConfig Model { get; set; } = new();
  public AudioConfig Audio { get; set; } = new();
  public LoggingConfig Logging { get; set; } = new();
}
