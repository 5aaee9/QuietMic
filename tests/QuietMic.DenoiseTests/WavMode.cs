using NAudio.Wave.SampleProviders;
using QuietMic.Denoise;

namespace QuietMic.DenoiseTests;

internal static class WavMode
{
    public static void Run(string model, string inputPath, string outputPath)
    {
        var (samples, rate) = ReadMono(inputPath);
        if (rate != 48_000)
        {
            throw new InvalidOperationException("需要 48 kHz 输入");
        }

        using IDenoiser denoiser = model == "deepfilternet3"
            ? new DeepFilterNetDenoiser(
                Path.Combine(Path.GetDirectoryName(inputPath)!, "enc.onnx"),
                Path.Combine(Path.GetDirectoryName(inputPath)!, "erb_dec.onnx"),
                Path.Combine(Path.GetDirectoryName(inputPath)!, "df_dec.onnx"))
            : new DpdfNetDenoiser(model);

        var hop = denoiser.HopSize;
        var output = new List<float>(samples.Length + hop);
        var frame = new float[hop];
        var processed = new float[hop];

        for (var offset = 0; offset < samples.Length; offset += hop)
        {
            Array.Clear(frame);
            var count = Math.Min(hop, samples.Length - offset);
            Array.Copy(samples, offset, frame, 0, count);

            denoiser.Process(frame, processed);
            output.AddRange(processed);
        }

        WriteMono(outputPath, output.ToArray(), 48_000);
        Console.WriteLine($"wrote {outputPath} ({output.Count} samples)");
    }

    public static void Play(string path, string deviceName)
    {
        var (samples, rate) = ReadMono(path);
        if (rate != 48_000)
        {
            throw new InvalidOperationException("需要 48 kHz 输入");
        }

        var device = QuietMic.Audio.DeviceList.Render().First(d => d.Name.Contains(deviceName));
        using var sink = new QuietMic.Audio.DeviceSink(QuietMic.Audio.DeviceList.Open(device.Id));
        var frame = new float[480];
        for (var offset = 0; offset < samples.Length; offset += frame.Length)
        {
            Array.Clear(frame);
            var count = Math.Min(frame.Length, samples.Length - offset);
            Array.Copy(samples, offset, frame, 0, count);
            sink.Write(frame, frame.Length);
            Thread.Sleep(10);
            if (offset % (frame.Length * 100) == 0)
            {
                Console.WriteLine($"t={offset / 48000.0:0.0}s pumped={sink.PumpedSamples} pending={sink.PendingBytes} err={sink.PumpError?.Message}");
            }
        }

        Thread.Sleep(500);
        Console.WriteLine("played");
    }

    public static void Record(string path, int seconds, string deviceName)
    {
        var device = QuietMic.Audio.DeviceList.Capture().First(d => d.Name.Contains(deviceName));
        using var capture = new NAudio.CoreAudioApi.WasapiCapture(QuietMic.Audio.DeviceList.Open(device.Id));
        using var writer = new NAudio.Wave.WaveFileWriter(path, capture.WaveFormat);
        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
        capture.StartRecording();
        Thread.Sleep(seconds * 1000);
        capture.StopRecording();
        Console.WriteLine("recorded " + path);
    }

    private static (float[] Samples, int Rate) ReadMono(string path)
    {
        using var reader = new NAudio.Wave.WaveFileReader(path);
        var rate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;
        var all = new List<float>();
        var buffer = new float[rate * channels];
        var provider = new Pcm16BitToSampleProvider(reader);
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i += channels)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += buffer[i + c];
                }

                all.Add(sum / channels);
            }
        }

        return (all.ToArray(), rate);
    }

    private static void WriteMono(string path, float[] samples, int rate)
    {
        using var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(rate, 16, 1));
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            bytes[i * 2] = (byte)value;
            bytes[i * 2 + 1] = (byte)(value >> 8);
        }

        writer.Write(bytes, 0, bytes.Length);
    }
}
