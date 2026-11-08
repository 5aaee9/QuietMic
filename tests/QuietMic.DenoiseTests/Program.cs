using System.Diagnostics;
using QuietMic.Denoise;
using QuietMic.DenoiseTests;

if (args.Length >= 3 && args[0] == "engine")
{
    using var denoiser = new QuietMic.Denoise.DpdfNetDenoiser(args[2]);
    using var engine = new QuietMic.Audio.AudioEngine(denoiser);
    engine.InputLevelChanged += v => Console.WriteLine($"in  {v:0.000}");
    engine.OutputLevelChanged += v => Console.WriteLine($"out {v:0.000}");
    engine.Faulted += e => Console.WriteLine("FAULT " + e.Message);
    engine.Start(QuietMic.Audio.DeviceList.Capture().First(d => d.Name.Contains(args[1])).Id, QuietMic.Audio.DeviceList.Render().First(d => d.Name.Contains("CABLE Input")).Id, null);
    Console.WriteLine("engine started");
    Thread.Sleep(int.Parse(args[3]) * 1000);
    return;
}

if (args.Length >= 3 && args[0] == "play")
{
    WavMode.Play(args[1], args[2]);
    return;
}

if (args.Length >= 3 && args[0] == "record")
{
    WavMode.Record(args[1], int.Parse(args[2]), args[3]);
    return;
}

if (args.Length >= 2 && args[0] == "sink")
{
    var device = QuietMic.Audio.DeviceList.Render().First(d => d.Name.Contains(args[1]));
    using var sink = new QuietMic.Audio.DeviceSink(QuietMic.Audio.DeviceList.Open(device.Id));
    var buffer = new float[480];
    for (var block = 0; block < 100; block++)
    {
        for (var i = 0; i < buffer.Length; i++) buffer[i] = 0.1f * MathF.Sin((block * 480 + i) * 2 * MathF.PI * 440 / 48000);
        sink.Write(buffer, buffer.Length);
        Thread.Sleep(10);
    }
    Console.WriteLine("sink ok: " + sink.DeviceName);
    return;
}

if (args.Length >= 2 && args[0] == "rename")
{
    var device = QuietMic.Audio.DeviceList.Capture().First(d => d.Matches("CABLE Output"));
    using var mm = QuietMic.Audio.DeviceList.Open(device.Id);
    // VT_LPWSTR
    var value = new NAudio.CoreAudioApi.Interfaces.PropVariant
    {
        vt = 31,
        pointerValue = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(args[1])
    };
    // NAudio 的 Properties 是只读存储，这里通过底层接口拿一个可写存储。
    var field = typeof(NAudio.CoreAudioApi.MMDevice)
        .GetField("deviceInterface", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    var open = field.FieldType.GetMethod("OpenPropertyStore")!;
    var openArgs = new object?[] { Enum.ToObject(open.GetParameters()[0].ParameterType, 2 /* ReadWrite */), null };
    open.Invoke(field.GetValue(mm), openArgs);
    var storeInterface = typeof(NAudio.CoreAudioApi.MMDevice).Assembly
        .GetType("NAudio.CoreAudioApi.Interfaces.IPropertyStore")!;
    var store = openArgs[1]!;
    storeInterface.GetMethod("SetValue", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(store, [new NAudio.CoreAudioApi.PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14), value]);
    storeInterface.GetMethod("Commit", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(store, null);
    Console.WriteLine("renamed to " + args[1]);
    return;
}

if (args.Length >= 1 && args[0] == "devices")
{
    Console.WriteLine("CAPTURE:");
    foreach (var d in QuietMic.Audio.DeviceList.Capture()) Console.WriteLine($"  {d.Name} | desc={d.Description} | adapter={d.Adapter} | default={d.IsDefault}");
    Console.WriteLine("RENDER:");
    foreach (var d in QuietMic.Audio.DeviceList.Render()) Console.WriteLine($"  {d.Name} | default={d.IsDefault}");
    var renamed = new QuietMic.Audio.AudioDevice("x", "QuietMic 虚拟麦克风", "QuietMic 虚拟麦克风", "VB-Audio Virtual Cable", false);
    Console.WriteLine("RENAMED-MATCH: " + renamed.IsAdapter("VB-Audio Virtual Cable"));
    var cable = QuietMic.VirtualMic.VirtualCable.Find();
    Console.WriteLine("CABLE: " + (cable is null ? "未安装" : $"{cable.Render.Name} -> {cable.Capture.Name}"));
    return;
}

if (args.Length >= 4 && args[0] == "wav")
{
    WavMode.Run(args[1], args[2], args[3]);
    return;
}

if (args.Length >= 1 && args[0] == "hp")
{
    double Rms(float[] values, int from, int count)
    {
        var sum = 0.0;
        for (var i = from; i < from + count; i++) sum += values[i] * values[i];
        return Math.Sqrt(sum / count);
    }

    // 两级 90 Hz 高通：直流应被完全去掉，60 Hz 明显衰减，1 kHz 基本不变。
    foreach (var frequency in new[] { 0d, 60d, 1000d })
    {
        var filter = new QuietMic.Audio.HighPassFilter(48_000, 90);
        var signal = new float[24_000];
        for (var i = 0; i < signal.Length; i++)
        {
            signal[i] = frequency == 0 ? 0.5f : (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / 48_000));
        }

        var before = Rms(signal, 12_000, 12_000);
        filter.Process(signal);
        var after = Rms(signal, 12_000, 12_000);
        Console.WriteLine($"{frequency,5} Hz: {20 * Math.Log10(after / before):0.0} dB");
    }

    return;
}

var modelsDirectory = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".research", "models"));

