namespace QuietMic.Denoise;

/// <summary>
/// DPDFNet 的前后处理：Vorbis 窗 STFT / 重叠相加 iSTFT。
/// 频点按 [re, im] 交错排列（window/2 + 1 个频点）。
/// </summary>
internal sealed class DpdfNetDsp
{
    public const int WindowLength = 960;
    public const int HopSize = 480;
    public const int BinCount = WindowLength / 2 + 1;
    public const int SpecLength = BinCount * 2;

    private readonly float[] _window;
    private readonly float[] _inputBuffer = new float[WindowLength];
    private readonly float[] _outputBuffer = new float[WindowLength];
    private readonly float[] _re = new float[WindowLength];
    private readonly float[] _im = new float[WindowLength];
    private readonly BluesteinFft _fft = new(WindowLength);

    public DpdfNetDsp()
    {
        _window = new float[WindowLength];
        var half = WindowLength / 2f;
        for (var i = 0; i < WindowLength; i++)
        {
            var sin = MathF.Sin(0.5f * MathF.PI * (i + 0.5f) / half);
            _window[i] = MathF.Sin(0.5f * MathF.PI * sin * sin);
        }
    }

    public void Reset()
    {
        Array.Clear(_inputBuffer);
        Array.Clear(_outputBuffer);
        Array.Clear(_re);
        Array.Clear(_im);
    }

    public void Analyze(ReadOnlySpan<float> hop, Span<float> spec)
    {
        Array.Copy(_inputBuffer, HopSize, _inputBuffer, 0, WindowLength - HopSize);
        hop.CopyTo(_inputBuffer.AsSpan(WindowLength - HopSize));

        for (var i = 0; i < WindowLength; i++)
        {
            _re[i] = _inputBuffer[i] * _window[i];
            _im[i] = 0f;
        }

        _fft.Forward(_re, _im);

        for (var bin = 0; bin < BinCount; bin++)
        {
            spec[bin * 2] = _re[bin];
            spec[bin * 2 + 1] = _im[bin];
        }
    }

    public void Synthesize(ReadOnlySpan<float> spec, Span<float> hop)
    {
        Array.Clear(_re);
        Array.Clear(_im);
        for (var bin = 0; bin < BinCount; bin++)
        {
            _re[bin] = spec[bin * 2];
            _im[bin] = spec[bin * 2 + 1];
        }

        for (var bin = 1; bin < WindowLength / 2; bin++)
        {
            _re[WindowLength - bin] = _re[bin];
            _im[WindowLength - bin] = -_im[bin];
        }

        _fft.Inverse(_re, _im);

        Array.Copy(_outputBuffer, HopSize, _outputBuffer, 0, WindowLength - HopSize);
        Array.Clear(_outputBuffer, WindowLength - HopSize, HopSize);

        for (var i = 0; i < WindowLength; i++)
        {
            _outputBuffer[i] += _re[i] * _window[i];
        }

        _outputBuffer.AsSpan(0, HopSize).CopyTo(hop);
    }
}
