using System.Text;
using UnpackVision.App;

namespace UnpackVision.Tests;

public sealed class LoudSpeechServiceTests
{
    [Fact]
    public void Amplifies_16_bit_pcm_samples_without_changing_wave_structure()
    {
        var wave = CreatePcmWave(1000, -1000);

        var amplified = LoudSpeechService.AmplifyPcmWave(wave, 2);

        Assert.Equal(wave.Length, amplified.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(amplified, 0, 4));
        Assert.Equal(2000, BitConverter.ToInt16(amplified, 44));
        Assert.Equal(-2000, BitConverter.ToInt16(amplified, 46));
    }

    private static byte[] CreatePcmWave(short first, short second)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(40);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(22050);
        writer.Write(44100);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(4);
        writer.Write(first);
        writer.Write(second);
        return stream.ToArray();
    }
}
