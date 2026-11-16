using BidBot.DataAccess;
using System;

namespace BidBot.Logic
{
    /// <summary>
    /// Auction timing for the current raid. Each value is what /setduration or /setspacing set for the
    /// active raid, otherwise the configured default. A new raid (/setsheet) starts back at the defaults.
    /// - Duration: how long an auction runs when the Loot Queue starts it or /auction has no duration.
    /// - Spacing: seconds between auctions the Loot Queue starts.
    /// </summary>
    public class AuctionTimingService
    {
        public const int MIN_MINUTES = 1;
        public const int MAX_MINUTES = 60;
        public const int MIN_SPACING_SECONDS = 10;
        public const int MAX_SPACING_SECONDS = 600;

        private const int FALLBACK_MINUTES = 5;
        private const int FALLBACK_SPACING_SECONDS = 30;

        private readonly BidbotDataStore dataStore;
        private readonly int configuredDuration;
        private readonly int configuredSpacing;

        public AuctionTimingService(BidbotDataStore dataStore, IConfiguration config)
        {
            this.dataStore = dataStore;
            configuredDuration = int.TryParse(config["Settings:DefaultAuctionDuration"], out var minutes) && minutes >= MIN_MINUTES && minutes <= MAX_MINUTES
                ? minutes
                : FALLBACK_MINUTES;

            // Clamped too, so a config value below 10 seconds can't get around the minimum.
            configuredSpacing = int.TryParse(config["Settings:QueuedAuctionSpacingSeconds"], out var seconds)
                ? Math.Clamp(seconds, MIN_SPACING_SECONDS, MAX_SPACING_SECONDS)
                : FALLBACK_SPACING_SECONDS;
        }

        public int GetDefaultDuration()
        {
            var raidMinutes = dataStore.ActiveRaid?.DefaultAuctionDuration;
            return raidMinutes is >= MIN_MINUTES and <= MAX_MINUTES ? raidMinutes.Value : configuredDuration;
        }

        public int GetSpacingSeconds()
        {
            var raidSeconds = dataStore.ActiveRaid?.AuctionSpacingSeconds;
            return raidSeconds is >= MIN_SPACING_SECONDS and <= MAX_SPACING_SECONDS ? raidSeconds.Value : configuredSpacing;
        }

        public async Task<(bool WasSuccessful, string Message)> SetDurationForActiveRaid(int minutes)
        {
            if (minutes < MIN_MINUTES || minutes > MAX_MINUTES)
                return (false, $"The duration must be between {MIN_MINUTES} and {MAX_MINUTES} minutes.");

            var raid = dataStore.ActiveRaid;
            if (raid == null)
                return (false, "There's no active raid. Run /setsheet first.");

            var previous = GetDefaultDuration();
            raid.DefaultAuctionDuration = minutes;
            await dataStore.SaveChanges();

            return (true, $"Auctions now default to {minutes} minute{(minutes == 1 ? "" : "s")} for this raid (was {previous}). Auctions already running keep their time.");
        }

        public async Task<(bool WasSuccessful, string Message)> SetSpacingForActiveRaid(int seconds)
        {
            if (seconds < MIN_SPACING_SECONDS || seconds > MAX_SPACING_SECONDS)
                return (false, $"The spacing must be between {MIN_SPACING_SECONDS} and {MAX_SPACING_SECONDS} seconds.");

            var raid = dataStore.ActiveRaid;
            if (raid == null)
                return (false, "There's no active raid. Run /setsheet first.");

            var previous = GetSpacingSeconds();
            raid.AuctionSpacingSeconds = seconds;
            await dataStore.SaveChanges();

            return (true, $"The Loot Queue now starts an auction every {seconds} seconds for this raid (was {previous}).");
        }
    }
}
