using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess.Entities
{
    public sealed class WishlistEntry
    {
        public Guid ID { get; set; }
        public ulong UserID { get; set; }
        public ulong? GuildID { get; set; }
        public string ItemName { get; set; }
    }
}
