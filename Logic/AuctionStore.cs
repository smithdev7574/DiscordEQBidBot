using BidBot.DataAccess.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Logic
{
    public class AuctionStore
    {
        private ConcurrentDictionary<Guid, Auction> auctions = new ConcurrentDictionary<Guid, Auction>();

        public AuctionStore()
        {
            
        }

        public void Add(Auction auction)
        {
            auctions[auction.AuctionID] = auction;
        }

        public Auction? TryGet(Guid auctionID)
        {
            auctions.TryGetValue(auctionID, out var bidMessage);
            return bidMessage;
        }

        public void Remove(Guid auctionID)
        {
            auctions.TryRemove(auctionID, out _);
        }

        public System.Collections.Generic.IReadOnlyCollection<Auction> GetAll()
        {
            return auctions.Values.ToList().AsReadOnly();
        }
    }
}
