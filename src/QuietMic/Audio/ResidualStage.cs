namespace QuietMic.Audio;

/// <summary>
/// 降噪后的收尾处理，解决两个实测问题：
/// 1. 模型输出电平普遍低于原声（DeepFilterNet 低约 12 dB），这里按原声电平做缓慢增益补偿；
/// 2. 模型在停顿处仍会留下“电流声”样的残留，用一个以噪声底为基准的下行扩展器把它压下去。
/// </summary>
public sealed class ResidualStage
{
    private const float SpeechOverFloor = 3f;
    private const float ThresholdBase = 0.02f;
    private const float MinGain = 0.008f;
    private const float EnvelopeRelease = 0.99983f;
    private const float OpenCoefficient = 0.01f;
    private const float CloseCoefficient = 0.00026f;
    private const float MinMakeupGain = 0.6f;
    private const float MaxMakeupGain = 12f;
    private const float EnergySmoothing = 0.02f;

    private readonly float _staticGain;
    private float _dryFloor = 1e-4f;
    private float _speechDryEnergy = 1e-12f;
    private float _speechWetEnergy = 1e-12f;
    private float _makeupGain = 1f;
    private float _envelope;
    private float _applied = 1f;

    public ResidualStage(float staticGain = 1f)
    {
        _staticGain = staticGain;
    }

    public void Process(Span<float> wet, ReadOnlySpan<float> dry)
    {
        var dryEnergy = Energy(dry);
        var wetEnergy = Energy(wet);
        var dryRms = MathF.Sqrt(dryEnergy + 1e-20f);

        // 噪声底跟踪：下降快、上升慢，语音帧的判定用“明显高于噪声底”。
        if (dryRms < _dryFloor)
        {
            _dryFloor += (dryRms - _dryFloor) * 0.1f;
        }
        else
        {
            _dryFloor += (dryRms - _dryFloor) * 0.0006f;
        }

        if (dryRms > _dryFloor * SpeechOverFloor)
        {
            _speechDryEnergy += (dryEnergy - _speechDryEnergy) * EnergySmoothing;
            _speechWetEnergy += (wetEnergy - _speechWetEnergy) * EnergySmoothing;
            var target = Math.Clamp(
                MathF.Sqrt(_speechDryEnergy / MathF.Max(_speechWetEnergy, 1e-12f)),
                MinMakeupGain,
                MaxMakeupGain);
            _makeupGain += (target - _makeupGain) * 0.05f;
        }

        // 阈值跟着总增益走：模型的固有增益越大，残留也越响，门限必须相应抬高。
        var threshold = _staticGain * _makeupGain * ThresholdBase;
        for (var i = 0; i < wet.Length; i++)
        {
            var sample = wet[i] * _makeupGain;
            var magnitude = MathF.Abs(sample);
            _envelope = magnitude > _envelope ? magnitude : _envelope * EnvelopeRelease;

            // 2:1 下行扩展：低于阈值每低 2 dB 再降 1 dB，最多降到 -42 dB。
            var gain = _envelope >= threshold
                ? 1f
                : MathF.Max(_envelope / threshold * (_envelope / threshold), MinGain);

            _applied += (gain - _applied) * (gain > _applied ? OpenCoefficient : CloseCoefficient);
            wet[i] = sample * _applied;
        }
    }

    private static float Energy(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
        }

        return (float)(sum / Math.Max(1, samples.Length));
    }
}
