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

        /// <summary>
        /// Takes every item picker for a sheet row out of the store, so none of them can start an
        /// auction for that row any more. Returns them so their messages can be deleted.
        /// </summary>
        public List<MultiItemAuction> RemoveByRow(ulong guildID, int rowNumber)
        {
            return removeWhere(x => x.GuildID == guildID && x.RowNumber == rowNumber);
        }

        /// <summary>
        /// Takes every item picker for the guild out of the store, for /endraid.
        /// </summary>
        public List<MultiItemAuction> RemoveAll(ulong guildID)
        {
            return removeWhere(x => x.GuildID == guildID);
        }

        private List<MultiItemAuction> removeWhere(Func<MultiItemAuction, bool> predicate)
        {
            var removed = new List<MultiItemAuction>();
            foreach (var picker in auctions.Values.Where(predicate).ToList())
            {
                // TryRemove so a picker someone is clicking right now is only taken once.
                if (auctions.TryRemove(picker.ID, out var taken))
                    removed.Add(taken);
            }
            return removed;
        }

        public MultiItemAuction Find(Guid ID)
        {
            auctions.TryGetValue(ID, out var auction);
            return auction;
        }
    }
}
