using System.Buffers.Binary;

namespace ReachyMini.Audio;

public static class WavePcm16
{
    public static byte[] Encode(byte[] pcm16Bytes, AudioFormat format)
    {
        if (format.BitsPerSample != 16)
        {
            throw new NotSupportedException("Only PCM16 WAV encoding is supported.");
        }

        var dataLength = pcm16Bytes.Length;
        var totalLength = 44 + dataLength;
        var output = new byte[totalLength];

        WriteAscii(output, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4, 4), totalLength - 8);
        WriteAscii(output, 8, "WAVE");
        WriteAscii(output, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(22, 2), format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24, 4), format.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(28, 4), checked((int)format.BytesPerSecond));
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(32, 2), (short)format.BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(34, 2), format.BitsPerSample);
        WriteAscii(output, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40, 4), dataLength);
        Buffer.BlockCopy(pcm16Bytes, 0, output, 44, dataLength);

        return output;
    }

    public static (byte[] Pcm16Bytes, AudioFormat Format) Decode(byte[] wavBytes)
    {
        if (wavBytes.Length < 44)
        {
            throw new InvalidDataException("WAV is too short.");
        }

        if (!IsAscii(wavBytes, 0, "RIFF") || !IsAscii(wavBytes, 8, "WAVE"))
        {
            throw new InvalidDataException("Invalid WAV header.");
        }

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? pcm = null;
        var index = 12;
        while (index + 8 <= wavBytes.Length)
        {
            var chunkId = wavBytes.AsSpan(index, 4);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.AsSpan(index + 4, 4));
            var payloadStart = index + 8;
            var remaining = wavBytes.Length - payloadStart;

            if (remaining < 0)
            {
                break;
            }

            if (chunkLength > (uint)remaining)
            {
                if (chunkId.SequenceEqual("data"u8) && remaining > 0)
                {
                    // Some providers emit an incorrect terminal data size.
                    chunkLength = (uint)remaining;
                }
                else if (pcm is not null)
                {
                    break;
                }
                else
                {
                    // Corrupt/misaligned chunk header: resync and continue scanning.
                    index++;
                    continue;
                }
            }

            var payloadLength = (int)chunkLength;
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkLength < 16)
                {
                    throw new InvalidDataException("WAV fmt chunk is too short.");
                }

                var fmt = wavBytes.AsSpan(payloadStart, payloadLength);
                var audioFormat = BinaryPrimitives.ReadInt16LittleEndian(fmt.Slice(0, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt.Slice(14, 2));

                if (audioFormat != 1)
                {
                    throw new NotSupportedException($"Only PCM WAV is supported. Format={audioFormat}.");
                }
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (chunkLength == 0)
                {
                    throw new InvalidDataException("WAV data chunk is empty.");
                }
                if (chunkLength > int.MaxValue)
                {
                    throw new InvalidDataException("WAV data chunk is too large.");
                }

                pcm = new byte[payloadLength];
                Buffer.BlockCopy(wavBytes, payloadStart, pcm, 0, payloadLength);
                break;
            }

            var pad = (chunkLength & 1u) == 1u ? 1 : 0;
            var nextIndex = payloadStart + payloadLength + pad;
            if (nextIndex > wavBytes.Length)
            {
                nextIndex = payloadStart + payloadLength;
            }

            if (nextIndex <= index)
            {
                index++;
                continue;
            }

            index = nextIndex;
        }

        if (pcm is null)
        {
            throw new InvalidDataException("WAV data chunk not found.");
        }
        if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0)
        {
            throw new InvalidDataException("WAV format chunk is missing or invalid.");
        }
        if (bitsPerSample != 16)
        {
            throw new NotSupportedException("Only PCM16 WAV decoding is supported.");
        }

        return (pcm, new AudioFormat(sampleRate, channels, bitsPerSample));
    }

    private static void WriteAscii(byte[] bytes, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            bytes[offset + i] = (byte)value[i];
        }
    }

    private static bool IsAscii(byte[] bytes, int offset, string expected)
    {
        if (offset + expected.Length > bytes.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (bytes[offset + i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }
}
