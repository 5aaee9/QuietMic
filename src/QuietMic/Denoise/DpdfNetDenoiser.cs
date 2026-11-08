using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace QuietMic.Denoise;

/// <summary>DPDFNet ONNX 流式降噪器（48 kHz）。</summary>
public sealed class DpdfNetDenoiser : IDenoiser
{
    private readonly InferenceSession _session;
    private readonly DpdfNetDsp _dsp = new();
    private readonly string _specInput;
    private readonly string _stateInput;
    private readonly string _specOutput;
    private readonly string _stateOutput;
    private readonly float[] _state;
    private readonly float[] _spec = new float[DpdfNetDsp.SpecLength];
    private readonly float[] _specOut = new float[DpdfNetDsp.SpecLength];

    public DpdfNetDenoiser(string modelPath)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        _session = new InferenceSession(modelPath, options);

        if (_session.InputMetadata.Count < 2 || _session.OutputMetadata.Count < 2)
        {
            throw new InvalidOperationException("DPDFNet 模型必须包含 spec/state 输入与输出。");
        }

        _specInput = _session.InputMetadata.Keys.ElementAt(0);
        _stateInput = _session.InputMetadata.Keys.ElementAt(1);
        _specOutput = _session.OutputMetadata.Keys.ElementAt(0);
        _stateOutput = _session.OutputMetadata.Keys.ElementAt(1);

        var metadata = _session.ModelMetadata.CustomMetadataMap;
        var stateSize = int.Parse(metadata["state_size"], CultureInfo.InvariantCulture);
        var erbSize = int.Parse(metadata["erb_norm_state_size"], CultureInfo.InvariantCulture);
        var specNormSize = int.Parse(metadata["spec_norm_state_size"], CultureInfo.InvariantCulture);

        var erbInit = ParseList(metadata["erb_norm_init"]);
        var specInit = ParseList(metadata["spec_norm_init"]);

        _state = new float[stateSize];
        Array.Copy(erbInit, 0, _state, 0, Math.Min(erbSize, erbInit.Length));
        Array.Copy(specInit, 0, _state, erbSize, Math.Min(specNormSize, specInit.Length));
    }

    public int HopSize => DpdfNetDsp.HopSize;

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        _dsp.Analyze(input, _spec);

        var specTensor = new DenseTensor<float>(_spec, [1, 1, DpdfNetDsp.BinCount, 2]);
        var stateTensor = new DenseTensor<float>(_state, [_state.Length]);
        var specValue = NamedOnnxValue.CreateFromTensor(_specInput, specTensor);
        var stateValue = NamedOnnxValue.CreateFromTensor(_stateInput, stateTensor);

        using var results = _session.Run([specValue, stateValue]);

        var spec = results[0].AsEnumerable<float>().ToArray();
        var state = results[1].AsEnumerable<float>().ToArray();

        spec.CopyTo(_specOut, 0);
        state.CopyTo(_state, 0);

        _dsp.Synthesize(_specOut, output);
    }

    public void Reset()
    {
        _dsp.Reset();
    }

    private static float[] ParseList(string value)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => float.Parse(item, CultureInfo.InvariantCulture))
            .ToArray();
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
