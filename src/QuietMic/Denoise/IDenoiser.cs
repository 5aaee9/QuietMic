namespace QuietMic.Denoise;

/// <summary>一帧（<see cref="HopSize"/> 个 48 kHz 采样）的降噪器。</summary>
public interface IDenoiser : IDisposable
{
    int HopSize { get; }

    /// <summary>处理一帧，把降噪结果写入 <paramref name="output"/>（与输入等长）。</summary>
    void Process(ReadOnlySpan<float> input, Span<float> output);

    void Reset();
}
