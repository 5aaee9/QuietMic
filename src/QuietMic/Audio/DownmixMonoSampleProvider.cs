using NAudio.Wave;

namespace QuietMic.Audio;

/// <summary>把任意声道数的浮点采样平均混成单声道。</summary>
internal sealed class DownmixMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _scratch = Array.Empty<float>();

    public DownmixMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var needed = count * _channels;
        if (_scratch.Length < needed)
        {
            _scratch = new float[needed];
        }

        var read = _source.Read(_scratch, 0, needed);
        var frames = read / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var baseIndex = frame * _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += _scratch[baseIndex + channel];
            }

            buffer[offset + frame] = sum / _channels;
        }

        return frames;
    }
}
