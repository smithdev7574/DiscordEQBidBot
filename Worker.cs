using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BidBot.Infrastructure;
using BidBot.Items;
using System.Text;
using Discord.Net;
using BidBot.Messaging;
using BidBot.DataAccess;
using BidBot.Logic;
using BidBot.EQLogs;

namespace BidBot
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> logger;
        private readonly DiscordSocketClient client;
        private readonly IConfiguration configuration;
        private readonly IServiceScopeFactory scopeFactory;
        private readonly ItemListStore itemListStore;
        private readonly ButtonHandler buttonHandler;
        private readonly ModalHandler modalHandler;

        public Worker(ILogger<Worker> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            DiscordSocketClient client,
            ItemListStore itemListStore,
            ButtonHandler buttonHandler,
            ModalHandler modalHandler)
        {
            this.logger = logger;
            this.client = client;
            this.configuration = configuration;
            this.scopeFactory = scopeFactory;
            this.itemListStore = itemListStore;
            this.buttonHandler = buttonHandler;
            this.modalHandler = modalHandler;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // Load the item list at startup and populate the singleton store
            try
            {
                using (var scope = scopeFactory.CreateScope())
                {
                    var parser = scope.ServiceProvider.GetRequiredService<ItemListParser>();
                    var list = await parser.Load();
                    itemListStore.Set(list);
                    logger.LogInformation("Loaded {Count} items from item list.", list.Items.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load item list at startup.");
            }

            client.ModalSubmitted += Client_ModalSubmitted;
            client.Ready += Client_Ready;
            client.SlashCommandExecuted += Client_SlashCommandExecuted;
            client.SelectMenuExecuted += Client_SelectMenuExecuted;
            client.ButtonExecuted += Client_ButtonExecuted;
            client.AutocompleteExecuted += Client_AutocompleteExecuted;
            client.MessageReceived += Client_MessageReceived;

            await base.StartAsync(cancellationToken);
        }

        private async Task Client_MessageReceived(SocketMessage arg)
        {            
            var message = arg as SocketUserMessage;
            if (message == null)
                return;

            var prefix = configuration["Settings:MessagePrefix"];
            if (!message.Content.StartsWith(prefix))
                return;

            var content = message.Content[prefix.Length..].Trim();


            var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            var guildChannel = message.Channel as SocketGuildChannel;
            if (guildChannel == null)
                return;

            using var scope = scopeFactory.CreateScope();
            

            var command = parts[0].ToLower();
            var args = parts.Skip(1).ToArray();

            switch(command)
            {
                case "auction":
                    if (args.Length < 3)
                        return;

                    if (!int.TryParse(args[0], out var duration))
                        return;

                    if (!int.TryParse(args[1], out var rowNumber))
                        return;

                    var itemName = string.Join(" ", args.Skip(2));
                    var bidService = scope.ServiceProvider.GetRequiredService<BidService>();
                    await bidService.StartBidByName(guildChannel.Guild.Id, message.Author.Id, itemName, duration, rowNumber);
                    await message.DeleteAsync();
                    break;
                case "manuallog":
                    var manualLogService = scope.ServiceProvider.GetRequiredService<ManualEQLogService>();
                    if(message.Attachments.Any())
                    {
                        manualLogService.AddManualLogsFromFile(message.Attachments.First().Url);
                    }
                    else
                    {
                        var logs = string.Join(" ", args);
                        manualLogService.AddManualLogs(logs);
                    }
                    await message.DeleteAsync();
                    break;
                case "additem":
                    // !bb additem <item name> [| <holder>]
                    var addText = string.Join(" ", args);
                    var separator = addText.IndexOf('|');
                    var addItemName = separator >= 0 ? addText[..separator] : addText;
                    var holder = separator >= 0 ? addText[(separator + 1)..] : null;

                    var manualService = scope.ServiceProvider.GetRequiredService<ManualEQLogService>();
                    var addResult = manualService.AddManualItem(addItemName, holder);
                    await message.DeleteAsync();
                    await message.Channel.SendMessageAsync(addResult.WasSuccessful ? $"✅ {addResult.Message}" : $"⚠️ {addResult.Message}");
                    break;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Keep the worker alive. Work is handled via the MessageReceived handler.
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }


        private async Task Client_Ready()
        {
            List<ApplicationCommandProperties> globalCommands = new List<ApplicationCommandProperties>();
            await client.BulkOverwriteGlobalApplicationCommandsAsync(globalCommands.ToArray());

            var bidCommand = new SlashCommandBuilder();
            bidCommand.WithName(SlashCommandHandler.START_AUCTION_COMMAND);
            bidCommand.WithDescription("Starts an auction for an item.");
            bidCommand.AddOption(new SlashCommandOptionBuilder()
                .WithName(SlashCommandHandler.ITEM_NAME_ARGUMENT)
                .WithDescription("The name of the item to auction")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.String)
                .WithAutocomplete(true));

            //bidCommand.AddOption(SlashCommandHandler.ITEM_NAME_ARGUMENT, ApplicationCommandOptionType.String, "The name of the item to auction.", isRequired: true);
            // Discord requires required options to come before optional ones.
            bidCommand.AddOption(SlashCommandHandler.ROW_NUMBER_ARGUMENT, ApplicationCommandOptionType.Integer, "The row of the item on the sheet.", isRequired: true);
            bidCommand.AddOption(SlashCommandHandler.DURATION_ARGUMENT, ApplicationCommandOptionType.Integer, "The duration of the auction in minutes. Defaults to the raid's /setduration.", isRequired: false);

            var sheetCommand = new SlashCommandBuilder();
            sheetCommand.WithName(SlashCommandHandler.SET_SHEET_COMMAND);
            sheetCommand.WithDescription("Sets the current sheet where auctions are being tracked.");
            sheetCommand.AddOption(new SlashCommandOptionBuilder()
                .WithName(SlashCommandHandler.GOOGLE_SHEET_URL_ARGUMENT)
                .WithDescription("The URL of the google sheet.")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.String));

            var endRaidCommand = new SlashCommandBuilder();
            endRaidCommand.WithName(SlashCommandHandler.END_RAID_COMMAND);
            endRaidCommand.WithDescription("Ends the raid so the bot doen't keep doing stuff...");

            var wishListCommand = new SlashCommandBuilder();
            wishListCommand.WithName(SlashCommandHandler.WISH_LIST);
            wishListCommand.WithDescription("Adds or Removes an item from your wishlist");
            wishListCommand.AddOption(new SlashCommandOptionBuilder()
                .WithName(SlashCommandHandler.ITEM_NAME_ARGUMENT)
                .WithDescription("The name of the item to add or remove from your wishlist.")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.String)
                .WithAutocomplete(true));

            var voiceTestCommand = new SlashCommandBuilder();
            voiceTestCommand.WithName(SlashCommandHandler.VOICE_TEST_COMMAND);
            voiceTestCommand.WithDescription("Has the bot join the voice channel and say something.");
            voiceTestCommand.AddOption(SlashCommandHandler.VOICE_TEXT_ARGUMENT, ApplicationCommandOptionType.String, "What the bot should say.", isRequired: false);

            var simulateLootCommand = new SlashCommandBuilder();
            simulateLootCommand.WithName(SlashCommandHandler.SIMULATE_LOOT_COMMAND);
            simulateLootCommand.WithDescription("Testing: adds random items from past raids to the current raid as if they were looted.");
            simulateLootCommand.AddOption(new SlashCommandOptionBuilder()
                .WithName(SlashCommandHandler.COUNT_ARGUMENT)
                .WithDescription("How many items to loot (default 5).")
                .WithRequired(false)
                .WithType(ApplicationCommandOptionType.Integer)
                .WithMinValue(1)
                .WithMaxValue(SlashCommandHandler.MAX_SIMULATED_LOOT));

            var setDurationCommand = new SlashCommandBuilder();
            setDurationCommand.WithName(SlashCommandHandler.SET_DURATION_COMMAND);
            setDurationCommand.WithDescription("Sets how long auctions run by default for the rest of this raid.");
            setDurationCommand.AddOption(new SlashCommandOptionBuilder()
                .WithName(SlashCommandHandler.MINUTES_ARGUMENT)
                .WithDescription("Auction length in minutes.")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer)
                .WithMinValue(AuctionTimingService.MIN_MINUTES)
                .WithMaxValue(AuctionTimingService.MAX_MINUTES));

            var setSpacingCommand = new SlashCommandBuilder();
            setSpacingCommand.WithName(SlashCommandHandler.SET_SPACING_COMMAND);
            setSpacingCommand.WithDescription("Sets how many seconds apart the Loot Queue starts auctions for the rest of this raid.");
            setSpacingCommand.AddOption(new SlashCommandOptionBuilder()
                .WithName(SlashCommandHandler.SECONDS_ARGUMENT)
                .WithDescription($"Seconds between auctions, at least {AuctionTimingService.MIN_SPACING_SECONDS}.")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.Integer)
                .WithMinValue(AuctionTimingService.MIN_SPACING_SECONDS)
                .WithMaxValue(AuctionTimingService.MAX_SPACING_SECONDS));

            var addItemCommand = new SlashCommandBuilder();
            addItemCommand.WithName(SlashCommandHandler.ADD_ITEM_COMMAND);
            addItemCommand.WithDescription("Adds an item to the sheet and the Loot Queue as if it was looted.");
            addItemCommand.AddOption(new SlashCommandOptionBuilder()
                .WithName(SlashCommandHandler.ITEM_NAME_ARGUMENT)
                .WithDescription("The name of the item to add")
                .WithRequired(true)
                .WithType(ApplicationCommandOptionType.String)
                .WithAutocomplete(true));
            addItemCommand.AddOption(SlashCommandHandler.HOLDER_ARGUMENT, ApplicationCommandOptionType.String, $"Who is holding the item (default {ManualEQLogService.MANUAL_LOOTER}).", isRequired: false);

            foreach (var guild in client.Guilds)
            {
                try
                {
                    List<ApplicationCommandProperties> commands = new List<ApplicationCommandProperties>();
                    commands.Add(bidCommand.Build());
                    commands.Add(wishListCommand.Build());
                    commands.Add(sheetCommand.Build());
                    commands.Add(endRaidCommand.Build());
                    commands.Add(voiceTestCommand.Build());
                    commands.Add(simulateLootCommand.Build());
                    commands.Add(setDurationCommand.Build());
                    commands.Add(setSpacingCommand.Build());
                    commands.Add(addItemCommand.Build());
                    await guild.BulkOverwriteApplicationCommandAsync(commands.ToArray());
                    await guild.GetApplicationCommandAsync(guild.Id);

                }
                catch (HttpException ex)
                {
                    logger.LogError(ex, "Error occurred while creating application command.");
                }
            }
        }

        private async Task Client_SlashCommandExecuted(SocketSlashCommand arg)
        {
            using (var scope = scopeFactory.CreateScope())
            {
                var handler = scope.ServiceProvider.GetRequiredService<SlashCommandHandler>();
                await handler.HandleCommand(arg);
            }
        }

        private async Task Client_SelectMenuExecuted(SocketMessageComponent arg)
        {
            using (var scope = scopeFactory.CreateScope())
            {
                var handler = scope.ServiceProvider.GetRequiredService<SelectHandler>();
                await handler.Handle(arg);
            }
        }

        private async Task Client_ButtonExecuted(SocketMessageComponent arg)
        {
            await buttonHandler.Handle(arg);
        }
        private async Task Client_ModalSubmitted(SocketModal arg)
        {
            await modalHandler.Handle(arg);
        }

        private async Task Client_AutocompleteExecuted(SocketAutocompleteInteraction arg)
        {
            using (var scope = scopeFactory.CreateScope())
            {
                try
                {
                    var handler = scope.ServiceProvider.GetRequiredService<AutoCompleteHandler>();
                    var data = await handler.GetAutoComplete(arg);

                    await arg.RespondAsync(data);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error with Auto Complete:");
                }
            }
        }


    }
}
