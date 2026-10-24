using BidBot.DataAccess.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess
{
    public class MultiItemAuctionStore
    {
        private ConcurrentDictionary<Guid, MultiItemAuction> auctions { get; set; } = new ConcurrentDictionary<Guid, MultiItemAuction>();

        public void Add(MultiItemAuction auction)
        {
            auctions[auction.ID] = auction;
        }

        public MultiItemAuction Remove(Guid ID)
        {
            auctions.TryRemove(ID, out var auction);
            return auction;
        }

        public MultiItemAuction Find(Guid ID)
        {
            auctions.TryGetValue(ID, out var auction);
            return auction;
        }
    }
}
