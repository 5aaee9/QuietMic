using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace QuietMic.Audio;

/// <summary>
/// 一个输出端点：接收 48 kHz 单声道浮点音频，按设备混音格式编码后播放。
/// 目标设备（虚拟声卡或耳机）使用共享模式，因此必须严格遵守其混音格式。
/// </summary>
public sealed class DeviceSink : IDisposable
{
    private const int SourceRate = 48_000;
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly BufferedWaveProvider _sourceBuffer;
    private readonly BufferedWaveProvider _playBuffer;
    private readonly ISampleProvider _converted;
    private readonly WaveFormat _mixFormat;
    private readonly Thread _pump;
    private readonly int _bytesPerSample;
    private readonly SampleEncoding _encoding;
    private readonly int _frameSamples;
    private volatile bool _running = true;
    private byte[] _floatBytes = Array.Empty<byte>();

    public DeviceSink(MMDevice device)
    {
        _device = device;
        _mixFormat = device.AudioClient.MixFormat;
        _encoding = DetectEncoding(_mixFormat);
        _bytesPerSample = Math.Max(1, _mixFormat.BitsPerSample / 8);
        _frameSamples = _mixFormat.SampleRate / 100; // 10 ms

        _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 60);

        _sourceBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SourceRate, 1))
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(500)
        };

        ISampleProvider chain = _sourceBuffer.ToSampleProvider();
        chain = new UpmixSampleProvider(chain, _mixFormat.Channels);
        if (_mixFormat.SampleRate != SourceRate)
        {
            chain = new WdlResamplingSampleProvider(chain, _mixFormat.SampleRate);
        }

        _converted = chain;

        _playBuffer = new BufferedWaveProvider(_mixFormat)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(500)
        };

        _output.Init(_playBuffer);
        _output.Play();

        _pump = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "QuietMic sink pump",
            Priority = ThreadPriority.AboveNormal
        };
        _pump.Start();
    }

    public string DeviceName => _device.FriendlyName;

    public long PumpedSamples;

    public Exception? PumpError;

    public long PendingBytes => _playBuffer.BufferedBytes;

    public void Write(float[] samples, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var bytes = count * sizeof(float);
        if (_floatBytes.Length < bytes)
        {
            _floatBytes = new byte[bytes];
        }

        Buffer.BlockCopy(samples, 0, _floatBytes, 0, bytes);
        _sourceBuffer.AddSamples(_floatBytes, 0, bytes);
    }

    private void PumpLoop()
    {
        var channels = _mixFormat.Channels;
        var floatBuffer = new float[_frameSamples * channels];
        var byteBuffer = new byte[_frameSamples * channels * _bytesPerSample];

        while (_running)
        {
            try
            {
                var read = _converted.Read(floatBuffer, 0, floatBuffer.Length);
                if (read <= 0)
                {
                    Thread.Sleep(2);
                    continue;
                }

                PumpedSamples += read;
                var encoded = Encode(floatBuffer, read, byteBuffer);
                _playBuffer.AddSamples(byteBuffer, 0, encoded);

                // 自适应抖动缓冲：播放缓冲领先太多就等一等，避免把采样一次性抽干。
                var bufferedMs = _playBuffer.BufferedBytes * 1000 / Math.Max(1, _mixFormat.AverageBytesPerSecond);
                if (bufferedMs > 120)
                {
                    Thread.Sleep(8);
                }
            }
            catch (Exception exception)
            {
                PumpError = exception;
                Thread.Sleep(10);
            }
        }
    }

    private int Encode(float[] samples, int count, byte[] destination)
    {
        var offset = 0;
        switch (_encoding)
        {
            case SampleEncoding.Float32:
                for (var i = 0; i < count; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(destination.AsSpan(offset), Math.Clamp(samples[i], -1f, 1f));
                    offset += 4;
                }

                break;
            case SampleEncoding.Pcm16:
                for (var i = 0; i < count; i++)
                {
                    var value = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(offset), value);
                    offset += 2;
                }

                break;
            case SampleEncoding.Pcm24:
                for (var i = 0; i < count; i++)
                {
                    var value = (int)(Math.Clamp(samples[i], -1f, 1f) * 8_388_607f);
                    destination[offset] = (byte)value;
                    destination[offset + 1] = (byte)(value >> 8);
                    destination[offset + 2] = (byte)(value >> 16);
                    offset += 3;
                }

                break;
            case SampleEncoding.Pcm32:
                for (var i = 0; i < count; i++)
                {
                    var value = (int)(Math.Clamp(samples[i], -1f, 1f) * int.MaxValue);
                    BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset), value);
                    offset += 4;
                }

                break;
        }

        return offset;
    }

    private enum SampleEncoding
    {
        Float32,
        Pcm16,
        Pcm24,
        Pcm32
    }

    private static SampleEncoding DetectEncoding(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == IeeeFloatSubFormat)
            {
                return SampleEncoding.Float32;
            }

            if (extensible.SubFormat == PcmSubFormat)
            {
                return extensible.BitsPerSample switch
                {
                    16 => SampleEncoding.Pcm16,
                    24 => SampleEncoding.Pcm24,
                    _ => SampleEncoding.Pcm32
                };
            }
        }

        return format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => SampleEncoding.Float32,
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => SampleEncoding.Pcm16,
            WaveFormatEncoding.Pcm when format.BitsPerSample == 24 => SampleEncoding.Pcm24,
            _ => SampleEncoding.Pcm32
        };
    }

    public void Dispose()
    {
        _running = false;
        _pump.Join(500);
        try
        {
            _output.Stop();
        }
        catch
        {
            // 设备可能已被拔出
        }

        _output.Dispose();
        _device.Dispose();
    }
}
