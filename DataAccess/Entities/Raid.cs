using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess.Entities
{
    public class Raid
    {
        public Guid RaidID { get; set; }
        public DateTime StartDT { get; set; }
        public DateTime? EndDT { get; set; }
        public ulong? MessageID { get; set; }
        public ulong? ItemMessageID { get; set; }
        public ulong? AuctionMessageID { get; set; }
        public ulong? ErrorMessageID { get; set; }

        public ulong GuildID { get; set; }
        public ulong? ChannelID { get; set; }

        public string SheetURL { get; set; }
        public string SheetID { get; set; }
        public List<Auction> Auctions { get; set; } = new List<Auction>();
        public List<(string item, string character)> LootedItems = new List<(string item, string character)>();
        public List<string> Errors { get; set; } = new List<string>();

    }
}
