using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace QuietMic.Denoise;

/// <summary>DeepFilterNet3 流式降噪器（48 kHz，编码器 + ERB 解码器 + 深度滤波解码器三段模型）。</summary>
public sealed class DeepFilterNetDenoiser : IDenoiser
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _erbDecoder;
    private readonly InferenceSession _dfDecoder;
    private readonly DeepFilterNetDsp _dsp = new();

    private readonly string[] _encoderInputs;
    private readonly string[] _encoderOutputs;
    private readonly string[] _erbDecoderInputs;
    private readonly string _erbDecoderOutput;
    private readonly string[] _dfDecoderInputs;
    private readonly string _dfDecoderOutput;

    private readonly float[] _featErb = new float[DeepFilterNetDsp.ErbBands];
    private readonly float[] _featSpec = new float[DeepFilterNetDsp.DfBins * 2];

    public DeepFilterNetDenoiser(string encoderPath, string erbDecoderPath, string dfDecoderPath)
    {
        _encoder = CreateSession(encoderPath);
        _erbDecoder = CreateSession(erbDecoderPath);
        _dfDecoder = CreateSession(dfDecoderPath);

        if (_encoder.InputMetadata.Count < 2 || _encoder.OutputMetadata.Count < 6
            || _erbDecoder.InputMetadata.Count < 5 || _erbDecoder.OutputMetadata.Count < 1
            || _dfDecoder.InputMetadata.Count < 2 || _dfDecoder.OutputMetadata.Count < 1)
        {
            throw new InvalidOperationException("DeepFilterNet 模型结构与预期不符。");
        }

        _encoderInputs = _encoder.InputMetadata.Keys.ToArray();
        _encoderOutputs = _encoder.OutputMetadata.Keys.ToArray();
        _erbDecoderInputs = _erbDecoder.InputMetadata.Keys.ToArray();
        _erbDecoderOutput = _erbDecoder.OutputMetadata.Keys.ElementAt(0);
        _dfDecoderInputs = _dfDecoder.InputMetadata.Keys.ToArray();
        _dfDecoderOutput = _dfDecoder.OutputMetadata.Keys.ElementAt(0);
    }

    public int HopSize => DeepFilterNetDsp.FrameSize;

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        _dsp.ExtractFeatures(input, _featErb, _featSpec);

        string[] encoderNames = _encoderInputs;
        float[] mask;
        float[] coefficients;
        float[] embedding;

        {
            var featErb = new DenseTensor<float>(_featErb, [1, 1, 1, DeepFilterNetDsp.ErbBands]);
            var featSpec = new DenseTensor<float>(_featSpec, [1, 2, 1, DeepFilterNetDsp.DfBins]);
            var erbValue = NamedOnnxValue.CreateFromTensor(encoderNames[0], featErb);
            var specValue = NamedOnnxValue.CreateFromTensor(encoderNames[1], featSpec);
            using var results = _encoder.Run([erbValue, specValue]);
            var e0 = Tensor(results, 0);
            var e1 = Tensor(results, 1);
            var e2 = Tensor(results, 2);
            var e3 = Tensor(results, 3);
            embedding = Tensor(results, 4);
            var c0 = Tensor(results, 5);

            mask = RunErbDecoder(embedding, e3, e2, e1, e0);
            coefficients = RunDfDecoder(embedding, c0);
        }

        if (mask.Length < DeepFilterNetDsp.ErbBands
            || coefficients.Length < DeepFilterNetDsp.DfBins * DeepFilterNetDsp.DfOrder * 2)
        {
            throw new InvalidOperationException("DeepFilterNet 输出形状不正确。");
        }

        _dsp.Synthesize(mask, coefficients, output);
    }

    private float[] RunErbDecoder(float[] embedding, float[] e3, float[] e2, float[] e1, float[] e0)
    {
        var emb = new DenseTensor<float>(embedding, [1, 1, 512]);
        var t3 = new DenseTensor<float>(e3, [1, 64, 1, 8]);
        var t2 = new DenseTensor<float>(e2, [1, 64, 1, 8]);
        var t1 = new DenseTensor<float>(e1, [1, 64, 1, 16]);
        var t0 = new DenseTensor<float>(e0, [1, 64, 1, 32]);

        var vEmb = NamedOnnxValue.CreateFromTensor(_erbDecoderInputs[0], emb);
        var v3 = NamedOnnxValue.CreateFromTensor(_erbDecoderInputs[1], t3);
        var v2 = NamedOnnxValue.CreateFromTensor(_erbDecoderInputs[2], t2);
        var v1 = NamedOnnxValue.CreateFromTensor(_erbDecoderInputs[3], t1);
        var v0 = NamedOnnxValue.CreateFromTensor(_erbDecoderInputs[4], t0);

        using var results = _erbDecoder.Run([vEmb, v3, v2, v1, v0]);
        return results[0].AsEnumerable<float>().ToArray();
    }

    private float[] RunDfDecoder(float[] embedding, float[] c0)
    {
        var emb = new DenseTensor<float>(embedding, [1, 1, 512]);
        var t0 = new DenseTensor<float>(c0, [1, 64, 1, DeepFilterNetDsp.DfBins]);

        var vEmb = NamedOnnxValue.CreateFromTensor(_dfDecoderInputs[0], emb);
        var v0 = NamedOnnxValue.CreateFromTensor(_dfDecoderInputs[1], t0);

        using var results = _dfDecoder.Run([vEmb, v0]);
        return results[0].AsEnumerable<float>().ToArray();
    }

    private static float[] Tensor(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int index)
    {
        return results[index].AsEnumerable<float>().ToArray();
    }

    private static InferenceSession CreateSession(string path)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        return new InferenceSession(path, options);
    }

    public void Reset()
    {
        _dsp.Reset();
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _erbDecoder.Dispose();
        _dfDecoder.Dispose();
    }
}
