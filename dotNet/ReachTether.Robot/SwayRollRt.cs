using System.Buffers.Binary;

internal sealed class SwayRollRt
{
    public const int HopMs = 50;

    private const int TargetSampleRateHz = 16_000;
    private const int FrameMs = 20;

    private const double SwayMaster = 1.5;
    private const double SensDbOffset = 4.0;
    private const double VadDbOn = -35.0;
    private const double VadDbOff = -45.0;
    private const int VadAttackMs = 40;
    private const int VadReleaseMs = 250;
    private const double EnvFollowGain = 0.65;

    private const double SwayFrequencyPitchHz = 2.2;
    private const double SwayAmplitudePitchDeg = 4.5;
    private const double SwayFrequencyYawHz = 0.6;
    private const double SwayAmplitudeYawDeg = 7.5;
    private const double SwayFrequencyRollHz = 1.3;
    private const double SwayAmplitudeRollDeg = 2.25;
    private const double SwayFrequencyXHz = 0.35;
    private const double SwayAmplitudeXMm = 4.5;
    private const double SwayFrequencyYHz = 0.45;
    private const double SwayAmplitudeYMm = 3.75;
    private const double SwayFrequencyZHz = 0.25;
    private const double SwayAmplitudeZMm = 2.25;

    private const double SwayDbLow = -46.0;
    private const double SwayDbHigh = -18.0;
    private const double LoudnessGamma = 0.9;
    private const int SwayAttackMs = 50;
    private const int SwayReleaseMs = 250;

    private static readonly int FrameSamples = TargetSampleRateHz * FrameMs / 1000;
    private static readonly int HopSamples = TargetSampleRateHz * HopMs / 1000;
    private static readonly int AttackFrames = Math.Max(1, VadAttackMs / HopMs);
    private static readonly int ReleaseFrames = Math.Max(1, VadReleaseMs / HopMs);
    private static readonly int SwayAttackFrames = Math.Max(1, SwayAttackMs / HopMs);
    private static readonly int SwayReleaseFrames = Math.Max(1, SwayReleaseMs / HopMs);

    private readonly Queue<float> _carry = new();
    private readonly float[] _frameRing = new float[FrameSamples];

    private readonly double _phasePitch;
    private readonly double _phaseYaw;
    private readonly double _phaseRoll;
    private readonly double _phaseX;
    private readonly double _phaseY;
    private readonly double _phaseZ;

    private int _frameCount;
    private int _frameWriteIndex;

    private bool _vadOn;
    private int _vadAbove;
    private int _vadBelow;

    private double _swayEnv;
    private int _swayUp;
    private int _swayDown;

    private double _tSeconds;

    public SwayRollRt(int rngSeed = 7)
    {
        var rng = new Random(rngSeed);
        _phasePitch = rng.NextDouble() * 2 * Math.PI;
        _phaseYaw = rng.NextDouble() * 2 * Math.PI;
        _phaseRoll = rng.NextDouble() * 2 * Math.PI;
        _phaseX = rng.NextDouble() * 2 * Math.PI;
        _phaseY = rng.NextDouble() * 2 * Math.PI;
        _phaseZ = rng.NextDouble() * 2 * Math.PI;
    }

    public void Reset()
    {
        _carry.Clear();
        _frameCount = 0;
        _frameWriteIndex = 0;

        _vadOn = false;
        _vadAbove = 0;
        _vadBelow = 0;

        _swayEnv = 0;
        _swayUp = 0;
        _swayDown = 0;

        _tSeconds = 0;
    }

    public IReadOnlyList<MotionOffsets> FeedPcm16(byte[] pcm16Bytes, int sampleRateHz, short channels = 1)
    {
        if (pcm16Bytes.Length < 2)
        {
            return [];
        }

        var normalizedMono = DecodeToMonoFloat32(pcm16Bytes, channels);
        if (normalizedMono.Length == 0)
        {
            return [];
        }

        var sourceRate = sampleRateHz > 0 ? sampleRateHz : TargetSampleRateHz;
        if (sourceRate != TargetSampleRateHz)
        {
            normalizedMono = ResampleLinear(normalizedMono, sourceRate, TargetSampleRateHz);
            if (normalizedMono.Length == 0)
            {
                return [];
            }
        }

        foreach (var sample in normalizedMono)
        {
            _carry.Enqueue(sample);
        }

        var offsets = new List<MotionOffsets>();
        while (_carry.Count >= HopSamples)
        {
            for (var i = 0; i < HopSamples; i++)
            {
                var sample = _carry.Dequeue();
                _frameRing[_frameWriteIndex] = sample;
                _frameWriteIndex = (_frameWriteIndex + 1) % FrameSamples;
                if (_frameCount < FrameSamples)
                {
                    _frameCount++;
                }
            }

            _tSeconds += HopMs / 1000.0;
            if (_frameCount < FrameSamples)
            {
                continue;
            }

            var db = ComputeRmsDbFs(_frameRing);
            UpdateVadAndEnvelope(db);

            var loud = LoudnessGain(db) * SwayMaster;
            var env = _swayEnv;

            var pitch =
                DegToRad(SwayAmplitudePitchDeg)
                * loud
                * env
                * Math.Sin((2 * Math.PI * SwayFrequencyPitchHz * _tSeconds) + _phasePitch);
            var yaw =
                DegToRad(SwayAmplitudeYawDeg)
                * loud
                * env
                * Math.Sin((2 * Math.PI * SwayFrequencyYawHz * _tSeconds) + _phaseYaw);
            var roll =
                DegToRad(SwayAmplitudeRollDeg)
                * loud
                * env
                * Math.Sin((2 * Math.PI * SwayFrequencyRollHz * _tSeconds) + _phaseRoll);
            var xMeters =
                (SwayAmplitudeXMm / 1000.0)
                * loud
                * env
                * Math.Sin((2 * Math.PI * SwayFrequencyXHz * _tSeconds) + _phaseX);
            var yMeters =
                (SwayAmplitudeYMm / 1000.0)
                * loud
                * env
                * Math.Sin((2 * Math.PI * SwayFrequencyYHz * _tSeconds) + _phaseY);
            var zMeters =
                (SwayAmplitudeZMm / 1000.0)
                * loud
                * env
                * Math.Sin((2 * Math.PI * SwayFrequencyZHz * _tSeconds) + _phaseZ);

            offsets.Add(new MotionOffsets(
                XMeters: xMeters,
                YMeters: yMeters,
                ZMeters: zMeters,
                RollRadians: roll,
                PitchRadians: pitch,
                YawRadians: yaw));
        }

        return offsets;
    }

