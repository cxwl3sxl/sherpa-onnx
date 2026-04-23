namespace WsAsrService;

public class AudioConfig
{
  public int SampleRate { get; set; } = 16000;
  public int BitsPerSample { get; set; } = 16;
  public int Channels { get; set; } = 1;
  public int FrameSize { get; set; } = 1280;
  public string EndMarker { get; set; } = "255,255,255,255";
}
