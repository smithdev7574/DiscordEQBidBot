using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess.Entities
{
    public class Bidder
    {
        public ulong UserID { get; set; }
        public string Nickname { get; set; }
        public int Amount { get; set; }
        public DateTime BidDT { get; set; }
    }
}
