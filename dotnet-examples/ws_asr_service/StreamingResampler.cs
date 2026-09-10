namespace WsAsrService;

/// <summary>
/// 流式抗混叠重采样器（Hann 窗 sinc 插值）。
///
/// 相比逐块独立的线性插值重采样：
/// 1. 跨块维护输入历史与分数相位，块边界处插值连续，消除首尾样本重复造成的周期性伪影；
/// 2. 降采样时把插值核的截止频率限制在目标奈奎斯特频率，提供抗混叠滤波。
///
/// 说明：
/// - 流结束后最多滞留 _radius 个输入样本（约几毫秒）不输出，对 ASR 识别无影响；
/// - 每个输出样本约需 2*_radius+1 次乘加与三角函数运算（_radius ≤ 64），
///   对 16kHz 输出的实时流开销可忽略；如需进一步优化可改为多相查找表。
/// </summary>
internal sealed class StreamingResampler
{
  private const int MaxKernelRadius = 64;

  // 源/目标采样率比 (>0)，也是相邻输出样本在输入坐标上的步长
  private readonly double _ratio;

  // 归一化截止频率 (0,1]，相对输入奈奎斯特频率；降采样时为 1/ratio
  private readonly double _cutoff;

  // 插值核半宽（以输入样本数计）
  private readonly int _radius;

  // 尚未被完全消费的输入样本（含为后续输出保留的历史窗口）
  private readonly List<float> _pending = new();

  // 下一个输出样本在 _pending 坐标系中的位置
  private double _position;

  public StreamingResampler(int srcSampleRate, int dstSampleRate)
  {
    _ratio = (double)srcSampleRate / dstSampleRate;
    // 降采样时把核的截止频率压到目标奈奎斯特频率，抑制混叠
    _cutoff = Math.Min(1.0, 1.0 / _ratio);
    _radius = Math.Clamp((int)Math.Ceiling(16.0 * Math.Max(1.0, _ratio)), 8, MaxKernelRadius);
  }

  /// <summary>
  /// 输入一块源采样率的样本，返回当前可产出的目标采样率样本（可能为空数组）。
  /// </summary>
  public float[] Process(float[] samples)
  {
    _pending.AddRange(samples);

    var estimatedCount = (int)((_pending.Count - _position) / _ratio) + 1;
    var output = new List<float>(Math.Max(0, estimatedCount));

    // 只要插值窗口的右缘已有数据就持续产出
    while (_position + _radius <= _pending.Count - 1)
    {
      output.Add(InterpolateAt(_position));
      _position += _ratio;
    }

    // 丢弃不再被后续输出窗口引用的历史样本
    var keepFrom = (int)Math.Floor(_position) - _radius;
    if (keepFrom > 0)
    {
      _pending.RemoveRange(0, Math.Min(keepFrom, _pending.Count));
      _position -= keepFrom;
    }

    return output.ToArray();
  }

  private float InterpolateAt(double position)
  {
    var lo = (int)Math.Ceiling(position - _radius);
    var hi = (int)Math.Floor(position + _radius);
    if (lo < 0) lo = 0;
    if (hi > _pending.Count - 1) hi = _pending.Count - 1;

    double sum = 0.0;
    for (var k = lo; k <= hi; k++)
    {
      var d = position - k;
      var sincArg = Math.PI * d * _cutoff;
      var sinc = sincArg == 0.0 ? 1.0 : Math.Sin(sincArg) / sincArg;
      var window = 0.5 * (1.0 + Math.Cos(Math.PI * d / _radius));
      sum += _pending[k] * sinc * window;
    }

    return (float)(_cutoff * sum);
  }
}
