using BidBot.DataAccess.Entities;
using Discord;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Logic
{
    public class BidDiscordMessageFactory
    {
        public BidDiscordMessageFactory()
        {
            
        }

        public string CreateDiscordText(Auction auction)
        {
            var sb = new StringBuilder();
            // The sheet row is in the title, so it shows while bidding and on the finished message.
            var row = auction.RowNumber.HasValue ? $" · Row {auction.RowNumber}" : string.Empty;
            sb.AppendLine($"## [{auction.Item.Name}]({auction.Item.LucyLink}){row}");

            if (auction.IsClosed)
            {
                // Finished auctions shrink to just the name and result, to keep the bid channel short.
                if(!string.IsNullOrWhiteSpace(auction.Winner))
                    sb.AppendLine($"**WINNER:** {auction.Winner} for **{auction.WinAmount.ToString("#,###")}** platinum.");
                else
                    sb.AppendLine("**WINNER:** No one was interested.");

                return sb.ToString();
            }


            if (auction.IsPaused)
            {
                sb.AppendLine((auction.ItemMarkdown ?? string.Empty).Trim());
                sb.AppendLine();
                sb.AppendLine("**Paused** ⏸️ The timer is stopped until the officers resume auctions. Bids still count.");
            }
            else
            {
                var itemText = CreateDiscordText(auction.ItemMarkdown, auction.EndDT);
                sb.AppendLine(itemText);
            }
            sb.AppendLine("-------------------------------------------------");
            return sb.ToString();
        }

        public string CreateDiscordText(string itemMarkdown, DateTime endDT)
        {
            if (itemMarkdown == null) itemMarkdown = string.Empty;

            // Treat endDT as UTC to avoid timezone surprises; caller should provide UTC.
            var now = DateTime.UtcNow;
            var remaining = endDT.ToUniversalTime() - now;

            var sb = new StringBuilder();


            sb.AppendLine(itemMarkdown.Trim());
            sb.AppendLine();

            if (remaining <= TimeSpan.Zero)
            {
                // Bid finished
                sb.AppendLine("**The bid is over** ⏰");
            }
            else
            {
                // Give a deliberately vague remaining time.
                if (remaining <= TimeSpan.FromSeconds(15))
                {
                    sb.AppendLine("**Remaining:** hurry a few seconds... ⏳");
                }
                if(remaining <= TimeSpan.FromSeconds(30) && remaining > TimeSpan.FromSeconds(15))
                {
                    sb.AppendLine("**Remaining:** almost done... ⏳");
                }
                else if (remaining <= TimeSpan.FromSeconds(60) && remaining > TimeSpan.FromSeconds(30))
                {
                    sb.AppendLine("**Remaining:** less than a minute... ⏳");
                }
                else if (remaining.TotalMinutes >= 1)
                {
                    // When more than a minute left, round to minutes:
                    // - if the seconds within the minute are more than 30 -> round up
                    // - otherwise -> round down
                    var wholeMinutes = (int)Math.Floor(remaining.TotalMinutes);
                    var seconds = remaining.Seconds;
                    var roundedMinutes = seconds > 30 ? wholeMinutes + 1 : wholeMinutes;
                    var minuteLabel = roundedMinutes == 1 ? "minute" : "minutes";
                    sb.AppendLine($"**Remaining:** about {roundedMinutes} {minuteLabel} ⏳");
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
