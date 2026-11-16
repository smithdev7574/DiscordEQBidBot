using Microsoft.CognitiveServices.Speech;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BidBot.Voice
{
    /// <summary>
    /// Reads the "Voice" section of appsettings.json once at startup.
    /// </summary>
    public class VoiceSettings
    {
        public const string PLACEHOLDER_PREFIX = "Your";
        public const string ITEM_NAME_TOKEN = "{ItemName}";
        public const string DEFAULT_ANNOUNCEMENT_TEMPLATE = "Bidding is now open for " + ITEM_NAME_TOKEN;

        public bool IsEnabled { get; }
        public bool DebugLogging { get; }
        public bool StayConnectedDuringRaid { get; }
        public int MaxRaidHours { get; }
        public string VoiceChannelName { get; }
        public string SpeechKey { get; }
        public string SpeechRegion { get; }
        public string AnnouncementTemplate { get; }
        public string SpeakingRate { get; }
        public double Volume { get; }
        public int IdleLeaveSeconds { get; }
        public int ConnectTimeoutSeconds { get; }
        public int MaxQueueSize { get; }
        public string CachePath { get; }
        public string LocaleFilter { get; }
        public List<string> AllowedVoices { get; }
        public Dictionary<string, string> Pronunciations { get; }

        public VoiceSettings(IConfiguration config)
        {
            bool.TryParse(config["Voice:IsEnabled"], out var isEnabled);
            this.IsEnabled = isEnabled;

            bool.TryParse(config["Voice:DebugLogging"], out var debugLogging);
            this.DebugLogging = debugLogging;

            // Defaults to true when the setting is missing.
            this.StayConnectedDuringRaid = !bool.TryParse(config["Voice:StayConnectedDuringRaid"], out var stayConnected) || stayConnected;
            this.MaxRaidHours = getInt(config["Voice:MaxRaidHours"], 6, 1, 24);

            this.VoiceChannelName = config["Voice:VoiceChannelName"] ?? string.Empty;
            this.SpeechKey = config["Voice:SpeechKey"] ?? string.Empty;
            this.SpeechRegion = config["Voice:SpeechRegion"] ?? string.Empty;
            this.SpeakingRate = config["Voice:SpeakingRate"] ?? "0%";
            this.CachePath = config["Voice:CachePath"] ?? string.Empty;
            this.LocaleFilter = config["Voice:LocaleFilter"] ?? string.Empty;

            var template = config["Voice:AnnouncementTemplate"];
            this.AnnouncementTemplate = string.IsNullOrWhiteSpace(template) ? DEFAULT_ANNOUNCEMENT_TEMPLATE : template;

            this.Volume = getDouble(config["Voice:Volume"], 1.0, 0.0, 2.0);
            this.IdleLeaveSeconds = getInt(config["Voice:IdleLeaveSeconds"], 5, 0, 300);
            this.ConnectTimeoutSeconds = getInt(config["Voice:ConnectTimeoutSeconds"], 30, 20, 120);
            this.MaxQueueSize = getInt(config["Voice:MaxQueueSize"], 25, 1, 500);

            this.AllowedVoices = config.GetSection("Voice:AllowedVoices").GetChildren()
                .Select(x => x.Value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .ToList();

            this.Pronunciations = config.GetSection("Voice:Pronunciations").GetChildren()
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
                .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
        }

        public bool HasSpeechCredentials =>
            isConfigured(SpeechKey) && isConfigured(SpeechRegion);

        public bool HasVoiceChannel => isConfigured(VoiceChannelName);

        public SpeechConfig CreateSpeechConfig()
        {
            return SpeechConfig.FromSubscription(SpeechKey, SpeechRegion);
        }

        private static bool isConfigured(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && !value.StartsWith(PLACEHOLDER_PREFIX, StringComparison.OrdinalIgnoreCase);
        }

        private static int getInt(string? value, int defaultValue, int min, int max)
        {
            if (!int.TryParse(value, out var result))
                return defaultValue;

            return Math.Clamp(result, min, max);
        }

        private static double getDouble(string? value, double defaultValue, double min, double max)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                return defaultValue;

            return Math.Clamp(result, min, max);
        }
    }
}
