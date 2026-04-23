namespace WsAsrService;

public class WsMessage
{
  public string Type { get; set; } = "";
  public string? Content { get; set; }
  public bool Success { get; set; }
  public string? Error { get; set; }
}
