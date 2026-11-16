using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using BidBot.Infrastructure;
using BidBot.Logic;
using BidBot.Messaging;
using BidBot.Sheets;
using BidBot.Voice;
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
        public const string VOICE_TEST_COMMAND = "voicetest";
        public const string VOICE_TEXT_ARGUMENT = "text";
        public const string SIMULATE_LOOT_COMMAND = "simulateloot";
        public const string COUNT_ARGUMENT = "count";
        public const int MAX_SIMULATED_LOOT = 50;
        public const string SET_DURATION_COMMAND = "setduration";
        public const string MINUTES_ARGUMENT = "minutes";
        public const string SET_SPACING_COMMAND = "setspacing";
        public const string SECONDS_ARGUMENT = "seconds";
        public const string ADD_ITEM_COMMAND = "additem";
        public const string HOLDER_ARGUMENT = "holder";
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
                case VOICE_TEST_COMMAND:
                    await voiceTest(command);
                    break;
                case SIMULATE_LOOT_COMMAND:
                    await simulateLoot(command);
                    break;
                case SET_DURATION_COMMAND:
                    await setDuration(command);
                    break;
                case SET_SPACING_COMMAND:
                    await setSpacing(command);
                    break;
                case ADD_ITEM_COMMAND:
                    await addItem(command);
                    break;
            }
        }

        private async Task addItem(SocketSlashCommand command)
        {
            using var scope = scopeFactory.CreateScope();

            var permission = scope.ServiceProvider.GetRequiredService<PermissionCalculator>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();

            var (hasPermission, message) = permission.CheckManagerPermissions(command, ADD_ITEM_COMMAND);
            if (!hasPermission)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, message);
                await command.FollowupAsync(message, ephemeral: true);
                return;
            }

            var itemName = command.Data.Options.FirstOrDefault(x => x.Name == ITEM_NAME_ARGUMENT)?.Value?.ToString() ?? string.Empty;
            var holder = command.Data.Options.FirstOrDefault(x => x.Name == HOLDER_ARGUMENT)?.Value?.ToString();

            var result = scope.ServiceProvider.GetRequiredService<EQLogs.ManualEQLogService>().AddManualItem(itemName, holder);
            if (result.WasSuccessful)
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, result.Message);

            await command.FollowupAsync(result.Message, ephemeral: true);
        }

        private async Task setSpacing(SocketSlashCommand command)
        {
            using var scope = scopeFactory.CreateScope();

            var permission = scope.ServiceProvider.GetRequiredService<PermissionCalculator>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();

            var (hasPermission, message) = permission.CheckManagerPermissions(command, SET_SPACING_COMMAND);
            if (!hasPermission)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, message);
                await command.FollowupAsync(message, ephemeral: true);
                return;
            }

            var secondsStr = command.Data.Options.FirstOrDefault(x => x.Name == SECONDS_ARGUMENT)?.Value?.ToString();
            if (!int.TryParse(secondsStr, out var seconds))
            {
                await command.FollowupAsync("Seconds must be a whole number.", ephemeral: true);
                return;
            }

            // Discord enforces the minimum too, this catches anything that gets past it.
            var result = await scope.ServiceProvider.GetRequiredService<AuctionTimingService>().SetSpacingForActiveRaid(seconds);
            if (result.WasSuccessful)
            {
                // The loot queue post shows the spacing.
                scope.ServiceProvider.GetRequiredService<LootQueueService>().MarkDirty();
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, result.Message);
            }

            await command.FollowupAsync(result.Message, ephemeral: true);
        }

        private async Task setDuration(SocketSlashCommand command)
        {
            using var scope = scopeFactory.CreateScope();

            var permission = scope.ServiceProvider.GetRequiredService<PermissionCalculator>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();

            var (hasPermission, message) = permission.CheckManagerPermissions(command, SET_DURATION_COMMAND);
            if (!hasPermission)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, message);
                await command.FollowupAsync(message, ephemeral: true);
                return;
            }

            var minutesStr = command.Data.Options.FirstOrDefault(x => x.Name == MINUTES_ARGUMENT)?.Value?.ToString();
            if (!int.TryParse(minutesStr, out var minutes))
            {
                await command.FollowupAsync("Minutes must be a whole number.", ephemeral: true);
                return;
            }

            var result = await scope.ServiceProvider.GetRequiredService<AuctionTimingService>().SetDurationForActiveRaid(minutes);
            if (result.WasSuccessful)
            {
                // The loot queue post shows the auction length.
                scope.ServiceProvider.GetRequiredService<LootQueueService>().MarkDirty();
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, result.Message);
            }

            await command.FollowupAsync(result.Message, ephemeral: true);
        }

        private async Task simulateLoot(SocketSlashCommand command)
        {
            using var scope = scopeFactory.CreateScope();

            var permission = scope.ServiceProvider.GetRequiredService<PermissionCalculator>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();

            var (hasPermission, message) = permission.CheckManagerPermissions(command, SIMULATE_LOOT_COMMAND);
            if (!hasPermission)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, message);
                await command.FollowupAsync(message, ephemeral: true);
                return;
            }

            // Simulated loot is dropped the same way as real loot, which needs a raid and a current sheet.
            var activeRaid = store.ActiveRaid;
            if (activeRaid == null)
            {
                await command.FollowupAsync("There's no active raid. Run /setsheet with a test sheet first.", ephemeral: true);
                return;
            }

            if (!scope.ServiceProvider.GetRequiredService<GoogleSheetService>().IsSheetCurrent)
            {
                await command.FollowupAsync("The sheet was set more than 24 hours ago. Run /setsheet again first.", ephemeral: true);
                return;
            }

            var countStr = command.Data.Options.FirstOrDefault(x => x.Name == COUNT_ARGUMENT)?.Value?.ToString();
            if (!int.TryParse(countStr, out var count))
                count = 5;
            count = Math.Clamp(count, 1, MAX_SIMULATED_LOOT);

            var result = scope.ServiceProvider.GetRequiredService<EQLogs.ManualEQLogService>().AddSimulatedLoot(count);
            if (!result.WasSuccessful)
            {
                await command.FollowupAsync(result.Message, ephemeral: true);
                return;
            }

            var summary = $"{result.Message}, looted by **{EQLogs.ManualEQLogService.SIMULATED_LOOTER}**. They'll show up on the sheet and the Loot Queue within a few seconds:\n"
                + string.Join("\n", result.Items.Select(x => $"- {x}"));
            if (summary.Length > 1900)
                summary = summary.Substring(0, 1900) + "\n...";

            await communicationService.SendLogMessage(command.GuildId, command.User.Id, $"Simulated {result.Items.Count} loot drop(s) for testing.");
            await command.FollowupAsync(summary, ephemeral: true);
        }

        private async Task voiceTest(SocketSlashCommand command)
        {
            using var scope = scopeFactory.CreateScope();

            var permission = scope.ServiceProvider.GetRequiredService<PermissionCalculator>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();
            var voiceCommunication = scope.ServiceProvider.GetRequiredService<VoiceCommunicationService>();

            var (hasPermission, message) = permission.CheckManagerPermissions(command, VOICE_TEST_COMMAND);
            if (!hasPermission)
            {
                await communicationService.SendLogMessage(command.GuildId, command.User.Id, message);
                await command.FollowupAsync(message, ephemeral: true);
                return;
            }

            if (!command.GuildId.HasValue)
            {
                await command.FollowupAsync("Could not determine the guild for this command.", ephemeral: true);
                return;
            }

            var text = command.Data.Options.FirstOrDefault(x => x.Name == VOICE_TEXT_ARGUMENT)?.Value?.ToString();
            var result = voiceCommunication.AnnounceTest(command.GuildId.Value, text);

            if (!result.WasSuccessful)
            {
                await command.FollowupAsync($"Voice is not available: {result.Message}", ephemeral: true);
                return;
            }

            await command.FollowupAsync("Voice test queued, listen in the voice channel.", ephemeral: true);
        }

        private async Task endRaid(SocketSlashCommand command)
        {
            var activeRaid = store.ActiveRaid;
            if(activeRaid != null)
            {
                using var scope = scopeFactory.CreateScope();

                activeRaid.EndDT = DateTime.UtcNow;
                var task = store.SaveChanges(); // don't care about waiting...

                // Never fails or throws, voice problems can't stop the raid from ending.
                scope.ServiceProvider.GetRequiredService<VoiceCommunicationService>().EndRaidPresence();

                // Deletes waiting item pickers, resumes anything paused, removes the queue buttons and clears the queue.
                await scope.ServiceProvider.GetRequiredService<LootQueueService>().EndRaid(activeRaid.GuildID);

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

            // Not given (or invalid): the raid's default from /setduration, otherwise Settings:DefaultAuctionDuration.
            if (!int.TryParse(durationStr, out var duration) || duration <= 0)
                duration = scope.ServiceProvider.GetRequiredService<AuctionTimingService>().GetDefaultDuration();

            // Every item is on the sheet, the row is where the winner gets written.
            if (!int.TryParse(rowNumStr, out var rowNum) || rowNum <= 1)
            {
                await command.FollowupAsync("A valid sheet row number is required to start an auction.", ephemeral: true);
                return;
            }

            var result = await bidService.StartBidByName(command.GuildId.Value, command.User.Id, itemName, duration, rowNum);

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

                // Join the voice channel for the raid. Never fails or throws, voice problems can't stop the raid from starting.
                scope.ServiceProvider.GetRequiredService<VoiceCommunicationService>().StartRaidPresence(raid.GuildID, raid.StartDT);

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
