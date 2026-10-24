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
        public List<Bidder> FinalBids { get; set; } = new List<Bidder>();

        public string ItemName => Item.Name;
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
