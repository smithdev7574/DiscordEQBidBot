using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess.Entities
{
    public class MultiItemAuction
    {
        public Guid ID { get; set; }
        
        public int Duration { get; set; }
        public int RowNumber { get; set; }
        
        public ulong UserID { get; set; }

        public List<MultiItemAuctionItem> Items { get; set; } = new List<MultiItemAuctionItem>();
        public ulong GuildID { get; set; }
        public ulong? MessageID { get; set; }
        public ulong? ChannelID { get; set; }

        /// <summary>
        /// The name that matched several items (from the loot queue or the auction command).
        /// </summary>
        public string SearchName { get; set; } = string.Empty;

        public class MultiItemAuctionItem
        {
            public int ItemID { get; set; }
            public string ItemName { get; set; }
            public string ItemMarkdown { get; set; }
            public ulong? MessageID { get; set; }
        }
    }
}
