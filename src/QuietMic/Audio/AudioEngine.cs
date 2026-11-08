using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using QuietMic.Denoise;

namespace QuietMic.Audio;

/// <summary>
/// 采集 → 降噪 → 虚拟声卡。采集在设备混音格式下进行，内部统一转为 48 kHz 单声道浮点。
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int ProcessingRate = 48_000;

    private readonly IDenoiser _denoiser;
    private readonly float _outputGain;
    private readonly HighPassFilter _highPass = new(ProcessingRate, 90);
    private WasapiCapture? _capture;
    private MMDevice? _micDevice;
    private BufferedWaveProvider? _captureBuffer;
    private ISampleProvider? _captureChain;
    private DeviceSink? _output;
    private DeviceSink? _monitor;
    private Thread? _worker;
    private volatile bool _running;
    private float _wet = 1f;
    private float _inputSmooth;
    private float _outputSmooth;
    private long _lastLevelTicks;

    public AudioEngine(IDenoiser denoiser, float outputGain = 1f)
    {
        _denoiser = denoiser;
        _outputGain = outputGain;
    }

    public event Action<double>? InputLevelChanged;

    public event Action<double>? OutputLevelChanged;

    public event Action<Exception>? Faulted;

    public bool Bypass { get; set; }

    public float Wet
    {
        get => _wet;
        set => _wet = Math.Clamp(value, 0f, 1f);
    }

    public void Start(string microphoneId, string renderId, string? monitorId)
    {
        using var enumerator = new MMDeviceEnumerator();
        _micDevice = enumerator.GetDevice(microphoneId);

        _capture = new WasapiCapture(_micDevice, true, 40);
        _captureBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(400)
        };

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        ISampleProvider chain = _captureBuffer.ToSampleProvider();
        chain = new DownmixMonoSampleProvider(chain);
        if (_capture.WaveFormat.SampleRate != ProcessingRate)
        {
            chain = new WdlResamplingSampleProvider(chain, ProcessingRate);
        }

        _captureChain = chain;

        _output = new DeviceSink(DeviceList.Open(renderId));
        if (!string.IsNullOrEmpty(monitorId))
        {
            _monitor = new DeviceSink(DeviceList.Open(monitorId));
        }

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "QuietMic processor",
            Priority = ThreadPriority.AboveNormal
        };

        _running = true;
        _worker.Start();
        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _captureBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && _running)
        {
            Faulted?.Invoke(e.Exception);
        }
    }

    private void WorkerLoop()
    {
        var hop = _denoiser.HopSize;
        var dry = new float[hop];
        var wetFrame = new float[hop];
        var mixed = new float[hop];
        var faultCount = 0;

        while (_running)
        {
            var filled = 0;
            while (filled < hop && _running)
            {
                var read = _captureChain!.Read(dry, filled, hop - filled);
                if (read <= 0)
                {
                    Thread.Sleep(2);
                }
                else
                {
                    filled += read;
                }
            }

            if (filled < hop)
            {
                break;
            }

            _highPass.Process(dry);

            try
            {
                _denoiser.Process(dry, wetFrame);
                faultCount = 0;
            }
            catch (Exception exception)
            {
                Array.Copy(dry, wetFrame, hop);
                if (faultCount++ % 50 == 0)
                {
                    Faulted?.Invoke(exception);
                }
            }

            var wet = Bypass ? 0f : _wet;
            for (var i = 0; i < hop; i++)
            {
                mixed[i] = Math.Clamp(dry[i] + (wetFrame[i] * _outputGain - dry[i]) * wet, -1f, 1f);
            }

            _output?.Write(mixed, hop);
            _monitor?.Write(mixed, hop);

            PublishLevels(dry, mixed, hop);
        }
    }

    private void PublishLevels(float[] input, float[] output, int count)
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastLevelTicks < Stopwatch.Frequency / 30)
        {
            return;
        }

        _lastLevelTicks = now;

        _inputSmooth = Smooth(_inputSmooth, ToLevel(input, count));
        _outputSmooth = Smooth(_outputSmooth, ToLevel(output, count));

        InputLevelChanged?.Invoke(_inputSmooth);
        OutputLevelChanged?.Invoke(_outputSmooth);
    }

    private static float Smooth(float current, float target)
    {
        var factor = target > current ? 0.6f : 0.12f;
        return current + (target - current) * factor;
    }

    private static float ToLevel(float[] samples, int count)
    {
        var sum = 0.0;
        for (var i = 0; i < count; i++)
        {
            sum += samples[i] * samples[i];
        }

        var rms = Math.Sqrt(sum / count);
        var decibels = 20 * Math.Log10(rms + 1e-9);
        return (float)Math.Clamp((decibels + 60) / 60, 0, 1);
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _worker?.Join(1500);
        _worker = null;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                _capture.StopRecording();
            }
            catch
            {
                // 设备可能已消失
            }

            _capture.Dispose();
            _capture = null;
        }

        _captureChain = null;
        _captureBuffer = null;
        _micDevice?.Dispose();
        _micDevice = null;

        _output?.Dispose();
        _output = null;
        _monitor?.Dispose();
        _monitor = null;

        _inputSmooth = 0;
        _outputSmooth = 0;
    }

    public void Dispose()
    {
        Stop();
        _denoiser.Dispose();
    }
}
