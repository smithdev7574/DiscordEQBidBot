using BidBot.Logic;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Messaging
{
    public class ModalHandler
    {
        public const string PLACE_BID = "placebid";
        public const string AMOUNT_FIELD = "amount";
        private readonly IServiceScopeFactory scopeFactory;

        public ModalHandler(IServiceScopeFactory scopeFactory)
        {
            this.scopeFactory = scopeFactory;
        }

        public async Task Handle(SocketModal modal)
        {
            await modal.DeferAsync(ephemeral: true);

            using var scope = scopeFactory.CreateScope();
            var bidService = scope.ServiceProvider.GetRequiredService<BidService>();

            var identifiers = getIdentifiers(modal.Data.CustomId);
            switch (identifiers.Prefix)
            {
                case PLACE_BID:
                    if(identifiers.AuctionID == null)
                    {
                        await modal.RespondAsync("Auction ID missing from modal.", ephemeral: true);
                        return;
                    }
                    await bidService.RespondToBidModal(identifiers.AuctionID.Value, modal);
                    break;
                default:
                    await modal.FollowupAsync($"Unknown requests {identifiers.Prefix}", ephemeral: true);
                    break;
            }

        }

        private (string Prefix, Guid? AuctionID) getIdentifiers(string customID)
        {
            if (!customID.Contains(":"))
                return (string.Empty, null);

            var splitStr = customID.Split(":");
            var prefix = splitStr[0];
            var suffix = splitStr[1];

            if (!Guid.TryParse(suffix, out var auctionID))
                return (prefix, null);

            return (prefix, auctionID);
        }

    }
}
