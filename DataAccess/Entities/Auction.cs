using BidBot.Items;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess.Entities
{
    public class Auction
    {
        private Guid _auctionID = Guid.NewGuid();
        private bool _isClosed = false;
        private ConcurrentDictionary<ulong, Bidder> _bidders = new ConcurrentDictionary<ulong, Bidder>();
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public Guid AuctionID
        {
            get
            {
                return _auctionID;
            }
        }
        public ulong GuildID { get; set; }
        public ulong MessageID { get; set; }
        public string ItemMarkdown { get; set; }
        public ItemEntry Item { get; set; }
        public int Duration { get; internal set; }
        public DateTime EndDT { get; internal set; }
        public ulong ChannelID { get; internal set; }
        public ulong UserID { get; internal set; }
        public string Winner { get; set; }
        public int WinAmount { get; set; } = 1;
        public DateTime? CancelledDT { get; set; }
        public int? RowNumber { get; set; }

        /// <summary>
        /// The raid that was active when the auction started, so an auction still running at /endraid
        /// is recorded on that raid when it finishes. Null for auctions from before this was tracked.
        /// </summary>
        public Guid? RaidID { get; set; }
        public List<Bidder> FinalBids { get; set; } = new List<Bidder>();

        // Public setters so these come back from bidbotdata.json after a restart.
        public ulong? WinnerUserID { get; set; }
        public DateTime? FinishedDT { get; set; }

        /// <summary>
        /// When the payment check for this item was done: reminder sent, already paid, or skipped.
        /// Set once so each item is only ever reminded about once.
        /// </summary>
        public DateTime? PaymentCheckedDT { get; set; }

        /// <summary>
        /// The timer is stopped (Pause Active Auctions on the loot queue post). Bids still work.
        /// While paused EndDT is DateTime.MaxValue so the auction can't finish.
        /// </summary>
        public bool IsPaused { get; private set; }
        public TimeSpan? PausedRemaining { get; private set; }

        public string ItemName => Item.Name;

        /// <summary>
        /// Stops the timer and remembers how much time was left. Returns false if it's already
        /// paused, closed, cancelled, or about to end anyway.
        /// </summary>
        public async Task<bool> Pause()
        {
            await _lock.WaitAsync();
            try
            {
                if (IsPaused || _isClosed || CancelledDT != null)
                    return false;

                var remaining = EndDT - DateTime.UtcNow;
                if (remaining <= TimeSpan.FromSeconds(1))
                    return false;

                PausedRemaining = remaining;
                EndDT = DateTime.MaxValue;
                IsPaused = true;
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Restarts the timer with the time that was left, but at least minimumRemaining so
        /// people get a chance to bid after a fight.
        /// </summary>
        public async Task<bool> Resume(TimeSpan minimumRemaining)
        {
            await _lock.WaitAsync();
            try
            {
                if (!IsPaused)
                    return false;

                var remaining = PausedRemaining ?? minimumRemaining;
                if (remaining < minimumRemaining)
                    remaining = minimumRemaining;

                EndDT = DateTime.UtcNow.Add(remaining);
                PausedRemaining = null;
                IsPaused = false;
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public bool IsClosed
        {
            get
            {
                return _isClosed;
            }
        }
        public async Task LockAuction()
        {
            await _lock.WaitAsync();
            try
            {
                _isClosed = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<(bool WasSuccessful, string Message)> AddBid(Bidder bidder)
        {
            await _lock.WaitAsync();
            try
            {
                if (_isClosed)
                    return (false, "Bid is closed.");

                _bidders[bidder.UserID] = bidder;
                return (true, string.Empty);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<(bool WasSuccessful, string Message)> RemoveBid(ulong userID)
        {
            await _lock.WaitAsync();
            try
            {
                if (_isClosed)
                    return (false, "Bid is closed.");

                if (!_bidders.TryRemove(userID, out var bidder))
                {
                    return (false, $"You don't have an active bid.");
                }

                return (true, string.Empty);
            }
            finally
            {
                _lock.Release();
            }
        }

        public List<Bidder> GetAllBidders()
        {
            return _bidders.Values.ToList();
        }

        public string GetFinishMessage()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## Results for {Item.Name} - {RowNumber}");
            if (!string.IsNullOrEmpty(Winner))
            {
                sb.AppendLine($"**{Winner}** won {Item.Name} for **{WinAmount.ToString("#,###")}** platinum.");
                var bidders = GetAllBidders().OrderByDescending(x => x.Amount).ThenBy(x => x.BidDT);
                sb.AppendLine($"### Bidders ({_bidders.Count})");

                int i = 1;
                foreach (var bidder in bidders)
                {
                    sb.AppendLine($"{i}. {bidder.Nickname} - {bidder.Amount.ToString("#,###")}");
                    i++;
                }
            }
            else
            {
                sb.AppendLine($"_No one was interested in {Item.Name}._");

            }            
            sb.AppendLine("------------------------------------");

            return sb.ToString();
        }
    }
}
