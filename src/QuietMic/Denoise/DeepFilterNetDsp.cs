namespace QuietMic.Denoise;

/// <summary>
/// DeepFilterNet3 的 DSP 前后处理（移植自 libDF 的 DFState）：ERB 特征、特征归一化、
/// Vorbis 窗 STFT 与重叠相加。比 DPDFNet 多出 ERB 掩码和深度滤波系数两步。
/// </summary>
internal sealed class DeepFilterNetDsp
{
    public const int SampleRate = 48_000;
    public const int FrameSize = 480;
    public const int WindowLength = 960;
    public const int BinCount = WindowLength / 2 + 1;
    public const int ErbBands = 32;
    public const int MinErbFreqs = 2;
    public const int DfOrder = 5;
    public const int DfBins = 96;

    private const float Alpha = 0.99f;

    private readonly int[] _erb;
    private readonly float[] _window;
    private readonly float _windowNorm;
    private readonly float[] _analysisMemory = new float[WindowLength - FrameSize];
    private readonly float[] _synthesisMemory = new float[WindowLength - FrameSize];
    private readonly float[][] _historyRe;
    private readonly float[][] _historyIm;
    private readonly float[] _currentRe = new float[BinCount];
    private readonly float[] _currentIm = new float[BinCount];
    private readonly float[] _re = new float[WindowLength];
    private readonly float[] _im = new float[WindowLength];
    private readonly float[] _normalizedRe = new float[BinCount];
    private readonly float[] _normalizedIm = new float[BinCount];
    private readonly float[] _meanNormState = new float[ErbBands];
    private readonly float[] _unitNormState = new float[BinCount];
    private readonly float[] _power = new float[BinCount];
    private readonly BluesteinFft _fft = new(WindowLength);

    public DeepFilterNetDsp()
    {
        _erb = BuildErb();
        _window = new float[WindowLength];
        var half = WindowLength / 2f;
        for (var i = 0; i < WindowLength; i++)
        {
            var sin = MathF.Sin(0.5f * MathF.PI * (i + 0.5f) / half);
            _window[i] = MathF.Sin(0.5f * MathF.PI * sin * sin);
        }

        // libDF: wnorm = 1 / (window_size^2 / (2 * frame_size))
        _windowNorm = 1f / (WindowLength * WindowLength / (2f * FrameSize));

        _historyRe = new float[DfOrder][];
        _historyIm = new float[DfOrder][];
        for (var i = 0; i < DfOrder; i++)
        {
            _historyRe[i] = new float[BinCount];
            _historyIm[i] = new float[BinCount];
        }

        Array.Fill(_meanNormState, -60f);
        Array.Fill(_unitNormState, 0.001f);
    }

    public void Reset()
    {
        Array.Clear(_analysisMemory);
        Array.Clear(_synthesisMemory);
        Array.Clear(_currentRe);
        Array.Clear(_currentIm);
        foreach (var frame in _historyRe)
        {
            Array.Clear(frame);
        }

        foreach (var frame in _historyIm)
        {
            Array.Clear(frame);
        }

        Array.Fill(_meanNormState, -60f);
        Array.Fill(_unitNormState, 0.001f);
    }

    public void ExtractFeatures(ReadOnlySpan<float> input, Span<float> featErb, Span<float> featSpec)
    {
        // 分析：前半窗作用于上一帧，后半窗作用于当前帧。
        for (var i = 0; i < FrameSize; i++)
        {
            _re[i] = _analysisMemory[i] * _window[i];
            _im[i] = 0f;
            _re[FrameSize + i] = input[i] * _window[FrameSize + i];
            _im[FrameSize + i] = 0f;
        }

        input.CopyTo(_analysisMemory);

        _fft.Forward(_re, _im);
        for (var i = 0; i < BinCount; i++)
        {
            _re[i] *= _windowNorm;
            _im[i] *= _windowNorm;
        }

        for (var i = DfOrder - 1; i >= 1; i--)
        {
            Array.Copy(_historyRe[i - 1], _historyRe[i], BinCount);
            Array.Copy(_historyIm[i - 1], _historyIm[i], BinCount);
        }

        Array.Copy(_re, _currentRe, BinCount);
        Array.Copy(_im, _currentIm, BinCount);
        Array.Copy(_currentRe, _historyRe[0], BinCount);
        Array.Copy(_currentIm, _historyIm[0], BinCount);

        for (var bin = 0; bin < BinCount; bin++)
        {
            _power[bin] = _currentRe[bin] * _currentRe[bin] + _currentIm[bin] * _currentIm[bin];
        }

        BandCompress(featErb, _power);
        for (var band = 0; band < ErbBands; band++)
        {
            featErb[band] = MathF.Log10(featErb[band] + 1e-10f) * 10f;
        }

        // 指数均值归一化
        for (var band = 0; band < ErbBands; band++)
        {
            var state = featErb[band] * (1f - Alpha) + _meanNormState[band] * Alpha;
            _meanNormState[band] = state;
            featErb[band] = (featErb[band] - state) / 40f;
        }

        Array.Copy(_currentRe, _normalizedRe, BinCount);
        Array.Copy(_currentIm, _normalizedIm, BinCount);
        for (var bin = 0; bin < BinCount; bin++)
        {
            var state = MathF.Sqrt(_normalizedRe[bin] * _normalizedRe[bin] + _normalizedIm[bin] * _normalizedIm[bin])
                        * (1f - Alpha) + _unitNormState[bin] * Alpha;
            _unitNormState[bin] = state;
            var scale = 1f / MathF.Sqrt(state);
            _normalizedRe[bin] *= scale;
            _normalizedIm[bin] *= scale;
        }

        for (var bin = 0; bin < DfBins; bin++)
        {
            featSpec[bin] = _normalizedRe[bin];
            featSpec[DfBins + bin] = _normalizedIm[bin];
        }
    }

