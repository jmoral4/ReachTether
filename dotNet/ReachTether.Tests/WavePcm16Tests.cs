using ReachTether.Audio;
using Xunit;

namespace ReachTether.Tests;

public class WavePcm16Tests
{
    [Fact]
    public void EncodeDecode_ShouldBeSymmetric()
    {
        // Arrange
        var format = new AudioFormat(16000, 1, 16);
        var pcm = new byte[1600]; // 0.1s of audio
        new Random(42).NextBytes(pcm);

        // Act
        var wav = WavePcm16.Encode(pcm, format);
        var (decodedPcm, decodedFormat) = WavePcm16.Decode(wav);

        // Assert
        Assert.Equal(format.SampleRateHz, decodedFormat.SampleRateHz);
        Assert.Equal(format.Channels, decodedFormat.Channels);
        Assert.Equal(format.BitsPerSample, decodedFormat.BitsPerSample);
        Assert.Equal(pcm.Length, decodedPcm.Length);
        Assert.Equal(pcm, decodedPcm);
    }
}
