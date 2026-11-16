using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using BidBot.Messaging;
using BidBot.Sheets;
using Discord;
using System;
using System.Linq;

namespace BidBot.Logic
{
    /// <summary>
    /// Once one of a winner's items has been won for Settings:PaymentReminderMinutes, mentions them in the
    /// award channel with every item of theirs whose Paid box (SheetSettings:PaidColumn) is still unchecked.
    /// Items an officer reassigned on the sheet (Winner cell no longer matches) are skipped.
    /// </summary>
    public class PaymentReminderWorker : BackgroundService
    {
        private static readonly TimeSpan CHECK_INTERVAL = TimeSpan.FromMinutes(1);

        private readonly ILogger<PaymentReminderWorker> logger;
        private readonly IServiceScopeFactory scopeFactory;
        private readonly BidbotDataStore dataStore;
        private readonly IConfiguration config;
        private readonly int reminderMinutes;

        public PaymentReminderWorker(ILogger<PaymentReminderWorker> logger,
            IServiceScopeFactory scopeFactory,
            BidbotDataStore dataStore,
            IConfiguration config)
        {
            this.logger = logger;
            this.scopeFactory = scopeFactory;
            this.dataStore = dataStore;
            this.config = config;
            this.reminderMinutes = int.TryParse(config["Settings:PaymentReminderMinutes"], out var minutes) ? minutes : 45;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (reminderMinutes <= 0)
            {
                logger.LogInformation("Payment reminders are turned off (Settings:PaymentReminderMinutes is 0).");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await checkPayments();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "PaymentReminderWorker encountered an error while checking payments.");
                }

                try
                {
                    await Task.Delay(CHECK_INTERVAL, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task checkPayments()
        {
            using var scope = scopeFactory.CreateScope();
            var sheetService = scope.ServiceProvider.GetRequiredService<GoogleSheetService>();

            if (!sheetService.HasPaidColumn || !sheetService.IsSheetCurrent)
                return;

            // The raid stays open until everything is paid, so only the active raid is checked.
            var raid = dataStore.ActiveRaid;
            if (raid == null)
                return;

            var wonItems = raid.Auctions.ToList()
                .Where(x => x.WinnerUserID.HasValue && x.RowNumber.HasValue && x.RowNumber > 1)
                .ToList();

            // A winner is reminded once one of their items passes the reminder time without being checked.
            var dueBefore = DateTime.UtcNow.AddMinutes(-reminderMinutes);
            var winnersToCheck = wonItems
                .Where(x => x.PaymentCheckedDT == null && x.FinishedDT.HasValue && x.FinishedDT <= dueBefore)
                .Select(x => (x.GuildID, x.WinnerUserID!.Value))
                .ToHashSet();
            if (winnersToCheck.Count == 0)
                return;

            // The reminder lists all of that winner's unpaid items, including ones not yet due.
            var toCheck = wonItems.Where(x => winnersToCheck.Contains((x.GuildID, x.WinnerUserID!.Value))).ToList();

            var status = await sheetService.GetPaymentStatus(toCheck.Select(x => x.RowNumber!.Value));
            if (!status.WasSuccessful)
            {
                // Try again next minute, the sheet may be briefly unavailable.
                logger.LogWarning("Unable to check payments on the sheet: {Message}", status.Message);
                return;
            }

            var unpaid = new List<Auction>();
            foreach (var auction in toCheck)
            {
                // Everything checked now is covered by this reminder, so none of these items trigger another one.
                // A later win by the same person starts a new reminder that lists whatever is still unpaid.
                auction.PaymentCheckedDT ??= DateTime.UtcNow;

                if (!status.Rows.TryGetValue(auction.RowNumber!.Value, out var row))
                    continue;

                if (row.IsPaid)
                    continue;

                if (!string.Equals(row.Winner, auction.Winner?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Skipping payment reminder for {Item} (row {Row}): the sheet's winner '{SheetWinner}' doesn't match '{Winner}'.",
                        auction.ItemName, auction.RowNumber, row.Winner, auction.Winner);
                    continue;
                }

                unpaid.Add(auction);
            }

            var task = dataStore.SaveChanges(); // Don't care about waiting.

            if (unpaid.Count == 0)
                return;

            var communication = scope.ServiceProvider.GetRequiredService<ICommunicationService>();
            var channelName = config["Settings:PaymentReminderChannelName"];
            if (string.IsNullOrWhiteSpace(channelName))
                channelName = config["Settings:AwardChannelName"];

            foreach (var winner in unpaid.GroupBy(x => (x.GuildID, x.WinnerUserID!.Value)))
            {
                var itemNames = string.Join(", ", winner.OrderBy(x => x.RowNumber).Select(x => x.ItemName));
                var mention = MentionUtils.MentionUser(winner.Key.Value);

                try
                {
                    await communication.SendMessageByChannelName(winner.Key.GuildID, channelName!, $"{mention} You still need to pay for {itemNames}, please pay promptly.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unable to send payment reminder for {Items}.", itemNames);
                }
            }
        }
    }
}
