using NAudio.Wave;

namespace QuietMic.Audio;

/// <summary>把单声道复制到目标声道的所有声道（前两个声道为主）。</summary>
internal sealed class UpmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _scratch = Array.Empty<float>();

    public UpmixSampleProvider(ISampleProvider source, int channels)
    {
        _source = source;
        _channels = Math.Max(1, channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, _channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / _channels;
        if (_scratch.Length < frames)
        {
            _scratch = new float[frames];
        }

        var read = _source.Read(_scratch, 0, frames);
        for (var frame = 0; frame < read; frame++)
        {
            var sample = _scratch[frame];
            var baseIndex = offset + frame * _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                buffer[baseIndex + channel] = channel < 2 ? sample : 0f;
            }
        }

        return read * _channels;
    }
}
