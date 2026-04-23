using SherpaOnnx;

namespace WsAsrService;

/// <summary>
/// 识别器包装类
/// </summary>
internal readonly struct RecognizerHandle(OfflineRecognizer recognizer, bool isEmergency)
{
  public OfflineRecognizer Recognizer { get; } = recognizer;
  public bool IsEmergency { get; } = isEmergency;
}
