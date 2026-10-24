using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using BidBot.Messaging;
using Discord;
using System.Security.Cryptography.X509Certificates;
using BidBot.DataAccess.Entities;

namespace BidBot.Logic
{
    public class BidTimerWorker : BackgroundService
    {
        private readonly ILogger<BidTimerWorker> logger;
        private readonly IServiceScopeFactory scopeFactory;
        private readonly AuctionStore store;

        public BidTimerWorker(ILogger<BidTimerWorker> logger,
            IServiceScopeFactory scopeFactory,
            AuctionStore store)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.scopeFactory = scopeFactory;
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var checkInterval = TimeSpan.FromSeconds(10);
            logger.LogInformation("BidTimerWorker starting, tick interval {Interval}", checkInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await OnTick(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "BidTimerWorker encountered an error while running tick");
                }

                try
                {
                    await Task.Delay(checkInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // shutdown requested
                    break;
                }
            }

            logger.LogInformation("BidTimerWorker stopping");
        }

        /// <summary>
        /// Called on each tick interval. Override or modify this to implement per-tick behavior.
        /// Default implementation inspects the BidMessageStore and logs existing bids.
        /// </summary>
        protected virtual async Task OnTick(CancellationToken ct)
        {
            var all = store.GetAll();
            if (all != null && all.Any())
            {
                using (var scope = scopeFactory.CreateScope())
                {
                    var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();
                    var messageFactory = scope.ServiceProvider.GetRequiredService<BidDiscordMessageFactory>();

                    var closedAuctions = all.Where(b => b.EndDT <= DateTime.UtcNow && b.CancelledDT == null).ToList();
                    foreach (var closedAuction in closedAuctions)
                    {
                        await bidFinished(closedAuction, scope);
                    }

                    var openAuctions = all.Where(b => b.EndDT > DateTime.UtcNow && b.CancelledDT == null).ToList();
                    foreach (var openAuction in openAuctions)
                    {
                        var messageMarkdown = messageFactory.CreateDiscordText(openAuction);
                        await communicationService.UpdateMessage(openAuction.GuildID, openAuction.ChannelID, openAuction.MessageID, messageMarkdown);
                    }
                }
            }
        }

        private async Task bidFinished(Auction auction, IServiceScope scope)
        {
            var messageFactory = scope.ServiceProvider.GetRequiredService<BidDiscordMessageFactory>();
            var communicationService = scope.ServiceProvider.GetRequiredService<ICommunicationService>();
            var bidService = scope.ServiceProvider.GetRequiredService<BidService>();

            await bidService.FinishAuction(auction);

            var messageMarkdown = messageFactory.CreateDiscordText(auction);
            await communicationService.UpdateMessageAndRemoveComponents(auction.GuildID, auction.ChannelID, auction.MessageID, messageMarkdown);
        }
    }
}
