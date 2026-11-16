using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Voice
{
    /// <summary>
    /// Picks a random voice from the voices available on the Azure Speech resource.
    /// Voices are shuffled and handed out one at a time so every voice gets a turn before any repeats.
    /// </summary>
    public class VoiceSelector
    {
        public const string FALLBACK_VOICE = "en-US-JennyNeural";
        private static readonly TimeSpan RETRY_LOAD_INTERVAL = TimeSpan.FromMinutes(30);

        private readonly VoiceSettings settings;
        private readonly ILogger<VoiceSelector> logger;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        private List<string> voices = new List<string>();
        private Queue<string> shuffledVoices = new Queue<string>();
        private bool loadedFromAzure;
        private DateTime? lastLoadAttemptDT;

        public VoiceSelector(VoiceSettings settings, ILogger<VoiceSelector> logger)
        {
            this.settings = settings;
            this.logger = logger;
        }

        public async Task<string> GetNextRandom()
        {
            await _lock.WaitAsync();
            try
            {
                await loadVoices();

                if (!shuffledVoices.Any())
                    shuffledVoices = shuffle(voices);

                if (!shuffledVoices.TryDequeue(out var voice))
                    return FALLBACK_VOICE;

                return voice;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting a random voice, using {Voice}", FALLBACK_VOICE);
                return FALLBACK_VOICE;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task loadVoices()
        {
            if (loadedFromAzure)
                return;

            if (lastLoadAttemptDT != null && DateTime.UtcNow - lastLoadAttemptDT < RETRY_LOAD_INTERVAL)
                return;

            lastLoadAttemptDT = DateTime.UtcNow;

            try
            {
                using var synthesizer = new SpeechSynthesizer(settings.CreateSpeechConfig(), (AudioConfig?)null);
                using var result = await synthesizer.GetVoicesAsync(string.Empty);

                if (result.Reason != ResultReason.VoicesListRetrieved)
                {
                    logger.LogWarning("Unable to load voices from Azure: {Error}", result.ErrorDetails);
                    useConfiguredVoices();
                    return;
                }

                var available = result.Voices
                    .Select(x => x.ShortName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                var filtered = settings.AllowedVoices.Any()
                    ? available.Where(x => settings.AllowedVoices.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList()
                    : available.Where(x => x.StartsWith(settings.LocaleFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                var missing = settings.AllowedVoices.Except(available, StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Any())
                    logger.LogWarning("These configured voices are not available on your Azure Speech resource: {Voices}", string.Join(", ", missing));

                if (!filtered.Any())
                {
                    logger.LogWarning("No Azure voices matched the Voice settings, using {Voice}", FALLBACK_VOICE);
                    filtered.Add(FALLBACK_VOICE);
                }

                voices = filtered;
                shuffledVoices = shuffle(voices);
                loadedFromAzure = true;
                logger.LogInformation("Loaded {Count} voices from Azure for announcements.", voices.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error loading voices from Azure.");
                useConfiguredVoices();
            }
        }

        private void useConfiguredVoices()
        {
            if (voices.Any())
                return;

            voices = settings.AllowedVoices.Any()
                ? settings.AllowedVoices.ToList()
                : new List<string>() { FALLBACK_VOICE };

            shuffledVoices = shuffle(voices);
        }

        private static Queue<string> shuffle(List<string> source)
        {
            var list = source.ToList();
            for (int i = list.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }

            return new Queue<string>(list);
        }
    }
}
