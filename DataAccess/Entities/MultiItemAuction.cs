using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess.Entities
{
    public class MultiItemAuction
    {
        public Guid ID { get; set; }
        
        public int Duration { get; set; }
        public int? RowNumber { get; set; }
        
        public ulong UserID { get; set; }

        public List<MultiItemAuctionItem> Items { get; set; } = new List<MultiItemAuctionItem>();
        public ulong GuildID { get; internal set; }
        public ulong? MessageID { get; internal set; }
        public ulong? ChannelID { get; internal set; }

        public class MultiItemAuctionItem
        {
            public int ItemID { get; set; }
            public string ItemName { get; set; }
            public string ItemMarkdown { get; set; }
            public ulong? MessageID { get; set; }
        }
    }
}
