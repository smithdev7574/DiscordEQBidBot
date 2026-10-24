using BidBot.DataAccess.Entities;
using BidBot.Infrastructure;
using BidBot.Logic;
using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace BidBot.Messaging
{
    public class ButtonHandler
    {
        public const string PLACE_BID = "placeBid";
        public const string CANCEL_BID = "cancelBid";
        public const string CANCEL_AUCTION = "cancelAuction";
        public const string MULTI_ITEM_PICK = "multiItemPick";
        private readonly IServiceScopeFactory scopeFactory;

        public ButtonHandler(IServiceScopeFactory scopeFactory)
        {
            this.scopeFactory = scopeFactory;
        }

        public async Task Handle(SocketMessageComponent component)
        {
            var identifiers = getIdentifiers(component.Data.CustomId);

            switch(identifiers.Prefix)
            {
                case PLACE_BID:
                    //Must respond with the modal as fast as possible, avoiding any additional code or calls out to dependencies for this case.
                    if (identifiers.AuctionID == null)
                    {
                        await component.RespondAsync("Unable to locate auction, please try again", ephemeral: true, flags: Discord.MessageFlags.Urgent);
                        return;
                    }
                    var mb = new ModalBuilder()
                        .WithTitle($"Place Bid")
                        .WithCustomId($"{ModalHandler.PLACE_BID}:{identifiers.AuctionID.Value}")
                        .AddTextInput("How much?", $"{ModalHandler.AMOUNT_FIELD}:{identifiers.AuctionID.Value}", TextInputStyle.Short, required: true);

                    await component.RespondWithModalAsync(mb.Build());
                    break;
                default:
                    //You must respond to the component within 3 seconds for Discord to be happy. This section is for processes that may need some additional work to respond.
                    await processDeferedHandler(component, identifiers);
                    break;
            }
        }

        private async Task processDeferedHandler(SocketMessageComponent component, (string Prefix, Guid? AuctionID) identifiers)
        {
            await component.DeferAsync(ephemeral: true);

            using var scope = scopeFactory.CreateScope();
            var bidService = scope.ServiceProvider.GetRequiredService<BidService>();
            var permissionCalculator = scope.ServiceProvider.GetRequiredService<PermissionCalculator>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();

            switch (identifiers.Prefix)
            {
                case CANCEL_BID:
                    if (identifiers.AuctionID == null)
                    {
                        await communicationService.SendLogMessage(component.GuildId, component.User.Id, "Unable to locate auction, please try again");
                        await component.FollowupAsync("Unable to locate auction, please try again", ephemeral: true);
                        return;
                    }

                    await bidService.RespondToCancelButton(identifiers.AuctionID.Value, component);
                    break;
                case CANCEL_AUCTION:
                    var permission = permissionCalculator.CheckManagerPermissions(component.User as IGuildUser, CANCEL_AUCTION);
                    if (!permission.HasPermission)
                    {
                        await communicationService.SendLogMessage(component.GuildId, component.User.Id, permission.Message);
                        await component.FollowupAsync(permission.Message, ephemeral: true);
                        return;
                    }
                    if (identifiers.AuctionID == null)
                    {
                        await communicationService.SendLogMessage(component.GuildId, component.User.Id, "Unable to locate auction, please try again");
                        await component.FollowupAsync("Unable to locate auction, please try again", ephemeral: true);
                        return;
                    }
                    await bidService.RespondToCancelAuctionButton(identifiers.AuctionID.Value, component);
                    break;
                case MULTI_ITEM_PICK:
                    if (identifiers.AuctionID == null)
                    {
                        await communicationService.SendLogMessage(component.GuildId, component.User.Id, "Unable to locate auction, please try again");
                        await component.FollowupAsync("Unable to locate auction, please try again", ephemeral: true);
                        return;
                    }

                    await bidService.ChooseMultiItem(identifiers.AuctionID.Value, component);
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
