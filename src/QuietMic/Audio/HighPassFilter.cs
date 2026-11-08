namespace QuietMic.Audio;

/// <summary>
/// RBJ 二阶高通（默认两级级联），用来在降噪前去掉麦克风的直流和低频隆隆声。
/// 降噪模型在 100 Hz 以下几乎不衰减，残留的低频比语音部分还响，听起来就是“闷”和“嗡嗡”。
/// </summary>
public sealed class HighPassFilter
{
    private readonly Section[] _sections;

    public HighPassFilter(int sampleRate, double cutoff, int stages = 2, double q = 0.7071)
    {
        _sections = new Section[Math.Max(1, stages)];
        for (var i = 0; i < _sections.Length; i++)
        {
            _sections[i] = new Section(sampleRate, cutoff, q);
        }
    }

    public void Process(Span<float> samples)
    {
        foreach (var section in _sections)
        {
            section.Process(samples);
        }
    }

    private struct Section
    {
        private readonly float _b0;
        private readonly float _b1;
        private readonly float _b2;
        private readonly float _a1;
        private readonly float _a2;
        private float _x1;
        private float _x2;
        private float _y1;
        private float _y2;

        public Section(int sampleRate, double cutoff, double q)
        {
            var w0 = 2 * Math.PI * cutoff / sampleRate;
            var alpha = Math.Sin(w0) / (2 * q);
            var cos = Math.Cos(w0);
            var a0 = 1 + alpha;

            _b0 = (float)((1 + cos) / 2 / a0);
            _b1 = (float)(-(1 + cos) / a0);
            _b2 = _b0;
            _a1 = (float)(-2 * cos / a0);
            _a2 = (float)((1 - alpha) / a0);
            _x1 = _x2 = _y1 = _y2 = 0;
        }

        public void Process(Span<float> samples)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var x = samples[i];
                var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1;
                _x1 = x;
                _y2 = _y1;
                _y1 = y;
                samples[i] = y;
            }
        }
    }
}
