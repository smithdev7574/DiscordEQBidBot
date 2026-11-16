using System;
using System.Collections.Generic;
using System.Linq;

namespace BidBot.DataAccess.Entities
{
    public enum LootQueueStatus
    {
        /// <summary>Looted, waiting for its turn.</summary>
        Waiting,
        /// <summary>The queue is posting its auction right now.</summary>
        Starting,
        /// <summary>The name matched several items, an officer needs to pick one from the posted list.</summary>
        AwaitingPick,
        /// <summary>Its auction is running.</summary>
        Active,
        /// <summary>The queue couldn't start it (for example it's not in the item list). An officer starts it by hand.</summary>
        NeedsOfficer
    }

    public class LootQueueEntry
    {
        public Guid ID { get; set; } = Guid.NewGuid();
        public string ItemName { get; set; } = string.Empty;
        public string? LucyLink { get; set; }
        public int RowNumber { get; set; }
        public LootQueueStatus Status { get; set; } = LootQueueStatus.Waiting;
        public Guid? AuctionID { get; set; }
        public string? Note { get; set; }
        public DateTime AddedDT { get; set; } = DateTime.UtcNow;

        public LootQueueEntry Clone() => (LootQueueEntry)MemberwiseClone();
    }

    /// <summary>
    /// The raid's looted items and the state of the Loot Queue post. Saved in bidbotdata.json.
    /// </summary>
    public class LootQueue
    {
        public ulong GuildID { get; set; }
        public ulong? ChannelID { get; set; }

        /// <summary>The main post (with the buttons) first, then any overflow posts.</summary>
        public List<ulong> MessageIDs { get; set; } = new List<ulong>();

        /// <summary>Auto-start is on: the queue starts the next item every few seconds.</summary>
        public bool IsRunning { get; set; }

        /// <summary>Active auction timers are stopped. Auto-start also waits while paused.</summary>
        public bool IsPaused { get; set; }

        /// <summary>The officer who pressed Start Auctions, shown as the auction starter.</summary>
        public ulong StartedByUserID { get; set; }
        public DateTime? LastStartDT { get; set; }
        public int FinishedCount { get; set; }
        public List<LootQueueEntry> Entries { get; set; } = new List<LootQueueEntry>();

        public LootQueue Clone()
        {
            var copy = (LootQueue)MemberwiseClone();
            copy.MessageIDs = MessageIDs.ToList();
            copy.Entries = Entries.Select(x => x.Clone()).ToList();
            return copy;
        }
    }
}
