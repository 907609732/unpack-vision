using System.Media;
using System.IO;
using System.Speech.Synthesis;
using System.Text;

namespace UnpackVision.App;

internal sealed class LoudSpeechService : IDisposable
{
    private readonly object _sync = new();
    private SoundPlayer? _activePlayer;
    private int _generation;
    private bool _disposed;

    public void Speak(string message, int volume)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        int generation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            generation = ++_generation;
            _activePlayer?.Stop();
        }

        _ = Task.Run(() => SynthesizeAndPlay(message, Math.Clamp(volume, 0, 100), generation));
    }

    private void SynthesizeAndPlay(string message, int volume, int generation)
    {
        try
        {
            byte[] wave;
            using (var synthesizer = new SpeechSynthesizer())
            using (var stream = new MemoryStream())
            {
                var chineseVoice = synthesizer.GetInstalledVoices()
                    .FirstOrDefault(voice => voice.Enabled &&
                        voice.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase));
                if (chineseVoice is not null)
                {
                    synthesizer.SelectVoice(chineseVoice.VoiceInfo.Name);
                }
                synthesizer.Volume = 100;
                synthesizer.Rate = 1;
                synthesizer.SetOutputToWaveStream(stream);
                synthesizer.Speak(message);
                synthesizer.SetOutputToNull();
                wave = AmplifyPcmWave(stream.ToArray(), volume / 50d);
            }

            var waveStream = new MemoryStream(wave, writable: false);
            var player = new SoundPlayer(waveStream);
            lock (_sync)
            {
                if (_disposed || generation != _generation)
                {
                    player.Dispose();
                    waveStream.Dispose();
                    return;
                }
                _activePlayer?.Dispose();
                _activePlayer = player;
            }
            player.PlaySync();
            lock (_sync)
            {
                if (ReferenceEquals(_activePlayer, player))
                {
                    _activePlayer = null;
                }
            }
            player.Dispose();
            waveStream.Dispose();
        }
        catch
        {
            // Voice feedback must never interrupt scanning or recording.
        }
    }

    internal static byte[] AmplifyPcmWave(byte[] source, double gain)
    {
        var result = source.ToArray();
        if (gain <= 0 || result.Length < 44 ||
            Encoding.ASCII.GetString(result, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(result, 8, 4) != "WAVE")
        {
            return result;
        }

        ushort format = 0;
        ushort bitsPerSample = 0;
        var offset = 12;
        while (offset + 8 <= result.Length)
        {
            var chunkName = Encoding.ASCII.GetString(result, offset, 4);
            var chunkLength = BitConverter.ToInt32(result, offset + 4);
            var chunkData = offset + 8;
            if (chunkLength < 0 || chunkData + chunkLength > result.Length)
            {
                break;
            }
            if (chunkName == "fmt " && chunkLength >= 16)
            {
                format = BitConverter.ToUInt16(result, chunkData);
                bitsPerSample = BitConverter.ToUInt16(result, chunkData + 14);
            }
            else if (chunkName == "data" && format == 1 && bitsPerSample == 16)
            {
                for (var sampleOffset = chunkData; sampleOffset + 1 < chunkData + chunkLength; sampleOffset += 2)
                {
                    var sample = BitConverter.ToInt16(result, sampleOffset);
                    var amplified = (short)Math.Clamp(Math.Round(sample * gain), short.MinValue, short.MaxValue);
                    result[sampleOffset] = (byte)(amplified & 0xFF);
                    result[sampleOffset + 1] = (byte)((amplified >> 8) & 0xFF);
                }
                break;
            }
            offset = chunkData + chunkLength + (chunkLength & 1);
        }
        return result;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _generation++;
            _activePlayer?.Stop();
            _activePlayer?.Dispose();
            _activePlayer = null;
        }
    }
}
