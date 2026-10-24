using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using BidBot.Infrastructure;
using BidBot.Logic;
using BidBot.Messaging;
using BidBot.Sheets;
using Discord.Rest;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Security;
using System.Text;

namespace BidBot.Messaging
{
    public class SlashCommandHandler
    {
        public const string START_AUCTION_COMMAND = "auction";
        public const string ITEM_NAME_ARGUMENT = "itemname";
        public const string DURATION_ARGUMENT = "duration";
        public const string WISH_LIST = "wishlist";
        public const string ROW_NUMBER_ARGUMENT = "rownumber";
        public const string SET_SHEET_COMMAND = "setsheet";
        public const string GOOGLE_SHEET_URL_ARGUMENT = "sheeturl";
        public const string END_RAID_COMMAND = "endraid";
        private readonly IServiceScopeFactory scopeFactory;
        private readonly BidbotDataStore store;

        public SlashCommandHandler(IServiceScopeFactory scopeFactory, BidbotDataStore store)
        {
            this.scopeFactory = scopeFactory;
            this.store = store;
        }

        public async Task HandleCommand(SocketSlashCommand command)
        {
            await command.DeferAsync(ephemeral: true);


            switch (command.Data.Name)
            {
                case START_AUCTION_COMMAND:
                    await beginBid(command);
                    break;
                case WISH_LIST:
                    await wishlist(command);
                    break;
                case SET_SHEET_COMMAND:
                    await setSheet(command);
                    break;
                case END_RAID_COMMAND:
                    await endRaid(command);
                    break;
            }
        }

        private async Task endRaid(SocketSlashCommand command)
        {
            var activeRaid = store.ActiveRaid;
            if(activeRaid != null)
            {
                using var scope = scopeFactory.CreateScope();

                activeRaid.EndDT = DateTime.UtcNow;
                var task = store.SaveChanges(); // don't care about waiting...

                var unixTime = new DateTimeOffset(activeRaid.EndDT.Value).ToUnixTimeSeconds() ;
                await command.FollowupAsync($"Ended Raid at: <t:{unixTime}:f>", ephemeral: true);
           
            }

            await command.FollowupAsync("No active raid", ephemeral: true);
        }

        private async Task beginBid(SocketSlashCommand command)
        {
            using var scope = scopeFactory.CreateScope();

            var permission = scope.ServiceProvider.GetRequiredService<PermissionCalculator>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();
            var bidService = scope.ServiceProvider.GetRequiredService<BidService>();

            var (hasPermission, message) = permission.CheckManagerPermissions(command, START_AUCTION_COMMAND);
            if (!hasPermission && !command.User.IsBot)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, message);
                await command.FollowupAsync(message, ephemeral: true);
                return;
            }

            var itemName = command.Data.Options.FirstOrDefault(x => x.Name == ITEM_NAME_ARGUMENT)?.Value?.ToString();
            var durationStr = command.Data.Options.FirstOrDefault(x => x.Name == DURATION_ARGUMENT)?.Value?.ToString();
            var rowNumStr = command.Data.Options.FirstOrDefault(x => x.Name == ROW_NUMBER_ARGUMENT)?.Value?.ToString();

            if (!command.GuildId.HasValue || !command.ChannelId.HasValue)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, "Could not determine the guild or channel for this command.");
                await command.FollowupAsync("Could not determine the guild or channel for this command.", ephemeral: true);
                return;
            }

            if (string.IsNullOrEmpty(itemName))
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, "Item name is required to start a bid.");
                await command.FollowupAsync("Item name is required to start a bid.", ephemeral: true);
                return;
            }

            if (!int.TryParse(durationStr, out var duration))
                duration = 3; //Default duration if not provided or invalid

            int? nullableRowNum = null;
            if (int.TryParse(rowNumStr, out var rowNum))
                nullableRowNum = rowNum;


            var result = await bidService.StartBidByName(command.GuildId.Value, command.User.Id, itemName, duration, nullableRowNum);

            if (!result.WasSuccess)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, $"Failed to Create Auction: {result.Message}");
                await command.FollowupAsync($"Failed to Create Auction: {result.Message}", ephemeral: true);
                return;
            }

            await command.FollowupAsync(result.Message, ephemeral: true);
        }

        private async Task wishlist(SocketSlashCommand command)
        {
            using var scope = scopeFactory.CreateScope();
            var wishlistService = scope.ServiceProvider.GetRequiredService<WishlistService>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();

            var itemName = command.Data.Options.FirstOrDefault(x => x.Name == ITEM_NAME_ARGUMENT)?.Value?.ToString();

            var result = await wishlistService.AddOrRemove(command.User.Id, command.GuildId, itemName);

            if (!result.WasSuccessful)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, $"Failed to update Wishlist: {result.Message}");
                await command.FollowupAsync($"Failed to update Wishlist: {result.Message}");
                return;
            }

            await command.FollowupAsync(result.Message, ephemeral: true);

        }

        private async Task setSheet(SocketSlashCommand command)
        {
            using var scope = scopeFactory.CreateScope();
            var sheetService = scope.ServiceProvider.GetRequiredService<GoogleSheetService>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();


            var url = command.Data.Options.FirstOrDefault(x => x.Name == GOOGLE_SHEET_URL_ARGUMENT)?.Value?.ToString();

            try
            {
                var sheetID = await sheetService.SetSheet(url, command.GuildId);

                var raid = new Raid()
                {
                    StartDT = DateTime.UtcNow,
                    RaidID = Guid.NewGuid(),
                    SheetID = sheetID,
                    SheetURL = url,
                    GuildID = command.GuildId.Value,
                };

                store.Raids.TryAdd(raid.RaidID, raid);
                var task = store.SaveChanges(); //Don't care about waiting...

            }
            catch(Exception ex)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, $"Failed to set Sheet: {ex.Message}");
                await command.FollowupAsync($"Failed to set Sheet: {ex.Message}");
                return;
            }

            await command.FollowupAsync($"The Sheet for the night has been set to: {url} ");
        }
    }
}
