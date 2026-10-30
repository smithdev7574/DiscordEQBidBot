using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using BidBot.Infrastructure;
using BidBot.Items;
using BidBot.Messaging;
using BidBot.Sheets;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace BidBot.Logic
{
    public class BidService
    {
        private readonly IConfiguration config;
        private readonly ItemStatsScraper scraper;
        private readonly DiscordItemFactory itemFactory;
        private readonly ItemListStore itemListStore;
        private readonly BidDiscordMessageFactory messageFactory;
        private readonly AuctionStore auctionStore;
        private readonly BidbotDataStore dataStore;
        private readonly ICommunicationService communication;
        private readonly SelectFactory selectFactory;
        private readonly UserService userService;
        private readonly WishlistService wishlistService;
        private readonly MultiItemAuctionStore multiStore;
        private readonly PermissionCalculator permissionCalculator;
        private readonly GoogleSheetService sheetService;

        public BidService(IConfiguration config,
            ItemStatsScraper scraper,
            DiscordItemFactory itemFactory,
            ItemListStore itemListStore,
            BidDiscordMessageFactory messageFactory,
            AuctionStore auctionStore,
            BidbotDataStore dataStore,
            ICommunicationService communication,
            SelectFactory selectFactory,
            UserService userService,
            WishlistService wishlistService,
            MultiItemAuctionStore multiStore,
            PermissionCalculator permissionCalculator,
            GoogleSheetService sheetService)
        {
            this.config = config;
            this.scraper = scraper ?? throw new ArgumentNullException(nameof(scraper));
            this.itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));
            this.itemListStore = itemListStore ?? throw new ArgumentNullException(nameof(itemListStore));
            this.messageFactory = messageFactory;
            this.auctionStore = auctionStore;
            this.dataStore = dataStore;
            this.communication = communication;
            this.selectFactory = selectFactory;
            this.userService = userService;
            this.wishlistService = wishlistService;
            this.multiStore = multiStore;
            this.permissionCalculator = permissionCalculator;
            this.sheetService = sheetService;
        }

        public async Task<(bool WasSuccess, string Message)> StartBidByName(ulong guildID, ulong userID, string itemName, int duration, int? rowNumber)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                throw new ArgumentNullException(nameof(itemName));

            var bidChannel = config?["Settings:BidChannelName"];
            if (string.IsNullOrWhiteSpace(bidChannel))
            {
                return (false, "Bid channel name is not configured.");
            }

            var store = itemListStore.Get();
            var items = store.FindByName(itemName);

            if (!items.Any())
                return (false, $"Could not find an item named '{itemName}'");

            if (items.Count() > 1)
            {
                var multiItemAuction = new MultiItemAuction()
                {
                    ID = Guid.NewGuid(),
                    Duration = duration,
                    UserID = userID,
                    GuildID = guildID,
                    RowNumber = rowNumber
                };

                var mention = MentionUtils.MentionUser(userID);
                var mainMessage = await communication.SendMessageByChannelName(guildID, bidChannel, $"{mention} There are multiple entries for {itemName}, please pick one below!");
                
                multiItemAuction.MessageID = mainMessage?.Id;
                multiItemAuction.ChannelID = mainMessage?.Channel.Id;

                foreach (var item in items)
                {
                    var multiItem = new MultiItemAuction.MultiItemAuctionItem()
                    {
                        ItemID = item.ID,
                        ItemMarkdown = await getItemMarkdown(item),
                        ItemName = item.Name,
                    };


                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("----------------------------------------------------");

                    var abbreviatedMarkdown = await getItemMarkdown(item);
                    sb.AppendLine($"Item ID: {item.ID}");
                    sb.AppendLine(abbreviatedMarkdown);

                    var builder = new ComponentBuilder();
                    builder.WithButton(FunnyButtonPhrases.GetRandom(), $"{ButtonHandler.MULTI_ITEM_PICK}:{multiItemAuction.ID}:{item.ID}", ButtonStyle.Success);
                    var component = builder.Build();

                    var itemMessage = await communication.SendMessageByChannelName(guildID, bidChannel, sb.ToString(), component);

                    multiItem.MessageID = itemMessage.Id;
                    multiItemAuction.Items.Add(multiItem);
                }

                multiStore.Add(multiItemAuction);

                return (true, "Found multiple items, choose one...");
            }

            return await StartBid(guildID, userID, items.First().ID, duration, rowNumber);
        }

        public async Task<(bool WasSuccess, string Message)> StartBid(ulong guildID, ulong userID, int itemID, int duration, int? rowNumber)
        {
            var store = itemListStore.Get();
            if (!store.TryGetById(itemID, out var item) || item == null)
                return (false, $"Could not find an item with ID '{itemID}'");


            var bidChannel = config?["Settings:BidChannelName"];
            if(string.IsNullOrWhiteSpace(bidChannel))
            {
                return (false, "Bid channel name is not configured.");
            }

            var endDT = DateTime.UtcNow.AddMinutes(duration);

            var itemMarkdown = await getItemMarkdown(item);
            

            var auction = new Auction()
            {
                GuildID = guildID,
                UserID = userID,
                ItemMarkdown = itemMarkdown,
                Duration = duration,
                EndDT = endDT,
                Item = item,
                RowNumber = rowNumber
            };

            var messageMarkdown = messageFactory.CreateDiscordText(auction);

            var builder = new ComponentBuilder();
            builder.WithButton("Place Bid", $"{ButtonHandler.PLACE_BID}:{auction.AuctionID}", ButtonStyle.Primary);
            builder.WithButton("Cancel Bid", $"{ButtonHandler.CANCEL_BID}:{auction.AuctionID}", ButtonStyle.Danger);
            builder.WithButton("Cancel Auction", $"{ButtonHandler.CANCEL_AUCTION}:{auction.AuctionID}", ButtonStyle.Secondary);
            var component = builder.Build();
            var userMessage = await communication.SendMessageByChannelName(guildID, bidChannel, messageMarkdown, component, flags: MessageFlags.SuppressEmbeds);

            auction.MessageID = userMessage.Id;
            auction.ChannelID = userMessage.Channel.Id;

            auctionStore.Add(auction);

            var task = wishlistService.NotifyUsers(auction.Item.Name);
            return (true, $"Bid Started for {item.Name}");
        }

        public async Task RespondToCancelButton(Guid auctionID, SocketMessageComponent component)
        {
            var auction = auctionStore.TryGet(auctionID);
            if (auction == null)
            {
                await communication.SendLogMessage(component.GuildId, component.User.Id, $"Unable to locatate auction with ID: {auctionID}");
                await component.FollowupAsync($"Unable to locatate auction with ID: {auctionID}");
                return;
            }

            var result = await auction.RemoveBid(component.User.Id);
            
            if (!result.WasSuccessful)
            {
                await communication.SendLogMessage(component.GuildId, component.User.Id, $"**WARNING!** Cancel Failed for {auction.ItemName}: {result.Message}");
                await communication.SendTell(component.User.Id, $"**WARNING!** Cancel Failed for {auction.ItemName}: {result.Message}");
                return;
            }

            await communication.SendTell(component.User.Id, $"Cancelled bid for {auction.ItemName}");
            await component.FollowupAsync($"Cancelled bid for {auction.ItemName}.", ephemeral: true);
        }


        public async Task RespondToBidModal(Guid auctionID, SocketModal modal)
        {
            var modalResponse = modal.TryGetRequiredInt(ModalHandler.AMOUNT_FIELD, out var amount);
            if(!modalResponse.WasSuccessful)
            {
                await communication.SendLogMessage(modal.GuildId, modal.User.Id, modalResponse.Message);
                await modal.FollowupAsync(modalResponse.Message, ephemeral: true);
                return;
            }

            if(amount <= 0)
            {
                await communication.SendLogMessage(modal.GuildId, modal.User.Id, "Bid must be greater than zero...");
                await modal.FollowupAsync("Bid must be greater than zero...", ephemeral: true);
                return;
            }

            var auction = auctionStore.TryGet(auctionID);
            if (auction == null)
            {
                await communication.SendLogMessage(modal.GuildId, modal.User.Id, $"**WARNING!** Bid Failed: Unable to locatate auction with ID: {auctionID}");
                await modal.FollowupAsync($"**WARNING!** Bid Failed: Unable to locatate auction with ID: {auctionID}", ephemeral: true);
                return;
            }

            var bidder = new Bidder()
            {
                Amount = amount,
                BidDT = DateTime.UtcNow,
                UserID = modal.User.Id,
                Nickname = userService.GetUsername(auction.GuildID, modal.User.Id)
            };

            var result = await auction.AddBid(bidder);
            if(!result.WasSuccessful)
            {
                await communication.SendLogMessage(modal.GuildId, modal.User.Id, $"**WARNING!** Bid Failed for {auction.ItemName}: {result.Message}");
                await modal.FollowupAsync($"**WARNING!** Bid Failed for {auction.ItemName}: {result.Message}", ephemeral: true);
                return;
            }

            await communication.SendTell(modal.User.Id, $"Bid accepted for {auction.ItemName} - {amount.ToString("#,###")} Plat.");
            await modal.FollowupAsync($"Bid accepted for {auction.ItemName} - {amount.ToString("#,###")} Plat.", ephemeral: true);
        }

        public async Task FinishAuction(Auction auction)
        {
            auction.LockAuction();
            var bidders = auction.GetAllBidders();
            var winner = bidders.OrderByDescending(x => x.Amount).ThenBy(x => x.BidDT).FirstOrDefault();

            var awardChannel = config?["Settings:AwardChannelName"];

            if (winner == null)
            {
                auctionStore.Remove(auction.AuctionID);
                await communication.SendMessageByChannelName(auction.GuildID, awardChannel, auction.GetFinishMessage());
                return;
            }

            int winningAmount = 1;

            var nextClosestBidder = bidders.Where(x => x.UserID != winner.UserID).OrderByDescending(x => x.Amount).ThenBy(x => x.BidDT).FirstOrDefault();
            if(nextClosestBidder != null)
            {
                winningAmount = nextClosestBidder.Amount + 1;
            }

            auction.Winner = winner.Nickname;
            auction.WinAmount = winningAmount;

            auctionStore.Remove(auction.AuctionID);

            auction.FinalBids = bidders.ToList();
            dataStore.HistoricalAuctions[auction.AuctionID] = auction;

            var activeRaid = dataStore.ActiveRaid;
            if(activeRaid != null)
            {
                activeRaid.Auctions.Add(auction);
            }
            var task = dataStore.SaveChanges();

            await communication.SendMessageByChannelName(auction.GuildID, awardChannel, auction.GetFinishMessage());
            await communication.SendTell(winner.UserID, $"You won **{auction.Item.Name}** for {winningAmount.ToString("#,###")} platinum.");

            var sheetResult = await sheetService.UpdateWinner(auction);
            if(!sheetResult.WasSuccessful)
            {
                await communication.SendMessageByChannelName(auction.GuildID, awardChannel, $"Issue Updating the Sheet: {sheetResult.Message}");
            }

        }

        public async Task ChooseMultiItem(Guid multiItemID, SocketMessageComponent component)
        {
            var multiAuction = multiStore.Find(multiItemID);
            if (multiAuction == null)
            {
                await communication.SendLogMessage(component.GuildId, component.User.Id, $"Could not find Multi Item Auction ID: {multiItemID}");
                await component.FollowupAsync($"Could not find Multi Item Auction ID: {multiItemID}", ephemeral: true);
                return;
            }

            var permissionResult = permissionCalculator.CheckManagerPermissions(component.User as IGuildUser, "ChooseMultiItem");
            if(!permissionResult.HasPermission)
            {
                await communication.SendLogMessage(component.GuildId, component.User.Id, $"You aren't an officer, no soup for you!");
                await component.FollowupAsync($"You aren't an officer, no soup for you!", ephemeral: true);
                return;
            }

            var split = component.Data.CustomId.Split(":");
            if(split.Length < 3 || !int.TryParse(split[2], out var itemID))
            {
                await communication.SendLogMessage(component.GuildId, component.User.Id, $"Malformed button, this isn't going to work..");
                await component.FollowupAsync($"Malformed button, this isn't going to work..", ephemeral: true);
                return;
            }

            var result =  await StartBid(multiAuction.GuildID, multiAuction.UserID, itemID, multiAuction.Duration, multiAuction.RowNumber);
            if(!result.WasSuccess)
            {
                await communication.SendLogMessage(component.GuildId, component.User.Id, result.Message);
                await component.FollowupAsync(result.Message, ephemeral: true);
                return;
            }

            await component.FollowupAsync(result.Message, ephemeral: true);

            multiStore.Remove(multiItemID);
            await communication.DeleteMessage(multiAuction.GuildID, multiAuction.ChannelID ?? 0, multiAuction.MessageID ?? 0);
            foreach(var item in multiAuction.Items)
            {
                await communication.DeleteMessage(multiAuction.GuildID, multiAuction.ChannelID ?? 0, item.MessageID ?? 0);
            }
        }

        public async Task RespondToCancelAuctionButton(Guid auctionID, SocketMessageComponent component)
        {
            var auction = auctionStore.TryGet(auctionID);
            if (auction == null)
            {
                await communication.SendLogMessage(component.GuildId, component.User.Id, $"Unable to locatate auction with ID: {auctionID}");
                await component.FollowupAsync($"Unable to locatate auction with ID: {auctionID}");
                return;
            }

            auction.CancelledDT = DateTime.UtcNow;
            auctionStore.Remove(auction.AuctionID);

            var mention = MentionUtils.MentionUser(component.User.Id);
            await component.FollowupAsync($"{mention} Cancelled the Auction for {auction.Item.Name}.");
            await communication.DeleteMessage(auction.GuildID, auction.ChannelID, auction.MessageID);

        }

        private async Task<string> getItemMarkdown(ItemEntry item)
        {
            var html = await scraper.GetTableHtmlById(item.ID).ConfigureAwait(false);
            StringBuilder sb = new StringBuilder();

            if (html == null)
            {
                sb.AppendLine("```yaml");
                sb.AppendLine("Item Description Not Available...");
                sb.AppendLine("```");
            }
            else
            {
                var itemMarkdown = itemFactory.CreateFromHtml(html, item.Name, item.LucyLink);
                sb.AppendLine(itemMarkdown);
            }

            return sb.ToString();
        }


    }
}
