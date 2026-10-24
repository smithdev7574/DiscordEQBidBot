using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess.Entities
{
    public sealed class ItemHtmlEntry
    {
        public int ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Html { get; set; } = string.Empty;
        public DateTimeOffset ScrapedAt { get; set; }
    }

}
