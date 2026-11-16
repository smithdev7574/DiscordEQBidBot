using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System;
using System.Collections.Generic;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace BidBot.Voice
{
    /// <summary>
    /// Creates text to speech audio files with Azure Speech, using the local cache when possible.
    /// Files are 48kHz 16-bit mono WAV, which is what Discord expects (minus the stereo copy done at playback).
    /// </summary>
    public class VoiceService
    {
        private readonly VoiceSettings settings;
        private readonly VoiceSelector voiceSelector;
        private readonly VoiceCache cache;
        private readonly ILogger<VoiceService> logger;

        public VoiceService(VoiceSettings settings, VoiceSelector voiceSelector, VoiceCache cache, ILogger<VoiceService> logger)
        {
            this.settings = settings;
            this.voiceSelector = voiceSelector;
            this.cache = cache;
            this.logger = logger;
        }

        public string CreateAnnouncementText(string itemName)
        {
            var spokenName = applyPronunciations(itemName);
            return settings.AnnouncementTemplate.Replace(VoiceSettings.ITEM_NAME_TOKEN, spokenName, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<(bool WasSuccessful, string Message, string FilePath)> GenerateFile(string cacheName, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return (false, "Nothing to say.", string.Empty);

            if (cache.TryGet(cacheName, text, out var cachedPath))
                return (true, "Loaded from cache.", cachedPath);

            var voiceName = await voiceSelector.GetNextRandom();
            var result = await synthesize(text, voiceName);

            if (!result.WasSuccessful && !voiceName.Equals(VoiceSelector.FALLBACK_VOICE, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Voice {Voice} failed ({Message}), retrying with {Fallback}", voiceName, result.Message, VoiceSelector.FALLBACK_VOICE);
                result = await synthesize(text, VoiceSelector.FALLBACK_VOICE);
            }

            if (!result.WasSuccessful || result.AudioData == null)
                return (false, result.Message, string.Empty);

            var filePath = await cache.Save(cacheName, text, result.AudioData);
            return (true, $"Created with {voiceName}.", filePath);
        }

        private async Task<(bool WasSuccessful, string Message, byte[]? AudioData)> synthesize(string text, string voiceName)
        {
            try
            {
                var speechConfig = settings.CreateSpeechConfig();
                speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff48Khz16BitMonoPcm);

                // A null AudioConfig keeps the audio in memory instead of playing it on the local speakers.
                using var synthesizer = new SpeechSynthesizer(speechConfig, (AudioConfig?)null);
                using var result = await synthesizer.SpeakSsmlAsync(buildSsml(text, voiceName));

                if (result.Reason == ResultReason.SynthesizingAudioCompleted && result.AudioData?.Length > 0)
                    return (true, string.Empty, result.AudioData);

                if (result.Reason == ResultReason.Canceled)
                {
                    var details = SpeechSynthesisCancellationDetails.FromResult(result);
                    return (false, $"{details.Reason} {details.ErrorCode}: {details.ErrorDetails}", null);
                }

                return (false, $"Unexpected result from Azure: {result.Reason}", null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating speech with voice {Voice}", voiceName);
                return (false, ex.Message, null);
            }
        }

        private string buildSsml(string text, string voiceName)
        {
            var ssml =
                "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">" +
                    $"<voice name=\"{SecurityElement.Escape(voiceName)}\">" +
                        $"<prosody rate=\"{SecurityElement.Escape(settings.SpeakingRate)}\">" +
                            $"{SecurityElement.Escape(text)}" +
                        "</prosody>" +
                    "</voice>" +
                "</speak>";

            return ssml;
        }

        private string applyPronunciations(string itemName)
        {
            var result = itemName ?? string.Empty;
            foreach (var pronunciation in settings.Pronunciations)
            {
                var pattern = $@"\b{Regex.Escape(pronunciation.Key)}\b";
                result = Regex.Replace(result, pattern, pronunciation.Value, RegexOptions.IgnoreCase);
            }

            return result;
        }
    }
}
