using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Voice
{
    /// <summary>
    /// Reads the 16-bit PCM WAV files created by VoiceService and converts them to the
    /// 48kHz 16-bit stereo PCM that Discord's PCM stream expects. Replaces the old ffmpeg step.
    /// </summary>
    public static class WaveFileReader
    {
        public const int DISCORD_SAMPLE_RATE = 48000;
        private const int BITS_PER_SAMPLE = 16;

        public static async Task<(bool WasSuccessful, string Message, byte[] Pcm)> ReadAsDiscordPcm(string filePath, double volume)
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            return ConvertToDiscordPcm(bytes, volume);
        }

        public static (bool WasSuccessful, string Message, byte[] Pcm) ConvertToDiscordPcm(byte[] wav, double volume)
        {
            if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
                return (false, "File is not a WAV file.", Array.Empty<byte>());

            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataLength = 0;

            var position = 12;
            while (position + 8 <= wav.Length)
            {
                var chunkID = Encoding.ASCII.GetString(wav, position, 4);
                var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(position + 4, 4));
                var chunkStart = position + 8;

                // Streamed WAVs can report a bad size, clamp to what's actually in the file.
                if (chunkSize < 0 || chunkStart + chunkSize > wav.Length)
                    chunkSize = wav.Length - chunkStart;

                if (chunkID == "fmt " && chunkSize >= 16)
                {
                    channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(chunkStart + 2, 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(chunkStart + 4, 4));
                    bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(chunkStart + 14, 2));
                }
                else if (chunkID == "data")
                {
                    dataOffset = chunkStart;
                    dataLength = chunkSize;
                    break;
                }

                position = chunkStart + chunkSize + (chunkSize % 2); // chunks are word aligned
            }

            if (dataOffset < 0)
                return (false, "WAV file has no audio data.", Array.Empty<byte>());

            if (sampleRate != DISCORD_SAMPLE_RATE || bitsPerSample != BITS_PER_SAMPLE || (channels != 1 && channels != 2))
                return (false, $"Unsupported WAV format: {sampleRate}Hz, {bitsPerSample}-bit, {channels} channel(s).", Array.Empty<byte>());

            var sampleCount = dataLength / 2;
            var outputSamples = channels == 1 ? sampleCount * 2 : sampleCount;
            var pcm = new byte[outputSamples * 2];
            var source = wav.AsSpan(dataOffset, sampleCount * 2);

            var outIndex = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i * 2, 2));
                var scaled = (short)Math.Clamp((int)(sample * volume), short.MinValue, short.MaxValue);

                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(outIndex, 2), scaled);
                outIndex += 2;

                if (channels == 1)
                {
                    // Copy mono into the right channel too.
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(outIndex, 2), scaled);
                    outIndex += 2;
                }
            }

            return (true, string.Empty, pcm);
        }
    }
}