Console.WriteLine("models: " + modelsDirectory);

CheckFft();
Run("dpdfnet2_48khz_hr", () => new DpdfNetDenoiser(Path.Combine(modelsDirectory, "dpdfnet2_48khz_hr.onnx")));
Run("deepfilternet3", () => new DeepFilterNetDenoiser(
    Path.Combine(modelsDirectory, "enc.onnx"),
    Path.Combine(modelsDirectory, "erb_dec.onnx"),
    Path.Combine(modelsDirectory, "df_dec.onnx")));

static void Run(string name, Func<IDenoiser> factory)
{
    Console.WriteLine($"\n== {name} ==");

    using (var denoiser = factory())
    {
        var voiced = Measure(denoiser, voiced: true);
        using var denoiser2 = factory();
        var noise = Measure(denoiser2, voiced: false);
        Console.WriteLine($"  语音保留 {voiced.Ratio * 100:0.0}%  语音 RMS {voiced.Rms:0.0000}");
        Console.WriteLine($"  噪声残留 {noise.Ratio * 100:0.0}%");
        Console.WriteLine($"  {voiced.MsPerFrame:0.00} ms/帧 (预算 10)");
    }
}

static (double Ratio, double Rms, double MsPerFrame) Measure(IDenoiser denoiser, bool voiced)
{
    var hop = denoiser.HopSize;
    var input = new float[hop];
    var output = new float[hop];
    var random = new Random(1234);
    var phases = new double[24];
    var watch = Stopwatch.StartNew();
    double inputEnergy = 0, outputEnergy = 0;
    var counted = 0;

    for (var frame = 0; frame < 250; frame++)
    {
        for (var i = 0; i < hop; i++)
        {
            var sample = 0.05 * (random.NextDouble() * 2 - 1);
            if (voiced)
            {
                // 基频 180 Hz 的谐波叠加，粗略模拟人声
                for (var harmonic = 0; harmonic < phases.Length; harmonic++)
                {
                    var frequency = 180 * (harmonic + 1);
                    phases[harmonic] += 2 * Math.PI * frequency / 48000;
                    sample += 0.16 / (harmonic + 1) * Math.Sin(phases[harmonic]);
                }
            }

            input[i] = (float)sample;
        }

        denoiser.Process(input, output);

        if (output.Any(value => !float.IsFinite(value)))
        {
            throw new Exception("非有限值");
        }

        if (frame > 60)
        {
            inputEnergy += input.Sum(value => value * (double)value);
            outputEnergy += output.Sum(value => value * (double)value);
            counted++;
        }
    }

    watch.Stop();
    return (Math.Sqrt(outputEnergy / Math.Max(inputEnergy, 1e-9)), Math.Sqrt(outputEnergy / counted / hop), watch.Elapsed.TotalMilliseconds / 250);
}

static void CheckFft()
{
    var fft = new BluesteinFft(960);
    var re = new float[960];
    var im = new float[960];
    var random = new Random(7);
    var copy = new float[960];
    for (var i = 0; i < 960; i++)
    {
        re[i] = copy[i] = (float)(random.NextDouble() * 2 - 1);
    }

    fft.Forward(re, im);
    fft.Inverse(re, im);

    var error = 0.0;
    for (var i = 0; i < 960; i++)
    {
        error = Math.Max(error, Math.Abs(re[i] - copy[i]));
    }

    Console.WriteLine($"FFT 往返最大误差: {error:0.0000000000}");
}
