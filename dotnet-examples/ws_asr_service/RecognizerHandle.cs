using System;
using SherpaOnnx;

namespace WsAsrService;

/// <summary>
/// 识别器包装类。
/// RequestId 为本次识别实例申请的申请编号（GUID），
/// 用于把“申请请求”与后续“释放请求”的日志通过同一编号关联起来。
/// </summary>
internal readonly struct RecognizerHandle(OfflineRecognizer recognizer, bool isEmergency, Guid requestId)
{
  public OfflineRecognizer Recognizer { get; } = recognizer;
  public bool IsEmergency { get; } = isEmergency;
  public Guid RequestId { get; } = requestId;
}