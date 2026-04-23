using SherpaOnnx;

namespace WsAsrService;

/// <summary>
/// 识别器包装类，用于追踪实例来源
/// </summary>
internal readonly struct RecognizerHandle
{
  public OfflineRecognizer Recognizer { get; }
  public bool IsEmergency { get; }

  public RecognizerHandle(OfflineRecognizer recognizer, bool isEmergency)
  {
    Recognizer = recognizer;
    IsEmergency = isEmergency;
  }
}
