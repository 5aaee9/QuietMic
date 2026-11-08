using System.Numerics;

namespace QuietMic.Denoise;

/// <summary>
/// 任意长度的复数 FFT（Bluestein 算法）。前向/逆向均不归一化，与 DeepFilterNet 使用的
/// realfft 语义一致（调用方自行缩放）。
/// </summary>
public sealed class BluesteinFft
{
    private readonly int _length;
    private readonly int _fftLength;
    private readonly float[] _chirpRe;
    private readonly float[] _chirpIm;
    private readonly float[] _kernelRe;
    private readonly float[] _kernelIm;
    private readonly float[] _workRe;
    private readonly float[] _workIm;
    private readonly Radix2Fft _inner;

    public BluesteinFft(int length)
    {
        _length = length;
        _fftLength = 1;
        while (_fftLength < 2 * length - 1)
        {
            _fftLength <<= 1;
        }

        _inner = new Radix2Fft(_fftLength);
        _chirpRe = new float[length];
        _chirpIm = new float[length];

        for (var i = 0; i < length; i++)
        {
            var angle = Math.PI * i * i / length;
            _chirpRe[i] = (float)Math.Cos(angle);
            _chirpIm[i] = (float)-Math.Sin(angle);
        }

        // b[n] = conj(chirp[n])，按循环卷积环绕摆放，然后预计算其 FFT。
        _kernelRe = new float[_fftLength];
        _kernelIm = new float[_fftLength];
        _kernelRe[0] = _chirpRe[0];
        _kernelIm[0] = -_chirpIm[0];
        for (var i = 1; i < length; i++)
        {
            _kernelRe[i] = _chirpRe[i];
            _kernelIm[i] = -_chirpIm[i];
            _kernelRe[_fftLength - i] = _chirpRe[i];
            _kernelIm[_fftLength - i] = -_chirpIm[i];
        }

        _inner.Transform(_kernelRe, _kernelIm, false);

        _workRe = new float[_fftLength];
        _workIm = new float[_fftLength];
    }

    public int Length => _length;

    public void Forward(float[] re, float[] im)
    {
        Prepare(re, im);
        _inner.Transform(_workRe, _workIm, false);
        MultiplyKernel();
        _inner.Transform(_workRe, _workIm, true);

        // 基 2 逆变换不归一化；卷积结果需要除以 FFT 长度。
        var inverseScale = 1f / _fftLength;
        for (var i = 0; i < _length; i++)
        {
            _workRe[i] *= inverseScale;
            _workIm[i] *= inverseScale;
        }

        for (var i = 0; i < _length; i++)
        {
            re[i] = _workRe[i] * _chirpRe[i] - _workIm[i] * _chirpIm[i];
            im[i] = _workRe[i] * _chirpIm[i] + _workIm[i] * _chirpRe[i];
        }
    }

    public void Inverse(float[] re, float[] im)
    {
        for (var i = 0; i < _length; i++)
        {
            im[i] = -im[i];
        }

        Forward(re, im);

        var scale = 1f / _length;
        for (var i = 0; i < _length; i++)
        {
            re[i] *= scale;
            im[i] = -im[i] * scale;
        }
    }

    private void Prepare(float[] re, float[] im)
    {
        Array.Clear(_workRe);
        Array.Clear(_workIm);

        for (var i = 0; i < _length; i++)
        {
            _workRe[i] = re[i] * _chirpRe[i] - im[i] * _chirpIm[i];
            _workIm[i] = re[i] * _chirpIm[i] + im[i] * _chirpRe[i];
        }
    }

    private void MultiplyKernel()
    {
        for (var i = 0; i < _fftLength; i++)
        {
            var real = _workRe[i] * _kernelRe[i] - _workIm[i] * _kernelIm[i];
            var imaginary = _workRe[i] * _kernelIm[i] + _workIm[i] * _kernelRe[i];
            _workRe[i] = real;
            _workIm[i] = imaginary;
        }
    }

    /// <summary>迭代式基 2 FFT，带位反转重排。</summary>
    private sealed class Radix2Fft
    {
        private readonly int _size;
        private readonly int[] _reversed;
        private readonly float[] _cos;
        private readonly float[] _sin;

        public Radix2Fft(int size)
        {
            _size = size;
            var bits = BitOperations.Log2((uint)size);
            _reversed = new int[size];
            for (var i = 0; i < size; i++)
            {
                _reversed[i] = ReverseBits(i, bits);
            }

            _cos = new float[size / 2];
            _sin = new float[size / 2];
            for (var i = 0; i < size / 2; i++)
            {
                var angle = 2 * Math.PI * i / size;
                _cos[i] = (float)Math.Cos(angle);
                _sin[i] = (float)Math.Sin(angle);
            }
        }

        private static int ReverseBits(int value, int bits)
        {
            var result = 0;
            for (var i = 0; i < bits; i++)
            {
                result = (result << 1) | (value & 1);
                value >>= 1;
            }

            return result;
        }

        public void Transform(float[] re, float[] im, bool inverse)
        {
            for (var i = 0; i < _size; i++)
            {
                var j = _reversed[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (var length = 2; length <= _size; length <<= 1)
            {
                var half = length >> 1;
                var step = _size / length;
                for (var start = 0; start < _size; start += length)
                {
                    for (var j = 0; j < half; j++)
                    {
                        var k = j * step;
                        var wr = _cos[k];
                        var wi = inverse ? _sin[k] : -_sin[k];
                        var a = start + j;
                        var b = a + half;

                        var vr = re[b] * wr - im[b] * wi;
                        var vi = re[b] * wi + im[b] * wr;
                        var ur = re[a];
                        var ui = im[a];

                        re[a] = ur + vr;
                        im[a] = ui + vi;
                        re[b] = ur - vr;
                        im[b] = ui - vi;
                    }
                }
            }
        }
    }
}
