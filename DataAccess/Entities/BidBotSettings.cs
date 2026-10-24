using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess.Entities
{
    public class BidBotSettings
    {
        public string CurrentSheetID { get; set; }
        public DateTime SheetDT { get; set; }
        public ulong? GuildID { get;  set; }
    }
}