    private void UpdateVadAndEnvelope(double db)
    {
        if (db >= VadDbOn)
        {
            _vadAbove++;
            _vadBelow = 0;
            if (!_vadOn && _vadAbove >= AttackFrames)
            {
                _vadOn = true;
            }
        }
        else if (db <= VadDbOff)
        {
            _vadBelow++;
            _vadAbove = 0;
            if (_vadOn && _vadBelow >= ReleaseFrames)
            {
                _vadOn = false;
            }
        }

        if (_vadOn)
        {
            _swayUp = Math.Min(SwayAttackFrames, _swayUp + 1);
            _swayDown = 0;
        }
        else
        {
            _swayDown = Math.Min(SwayReleaseFrames, _swayDown + 1);
            _swayUp = 0;
        }

        var up = (double)_swayUp / SwayAttackFrames;
        var down = 1.0 - ((double)_swayDown / SwayReleaseFrames);
        var target = _vadOn ? up : down;

        _swayEnv += EnvFollowGain * (target - _swayEnv);
        _swayEnv = Math.Clamp(_swayEnv, 0, 1);
    }

    private static double ComputeRmsDbFs(float[] frame)
    {
        var sumSquares = 0.0;
        for (var i = 0; i < frame.Length; i++)
        {
            var sample = frame[i];
            sumSquares += sample * sample;
        }

        var rms = Math.Sqrt((sumSquares / frame.Length) + 1e-12);
        return 20.0 * Math.Log10(rms + 1e-12);
    }

    private static double LoudnessGain(double db)
    {
        var normalized = (db + SensDbOffset - SwayDbLow) / (SwayDbHigh - SwayDbLow);
        normalized = Math.Clamp(normalized, 0.0, 1.0);
        return LoudnessGamma == 1.0 ? normalized : Math.Pow(normalized, LoudnessGamma);
    }

    private static float[] DecodeToMonoFloat32(byte[] pcm16Bytes, short channels)
    {
        var channelCount = channels <= 0 ? 1 : channels;
        var sampleCount = pcm16Bytes.Length / 2;
        if (sampleCount <= 0)
        {
            return [];
        }

        if (channelCount == 1)
        {
            var mono = new float[sampleCount];
            var source = pcm16Bytes.AsSpan();
            for (var i = 0; i < sampleCount; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i * 2, 2));
                mono[i] = sample / 32768f;
            }

            return mono;
        }

        var frameCount = sampleCount / channelCount;
        if (frameCount <= 0)
        {
            return [];
        }

        var output = new float[frameCount];
        var sourceMulti = pcm16Bytes.AsSpan();
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var sum = 0;
            for (var channel = 0; channel < channelCount; channel++)
            {
                var sampleOffset = ((frameIndex * channelCount) + channel) * 2;
                sum += BinaryPrimitives.ReadInt16LittleEndian(sourceMulti.Slice(sampleOffset, 2));
            }

            output[frameIndex] = (sum / (float)channelCount) / 32768f;
        }

        return output;
    }

    private static float[] ResampleLinear(float[] input, int sampleRateInHz, int sampleRateOutHz)
    {
        if (sampleRateInHz == sampleRateOutHz || input.Length == 0)
        {
            return input;
        }

        var outputCount = (int)Math.Round(input.Length * (double)sampleRateOutHz / sampleRateInHz);
        if (outputCount <= 1)
        {
            return [];
        }

        var output = new float[outputCount];
        var maxInputIndex = input.Length - 1;
        var maxOutputIndex = outputCount - 1;

        for (var i = 0; i < outputCount; i++)
        {
            var position = (i / (double)maxOutputIndex) * maxInputIndex;
            var index = (int)position;
            var next = Math.Min(index + 1, maxInputIndex);
            var fraction = position - index;
            output[i] = (float)(input[index] + ((input[next] - input[index]) * fraction));
        }

        return output;
    }

    private static double DegToRad(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }
}
