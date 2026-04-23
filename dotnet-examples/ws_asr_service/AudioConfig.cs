namespace WsAsrService;

public class AudioConfig
{
  public int SampleRate { get; set; } = 16000;
  public string EndMarker { get; set; } = "255,255,255,255";
}
