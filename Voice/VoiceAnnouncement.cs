using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Voice
{
    public enum VoiceCommand
    {
        Speak,
        JoinChannel,
        LeaveChannel
    }

    public class VoiceAnnouncement
    {
        public VoiceCommand Command { get; set; } = VoiceCommand.Speak;

        public ulong GuildID { get; set; }

        /// <summary>
        /// When set, the announcement is skipped if the auction has already ended or been cancelled.
        /// </summary>
        public Guid? AuctionID { get; set; }

        /// <summary>
        /// Used to name the cached audio file.
        /// </summary>
        public string CacheName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTime QueuedDT { get; set; } = DateTime.UtcNow;
    }
}
