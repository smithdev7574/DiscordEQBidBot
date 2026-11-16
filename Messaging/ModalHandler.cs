using BidBot.Infrastructure;
using BidBot.Logic;
using Discord;
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
        public const string QUEUE_REMOVE = "queueRemove";
        public const string ROW_FIELD = "row";
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
                case QUEUE_REMOVE:
                    await removeFromQueue(scope, modal);
                    break;
                default:
                    await modal.FollowupAsync($"Unknown requests {identifiers.Prefix}", ephemeral: true);
                    break;
            }

        }

        private async Task removeFromQueue(IServiceScope scope, SocketModal modal)
        {
            var permission = scope.ServiceProvider.GetRequiredService<PermissionCalculator>()
                .CheckManagerPermissions(modal.User as IGuildUser, "Remove Item");
            // Replies delete themselves so they don't pile up under the Loot Queue post.
            if (!permission.HasPermission)
            {
                await modal.FollowupThenExpire(permission.Message, InteractionExtensions.ErrorReply);
                return;
            }

            var rowResult = modal.TryGetRequiredInt(ROW_FIELD, out var rowNumber);
            if (!rowResult.WasSuccessful)
            {
                await modal.FollowupThenExpire(rowResult.Message, InteractionExtensions.ErrorReply);
                return;
            }

            var lootQueue = scope.ServiceProvider.GetRequiredService<LootQueueService>();
            var result = await lootQueue.RemoveRow(rowNumber);
            await lootQueue.RefreshPost();

            await scope.ServiceProvider.GetRequiredService<ICommunicationService>().SendLogMessage(modal.GuildId, modal.User.Id, result.Message);
            await modal.FollowupThenExpire(result.Message, result.WasSuccessful ? InteractionExtensions.ShortReply : InteractionExtensions.ErrorReply);
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