    public void Synthesize(ReadOnlySpan<float> mask, ReadOnlySpan<float> coefficients, Span<float> output)
    {
        ApplyErbMask(mask);
        ApplyDeepFilter(coefficients);

        Array.Clear(_re);
        Array.Clear(_im);
        Array.Copy(_currentRe, _re, BinCount);
        Array.Copy(_currentIm, _im, BinCount);
        for (var bin = 1; bin < BinCount - 1; bin++)
        {
            _re[WindowLength - bin] = _re[bin];
            _im[WindowLength - bin] = -_im[bin];
        }

        _fft.Inverse(_re, _im);

        var correction = (float)WindowLength;
        for (var i = 0; i < WindowLength; i++)
        {
            _re[i] *= _window[i] * correction;
        }

        for (var i = 0; i < FrameSize; i++)
        {
            output[i] = _re[i] + _synthesisMemory[i];
            _synthesisMemory[i] = _re[FrameSize + i];
        }
    }

    private void ApplyErbMask(ReadOnlySpan<float> mask)
    {
        var offset = 0;
        for (var band = 0; band < ErbBands; band++)
        {
            var size = _erb[band];
            var gain = mask[band];
            for (var i = offset; i < offset + size; i++)
            {
                _currentRe[i] *= gain;
                _currentIm[i] *= gain;
            }

            offset += size;
        }
    }

    private void ApplyDeepFilter(ReadOnlySpan<float> coefficients)
    {
        for (var bin = 0; bin < DfBins && bin < BinCount; bin++)
        {
            var enhancedRe = 0f;
            var enhancedIm = 0f;
            for (var order = 0; order < DfOrder; order++)
            {
                var index = (bin * DfOrder + order) * 2;
                var coefRe = coefficients[index];
                var coefIm = coefficients[index + 1];
                // libDF 的系数索引 0 对应最旧的帧，历史缓冲区 0 是最新帧。
                var historyRe = _historyRe[DfOrder - 1 - order][bin];
                var historyIm = _historyIm[DfOrder - 1 - order][bin];
                enhancedRe += historyRe * coefRe - historyIm * coefIm;
                enhancedIm += historyRe * coefIm + historyIm * coefRe;
            }

            _currentRe[bin] = enhancedRe;
            _currentIm[bin] = enhancedIm;
        }
    }

    private void BandCompress(Span<float> output, ReadOnlySpan<float> power)
    {
        output.Clear();
        var offset = 0;
        for (var band = 0; band < ErbBands; band++)
        {
            var size = _erb[band];
            var scale = 1f / size;
            var sum = 0f;
            for (var i = offset; i < offset + size; i++)
            {
                sum += power[i] * scale;
            }

            output[band] = sum;
            offset += size;
        }
    }

    private static int[] BuildErb()
    {
        const int sampleRate = SampleRate;
        const int fftSize = WindowLength;
        const int bands = ErbBands;
        const int minFreqs = MinErbFreqs;

        var frequencyWidth = sampleRate / (float)fftSize;
        var erbLow = FrequencyToErb(0f);
        var erbHigh = FrequencyToErb(sampleRate / 2f);
        var step = (erbHigh - erbLow) / bands;

        var result = new int[bands];
        var previous = 0;
        var overflow = 0;

        for (var i = 1; i <= bands; i++)
        {
            var frequency = ErbToFrequency(erbLow + i * step);
            var bin = (int)MathF.Round(frequency / frequencyWidth, MidpointRounding.AwayFromZero);
            var count = bin - previous - overflow;
            if (count < minFreqs)
            {
                overflow = minFreqs - count;
                count = minFreqs;
            }
            else
            {
                overflow = 0;
            }

            result[i - 1] = count;
            previous = bin;
        }

        result[bands - 1] += 1;
        var tooLarge = result.Sum() - (fftSize / 2 + 1);
        if (tooLarge > 0)
        {
            result[bands - 1] -= tooLarge;
        }

        return result;
    }

    private static float FrequencyToErb(float hertz)
    {
        return 9.265f * MathF.Log(1f + hertz / (24.7f * 9.265f));
    }

    private static float ErbToFrequency(float erb)
    {
        return 24.7f * 9.265f * (MathF.Exp(erb / 9.265f) - 1f);
    }
}
