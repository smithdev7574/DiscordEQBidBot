using BidBot.DataAccess.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess
{
    public class BidbotData
    {
        public ConcurrentDictionary<Guid, WishlistEntry> Wishlist { get; set; } = new ConcurrentDictionary<Guid, WishlistEntry>();
        public ConcurrentDictionary<int, ItemHtmlEntry> ItemHtmlEntries { get; set; } = new ConcurrentDictionary<int, ItemHtmlEntry>();
        public ConcurrentDictionary<Guid, Auction> HistoricalAuctions { get; set; } = new ConcurrentDictionary<Guid, Auction>();
        public ConcurrentDictionary<Guid, Raid> Raids { get; set; } = new ConcurrentDictionary<Guid, Raid>();
        public BidBotSettings Settings { get; set; } = new BidBotSettings();
    }
}
