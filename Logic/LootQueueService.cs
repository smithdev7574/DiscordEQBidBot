using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using BidBot.Messaging;
using BidBot.Sheets;
using Discord;
using Discord.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace BidBot.Logic
{
    /// <summary>
    /// Keeps the raid's looted items in one Loot Queue post and starts their auctions one at a time.
    ///
    /// - The EQ log adds items as they're looted. Nothing is auctioned until an officer presses Start Auctions.
    /// - While auto-start is on, the next waiting item starts every /setspacing seconds, skipping
    ///   items whose name already has an auction running until that auction closes.
    /// - Pause Active Auctions stops every running timer (and auto-start). Resume Active Auctions restarts them.
    /// - A cancelled auction goes back in line and its auction-started box on the sheet is unchecked.
    /// </summary>
    public class LootQueueService : BackgroundService
    {
        /// <summary>Discord allows 2000 characters per message, this leaves room for the continuation notes.</summary>
        private const int MAX_POST_LENGTH = 1900;
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MinimumResumeTime = TimeSpan.FromSeconds(30);

        private readonly IServiceScopeFactory scopeFactory;
        private readonly BidbotDataStore dataStore;
        private readonly AuctionStore auctionStore;
        private readonly ItemListStore itemListStore;
        private readonly Lazy<GoogleSheetService> sheetService;
        private readonly ILogger<LootQueueService> logger;

        private readonly string? channelName;
        private readonly AuctionTimingService timing;

        // The queue itself is only read or changed while holding gate. Discord calls happen outside it.
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim postGate = new SemaphoreSlim(1, 1);
        private LootQueue? queue;
        private volatile bool isDirty;
        private DateTime nextRefreshAttempt = DateTime.MinValue;
        private readonly List<int> rowsToUncheckAtStartup = new List<int>();

        public LootQueueService(IServiceScopeFactory scopeFactory,
            BidbotDataStore dataStore,
            AuctionStore auctionStore,
            ItemListStore itemListStore,
            AuctionTimingService timing,
            IConfiguration config,
            ILogger<LootQueueService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.dataStore = dataStore;
            this.auctionStore = auctionStore;
            this.itemListStore = itemListStore;
            this.timing = timing;
            this.logger = logger;

            // Resolved on first use, so a sheet setup problem can't stop the bot from starting.
            sheetService = new Lazy<GoogleSheetService>(() =>
            {
                using var scope = scopeFactory.CreateScope();
                return scope.ServiceProvider.GetRequiredService<GoogleSheetService>();
            });

            var queueChannel = config["Settings:LootQueueChannelName"];
            channelName = string.IsNullOrWhiteSpace(queueChannel) ? config["Settings:BidChannelName"] : queueChannel;

            restore();
        }

        /// <summary>Read every time, so /setspacing takes effect on the next tick.</summary>
        public int SpacingSeconds => timing.GetSpacingSeconds();

        /// <summary>Redraws the post on the next tick, for changes outside the queue like /setduration and /setspacing.</summary>
        public void MarkDirty() => isDirty = true;

        /// <summary>
        /// Auctions live in memory, so after a restart anything that was running goes back in line.
        /// Auto-start is turned off so nothing starts until an officer is ready.
        /// </summary>
        private void restore()
        {
            queue = dataStore.LootQueue?.Clone();
            if (queue == null)
                return;

            foreach (var entry in queue.Entries.Where(x => x.Status is LootQueueStatus.Starting or LootQueueStatus.AwaitingPick or LootQueueStatus.Active))
            {
                if (entry.Status == LootQueueStatus.Active)
                    rowsToUncheckAtStartup.Add(entry.RowNumber);

                entry.Status = LootQueueStatus.Waiting;
                entry.AuctionID = null;
                entry.Note = "the bot restarted, back in line";
            }

            queue.IsRunning = false;
            queue.IsPaused = false;
            isDirty = true;
            logger.LogInformation("Restored the loot queue with {Count} item(s).", queue.Entries.Count);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            foreach (var row in rowsToUncheckAtStartup)
                await uncheckRow(row);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await startNextIfDue();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Loot queue failed to start the next auction.");
                }

                try
                {
                    await RefreshPost();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Loot queue failed to update its post.");
                }

                try
                {
                    await Task.Delay(TickInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        #region Loot and auction events

        /// <summary>
        /// Adds an item the EQ log just saw handed out. Never starts anything on its own.
        /// </summary>
        public async Task AddLoot(ulong guildID, string itemName, int rowNumber)
        {
            var matches = itemListStore.Get()?.FindByName(itemName).ToList() ?? new List<Items.ItemEntry>();
            var entry = new LootQueueEntry()
            {
                ItemName = matches.Count == 1 ? matches[0].Name : itemName.Trim(),
                LucyLink = matches.Count == 1 ? matches[0].LucyLink : null,
                RowNumber = rowNumber,
            };

            if (matches.Count == 0)
            {
                entry.Status = LootQueueStatus.NeedsOfficer;
                entry.Note = $"not in the item list, start it with the auction command and row {rowNumber}";
            }

            await gate.WaitAsync();
            try
            {
                queue ??= new LootQueue() { GuildID = guildID };

                // A manual log import can see the same loot line again.
                if (queue.Entries.Any(x => x.RowNumber == rowNumber))
                    return;

                queue.Entries.Add(entry);
                changed();
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Called whenever any auction starts (queue, command or item picker), whether or not auto-start
        /// is on. Links it to its queued item by row number. A hand-started auction for a row the queue
        /// doesn't have yet (for example loot from before the bot started) is added, so the post shows it.
        /// </summary>
        public async Task OnAuctionStarted(Auction auction)
        {
            if (auction.RowNumber is not int rowNumber)
                return;

            await gate.WaitAsync();
            try
            {
                // After /endraid there's no queue, and an auction started then shouldn't open a new post.
                if (queue == null && !hasActiveRaid())
                    return;
                queue ??= new LootQueue() { GuildID = auction.GuildID };

                var entry = queue.Entries.FirstOrDefault(x => x.RowNumber == rowNumber);
                if (entry == null)
                {
                    entry = new LootQueueEntry() { RowNumber = rowNumber };
                    queue.Entries.Add(entry);
                }

                // A second auction for a row that's already running leaves the first one tracked.
                if (entry.Status != LootQueueStatus.Active)
                {
                    entry.Status = LootQueueStatus.Active;
                    entry.AuctionID = auction.AuctionID;
                    entry.ItemName = auction.ItemName;
                    entry.LucyLink = auction.Item.LucyLink;
                    entry.Note = null;
                }

                // Hand-started auctions count toward the spacing too.
                queue.LastStartDT = DateTime.UtcNow;
                changed();
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Called when an item picker is posted because a name matches several items (by the queue or by
        /// an auction command). Shows the queued item as waiting for a pick so auto-start doesn't post a
        /// second picker.
        /// </summary>
        public async Task OnPickerPosted(int rowNumber)
        {
            await gate.WaitAsync();
            try
            {
                var entry = queue?.Entries.FirstOrDefault(x => x.RowNumber == rowNumber && x.Status != LootQueueStatus.Active);
                if (queue == null || entry == null)
                    return;

                entry.Status = LootQueueStatus.AwaitingPick;
                entry.Note = "matches several items, an officer needs to pick the right one from the list";
                queue.LastStartDT = DateTime.UtcNow;
                changed();
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task OnAuctionFinished(Auction auction)
        {
            await gate.WaitAsync();
            try
            {
                var entry = queue?.Entries.FirstOrDefault(x => x.AuctionID == auction.AuctionID);
                if (queue == null || entry == null)
                    return;

                queue.Entries.Remove(entry);
                queue.FinishedCount++;
                changed();
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Puts the item back in line and unchecks its auction-started box on the sheet.
        /// </summary>
        public async Task OnAuctionCancelled(Auction auction)
        {
            await gate.WaitAsync();
            try
            {
                var entry = queue?.Entries.FirstOrDefault(x => x.AuctionID == auction.AuctionID);
                if (entry != null)
                {
                    entry.Status = LootQueueStatus.Waiting;
                    entry.AuctionID = null;
                    entry.Note = "auction was cancelled, back in line";
                    changed();
                }
                else if (auction.RowNumber.HasValue && (queue != null || hasActiveRaid()))
                {
                    // A hand-started auction that wasn't in the queue (for example loot from before the bot started).
                    // After /endraid there's no queue, and a cancel then shouldn't open a new post.
                    queue ??= new LootQueue() { GuildID = auction.GuildID };
                    if (!queue.Entries.Any(x => x.RowNumber == auction.RowNumber))
                    {
                        queue.Entries.Add(new LootQueueEntry()
                        {
                            ItemName = auction.ItemName,
                            LucyLink = auction.Item.LucyLink,
                            RowNumber = auction.RowNumber.Value,
                            Note = "auction was cancelled, back in line"
                        });
                        changed();
                    }
                }
            }
            finally
            {
                gate.Release();
            }

            if (auction.RowNumber.HasValue)
                await uncheckRow(auction.RowNumber.Value);
        }

        private async Task uncheckRow(int rowNumber)
        {
            try
            {
                var result = await sheetService.Value.SetAuctionStarted(rowNumber, false);
                if (!result.WasSuccessful)
                    logger.LogWarning("Couldn't uncheck row {Row} on the sheet: {Message}", rowNumber, result.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't uncheck row {Row} on the sheet.", rowNumber);
            }
        }

        #endregion

        #region Buttons

        public async Task<string> StartAuctions(ulong guildID, ulong userID)
        {
            await gate.WaitAsync();
            try
            {
                queue ??= new LootQueue() { GuildID = guildID };
                if (queue.IsRunning)
                    return "Auctions are already starting automatically.";

                queue.IsRunning = true;
                queue.StartedByUserID = userID;
                changed();

                if (queue.IsPaused)
                    return "Auto-start is on, but active auctions are paused. Press Resume Active to continue.";

                return $"Starting auctions, one every {SpacingSeconds} seconds.";
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<string> StopAuctions()
        {
            await gate.WaitAsync();
            try
            {
                if (queue == null || !queue.IsRunning)
                    return "Auctions aren't starting automatically.";

                queue.IsRunning = false;
                changed();
                return "Stopped starting new auctions. Auctions already running keep going.";
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<string> PauseActive(ulong guildID)
        {
            await gate.WaitAsync();
            try
            {
                queue ??= new LootQueue() { GuildID = guildID };
                queue.IsPaused = true;
                changed();
            }
            finally
            {
                gate.Release();
            }

            var paused = new List<Auction>();
            foreach (var auction in auctionStore.GetAll().Where(x => x.GuildID == guildID))
            {
                if (await auction.Pause())
                    paused.Add(auction);
            }

            await updateAuctionMessages(paused);
            return $"Paused {paused.Count} active auction{(paused.Count == 1 ? "" : "s")}. No new auctions start until you press Resume Active.";
        }

        public async Task<string> ResumeActive(ulong guildID)
        {
            await gate.WaitAsync();
            try
            {
                if (queue != null)
                {
                    queue.IsPaused = false;
                    // Give people a moment before the next new auction joins the resumed ones.
                    queue.LastStartDT = DateTime.UtcNow;
                    changed();
                }
            }
            finally
            {
                gate.Release();
            }

            var resumed = new List<Auction>();
            foreach (var auction in auctionStore.GetAll().Where(x => x.GuildID == guildID))
            {
                if (await auction.Resume(MinimumResumeTime))
                    resumed.Add(auction);
            }

            await updateAuctionMessages(resumed);
            return $"Resumed {resumed.Count} auction{(resumed.Count == 1 ? "" : "s")}.";
        }

        /// <summary>
        /// Takes an item out of the queue and clears its row on the sheet, for loot that shouldn't be
        /// auctioned. Only rows in the queue can be removed, so a finished auction's row is never cleared.
        /// </summary>
        public async Task<(bool WasSuccessful, string Message)> RemoveRow(int rowNumber)
        {
            string itemName;
            ulong guildID;
            await gate.WaitAsync();
            try
            {
                var entry = queue?.Entries.FirstOrDefault(x => x.RowNumber == rowNumber);
                if (queue == null || entry == null)
                    return (false, $"Row {rowNumber} isn't in the loot queue, nothing was removed.");

                if (entry.Status is LootQueueStatus.Active or LootQueueStatus.Starting)
                    return (false, $"{entry.ItemName} (row {rowNumber}) is being auctioned. Cancel the auction first, then remove it.");

                queue.Entries.Remove(entry);
                changed();
                itemName = entry.ItemName;
                guildID = queue.GuildID;
            }
            finally
            {
                gate.Release();
            }

            // An item picker still posted for this row would start an auction for the old item on a
            // row the next loot may reuse, so take it down before clearing the row.
            await removePickers(guildID, rowNumber);

            try
            {
                var result = await sheetService.Value.ClearItemRow(rowNumber);
                if (!result.WasSuccessful)
                    return (false, $"Removed {itemName} (row {rowNumber}) from the loot queue, but couldn't clear it on the sheet: {result.Message}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't clear row {Row} on the sheet.", rowNumber);
                return (false, $"Removed {itemName} (row {rowNumber}) from the loot queue, but couldn't clear it on the sheet: {ex.Message}");
            }

            return (true, $"Removed {itemName} (row {rowNumber}) from the loot queue and cleared it on the sheet.");
        }

        private async Task removePickers(ulong guildID, int? rowNumber)
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<MultiItemAuctionStore>();
            var pickers = rowNumber.HasValue ? store.RemoveByRow(guildID, rowNumber.Value) : store.RemoveAll(guildID);
            if (pickers.Count == 0)
                return;

            var communication = scope.ServiceProvider.GetRequiredService<ICommunicationService>();
            foreach (var picker in pickers)
            {
                if (picker.ChannelID is not ulong channelID)
                    continue;

                if (picker.MessageID is ulong headerID)
                    await tryDelete(communication, guildID, channelID, headerID);
                foreach (var item in picker.Items)
                {
                    if (item.MessageID is ulong itemMessageID)
                        await tryDelete(communication, guildID, channelID, itemMessageID);
                }
            }
        }

        /// <summary>
        /// Called by /endraid. Deletes item pickers still waiting (so nobody starts an auction after the
        /// raid), resumes anything paused so it can finish, removes the buttons and clears the queue.
        /// Auctions already running keep going and finish normally.
        /// </summary>
        public async Task EndRaid(ulong guildID)
        {
            try
            {
                await removePickers(guildID, rowNumber: null);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't remove item pickers at the end of the raid.");
            }

            LootQueue? final;
            await gate.WaitAsync();
            try
            {
                final = queue;
                queue = null;
                dataStore.LootQueue = null;
                isDirty = false;
            }
            finally
            {
                gate.Release();
            }

            if (final == null)
                return;

            _ = saveAsync();

            var resumed = new List<Auction>();
            foreach (var auction in auctionStore.GetAll().Where(x => x.GuildID == final.GuildID))
            {
                if (await auction.Resume(MinimumResumeTime))
                    resumed.Add(auction);
            }
            await updateAuctionMessages(resumed);

            if (final.ChannelID == null || final.MessageIDs.Count == 0)
                return;

            await postGate.WaitAsync();
            try
            {
                final.IsRunning = false;
                final.IsPaused = false;
                var chunks = render(final, raidEnded: true);
                using var scope = scopeFactory.CreateScope();
                var communication = scope.ServiceProvider.GetRequiredService<ICommunicationService>();

                for (int i = 0; i < final.MessageIDs.Count; i++)
                {
                    try
                    {
                        var text = i < chunks.Count ? chunks[i] : "_Loot Queue, continued: nothing more._";
                        if (i == 0)
                            await communication.UpdateMessageAndRemoveComponents(final.GuildID, final.ChannelID.Value, final.MessageIDs[i], text);
                        else
                            await communication.UpdateMessage(final.GuildID, final.ChannelID.Value, final.MessageIDs[i], text);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Couldn't close out the loot queue post.");
                    }
                }

                // The list grew since the last refresh, so the final version needs more messages than exist.
                foreach (var extra in chunks.Skip(final.MessageIDs.Count))
                {
                    try
                    {
                        await communication.SendMessageByChannelName(final.GuildID, channelName!, extra, flags: MessageFlags.SuppressEmbeds);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Couldn't post the rest of the final loot queue.");
                    }
                }
            }
            finally
            {
                postGate.Release();
            }
        }

        #endregion

        #region Auto-start

        private async Task startNextIfDue()
        {
            Guid entryID;
            string itemName;
            int rowNumber;
            ulong guildID;
            ulong userID;

            await gate.WaitAsync();
            try
            {
                if (queue == null || !queue.IsRunning || queue.IsPaused)
                    return;

                if (queue.LastStartDT.HasValue && DateTime.UtcNow - queue.LastStartDT.Value < TimeSpan.FromSeconds(SpacingSeconds))
                    return;

                // Only one auction per item name at a time. Similar items wait until the previous one closes.
                var busyNames = queue.Entries
                    .Where(x => x.Status is LootQueueStatus.Starting or LootQueueStatus.AwaitingPick or LootQueueStatus.Active)
                    .Select(x => x.ItemName)
                    .Concat(auctionStore.GetAll().Where(x => !x.IsClosed && x.CancelledDT == null).Select(x => x.ItemName))
                    .ToList();

                var next = queue.Entries
                    .Where(x => x.Status == LootQueueStatus.Waiting)
                    .OrderBy(x => x.RowNumber)
                    .FirstOrDefault(x => !busyNames.Any(name => isSameItem(name, x.ItemName)));

                if (next == null)
                    return;

                next.Status = LootQueueStatus.Starting;
                next.Note = null;
                queue.LastStartDT = DateTime.UtcNow;
                changed();

                entryID = next.ID;
                itemName = next.ItemName;
                rowNumber = next.RowNumber;
                guildID = queue.GuildID;
                userID = queue.StartedByUserID;
            }
            finally
            {
                gate.Release();
            }

            // Outside the lock: starting the auction calls back into OnAuctionStarted.
            (bool WasSuccess, string Message) result = (false, string.Empty);
            Exception? error = null;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var bidService = scope.ServiceProvider.GetRequiredService<BidService>();
                result = await bidService.StartBidByName(guildID, userID, itemName, timing.GetDefaultDuration(), rowNumber);
            }
            catch (Exception ex)
            {
                error = ex;
                logger.LogError(ex, "Loot queue couldn't start the auction for {Item} (row {Row}).", itemName, rowNumber);
            }

            await gate.WaitAsync();
            try
            {
                var entry = queue?.Entries.FirstOrDefault(x => x.ID == entryID);
                if (entry == null || entry.Status != LootQueueStatus.Starting)
                    return; // OnAuctionStarted already linked it (or it was removed).

                if (error != null)
                {
                    entry.Status = LootQueueStatus.Waiting;
                    entry.Note = "couldn't start, will try again";
                }
                else if (result.WasSuccess)
                {
                    // StartBidByName posted an item picker because the name matches several items.
                    entry.Status = LootQueueStatus.AwaitingPick;
                    entry.Note = "matches several items, an officer needs to pick the right one from the list";
                }
                else
                {
                    entry.Status = LootQueueStatus.NeedsOfficer;
                    entry.Note = result.Message;
                }
                changed();
            }
            finally
            {
                gate.Release();
            }
        }

        private bool hasActiveRaid() => dataStore.ActiveRaid != null;

        private static bool isSameItem(string a, string b)
        {
            return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Post

        /// <summary>
        /// Brings the Loot Queue post up to date when something changed. Edits the existing messages,
        /// posts overflow messages when the list outgrows one message, and deletes extras when it shrinks.
        /// Reposts everything if the main message was deleted.
        /// </summary>
        public async Task RefreshPost()
        {
            if (!isDirty || DateTime.UtcNow < nextRefreshAttempt)
                return;

            await postGate.WaitAsync();
            try
            {
                LootQueue? snapshot;
                await gate.WaitAsync();
                try
                {
                    isDirty = false;
                    snapshot = queue?.Clone();
                }
                finally
                {
                    gate.Release();
                }

                if (snapshot == null)
                    return;

                try
                {
                    var (channelID, messageIDs) = await syncPost(snapshot);

                    await gate.WaitAsync();
                    try
                    {
                        if (queue != null)
                        {
                            queue.ChannelID = channelID;
                            queue.MessageIDs = messageIDs;
                            persist();
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (Exception ex)
                {
                    isDirty = true;
                    nextRefreshAttempt = DateTime.UtcNow.Add(RefreshRetryDelay);
                    logger.LogWarning(ex, "Couldn't update the loot queue post, trying again in {Seconds} seconds.", RefreshRetryDelay.TotalSeconds);
                }
            }
            finally
            {
                postGate.Release();
            }
        }

        private async Task<(ulong ChannelID, List<ulong> MessageIDs)> syncPost(LootQueue snapshot)
        {
            using var scope = scopeFactory.CreateScope();
            var communication = scope.ServiceProvider.GetRequiredService<ICommunicationService>();

            var chunks = render(snapshot, raidEnded: false);
            var buttons = buildButtons(snapshot);
            var ids = snapshot.MessageIDs.ToList();

            if (snapshot.ChannelID.HasValue && ids.Count > 0)
            {
                var channelID = snapshot.ChannelID.Value;
                try
                {
                    await communication.UpdateMessageAndComponents(snapshot.GuildID, channelID, ids[0], chunks[0], buttons);
                }
                catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound)
                {
                    logger.LogInformation("The loot queue post was deleted, posting it again.");
                    return await repost(communication, snapshot, chunks, buttons);
                }

                var newIds = new List<ulong> { ids[0] };
                for (int i = 1; i < chunks.Count; i++)
                {
                    if (i < ids.Count)
                    {
                        try
                        {
                            await communication.UpdateMessage(snapshot.GuildID, channelID, ids[i], chunks[i]);
                            newIds.Add(ids[i]);
                            continue;
                        }
                        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound)
                        {
                            // Someone deleted an overflow post, post a new one below.
                        }
                    }

                    var message = await communication.SendMessageByChannelName(snapshot.GuildID, channelName!, chunks[i], flags: MessageFlags.SuppressEmbeds);
                    newIds.Add(message.Id);
                }

                // The list got shorter, remove the overflow posts it no longer needs.
                foreach (var extra in ids.Skip(chunks.Count))
                    await tryDelete(communication, snapshot.GuildID, channelID, extra);

                return (channelID, newIds);
            }

            return await repost(communication, snapshot, chunks, buttons);
        }

        private async Task<(ulong ChannelID, List<ulong> MessageIDs)> repost(ICommunicationService communication, LootQueue snapshot, List<string> chunks, MessageComponent buttons)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                throw new InvalidOperationException("Neither Settings:LootQueueChannelName nor Settings:BidChannelName is configured.");

            if (snapshot.ChannelID.HasValue)
            {
                foreach (var id in snapshot.MessageIDs)
                    await tryDelete(communication, snapshot.GuildID, snapshot.ChannelID.Value, id);
            }

            var main = await communication.SendMessageByChannelName(snapshot.GuildID, channelName, chunks[0], buttons, flags: MessageFlags.SuppressEmbeds);
            var ids = new List<ulong> { main.Id };
            foreach (var chunk in chunks.Skip(1))
            {
                var message = await communication.SendMessageByChannelName(snapshot.GuildID, channelName, chunk, flags: MessageFlags.SuppressEmbeds);
                ids.Add(message.Id);
            }

            return (main.Channel.Id, ids);
        }

        private async Task tryDelete(ICommunicationService communication, ulong guildID, ulong channelID, ulong messageID)
        {
            try
            {
                await communication.DeleteMessage(guildID, channelID, messageID);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Couldn't delete old loot queue message {MessageID}.", messageID);
            }
        }

        private static MessageComponent buildButtons(LootQueue snapshot)
        {
            return new ComponentBuilder()
                .WithButton("Start Auctions", $"{ButtonHandler.QUEUE_START}:queue", ButtonStyle.Success, disabled: snapshot.IsRunning)
                .WithButton("Stop Auctions", $"{ButtonHandler.QUEUE_STOP}:queue", ButtonStyle.Danger, disabled: !snapshot.IsRunning)
                .WithButton("Pause Active", $"{ButtonHandler.QUEUE_PAUSE}:queue", ButtonStyle.Primary, disabled: snapshot.IsPaused)
                .WithButton("Resume Active", $"{ButtonHandler.QUEUE_RESUME}:queue", ButtonStyle.Primary, disabled: !snapshot.IsPaused)
                .WithButton("Remove Item", $"{ButtonHandler.QUEUE_REMOVE}:queue", ButtonStyle.Secondary)
                .Build();
        }

        /// <summary>
        /// Builds the post text, split into messages that each fit Discord's length limit.
        /// </summary>
        private List<string> render(LootQueue snapshot, bool raidEnded)
        {
            var lines = new List<string>();
            lines.Add(raidEnded ? "## 📦 Loot Queue (raid ended)" : "## 📦 Loot Queue");

            if (!raidEnded)
            {
                lines.Add(snapshot.IsRunning
                    ? $"**Auto-start:** ▶️ On, a new auction every {SpacingSeconds} seconds"
                    : "**Auto-start:** ⏹️ Off");
                var minutes = timing.GetDefaultDuration();
                lines.Add($"**Auction length:** {minutes} minute{(minutes == 1 ? "" : "s")}");
                if (snapshot.IsPaused)
                    lines.Add("**Active auctions:** ⏸️ Paused");
            }

            var active = snapshot.Entries
                .Where(x => x.Status is LootQueueStatus.Starting or LootQueueStatus.AwaitingPick or LootQueueStatus.Active)
                .OrderBy(x => x.RowNumber)
                .ToList();
            var waiting = snapshot.Entries.Where(x => x.Status == LootQueueStatus.Waiting).OrderBy(x => x.RowNumber).ToList();
            var needsOfficer = snapshot.Entries.Where(x => x.Status == LootQueueStatus.NeedsOfficer).OrderBy(x => x.RowNumber).ToList();

            if (active.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add($"### 🔨 Bidding now ({active.Count})");
                foreach (var entry in active)
                {
                    var state = entry.Status switch
                    {
                        LootQueueStatus.Starting => "starting...",
                        LootQueueStatus.AwaitingPick => entry.Note,
                        _ => snapshot.IsPaused ? "paused" : null
                    };
                    lines.Add(formatEntry(entry, state));
                }
            }

            lines.Add(string.Empty);
            lines.Add($"### ⏳ Waiting ({waiting.Count})");
            if (waiting.Count == 0)
                lines.Add(raidEnded ? "_Nothing was left waiting._" : "_Nothing waiting. New loot shows up here._");

            var busyNames = active.Select(x => x.ItemName).ToList();
            foreach (var entry in waiting)
            {
                var note = entry.Note;
                if (!raidEnded && busyNames.Any(name => isSameItem(name, entry.ItemName)))
                    note = "waits for the current one to close";
                lines.Add(formatEntry(entry, note));
            }

            if (needsOfficer.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add($"### ⚠️ Needs an officer ({needsOfficer.Count})");
                foreach (var entry in needsOfficer)
                    lines.Add(formatEntry(entry, entry.Note));
            }

            if (snapshot.FinishedCount > 0)
            {
                lines.Add(string.Empty);
                lines.Add($"✅ Finished: {snapshot.FinishedCount}");
            }

            return chunk(lines);
        }

        private static string formatEntry(LootQueueEntry entry, string? note)
        {
            // Angle brackets stop Discord from adding a preview for every link.
            var name = string.IsNullOrWhiteSpace(entry.LucyLink) ? entry.ItemName : $"[{entry.ItemName}](<{entry.LucyLink}>)";
            var line = $"- {name} · row {entry.RowNumber}";
            if (!string.IsNullOrWhiteSpace(note))
                line += $" · _{note}_";
            return line;
        }

        private static List<string> chunk(List<string> lines)
        {
            const string continuedBelow = "_Continued below..._";
            const string continuedHeader = "_Loot Queue, continued_";

            // "\n" rather than AppendLine, so every line break is exactly one character on every OS.
            // Room is always kept for the "continued below" footer, so no message goes over MAX_POST_LENGTH.
            var limit = MAX_POST_LENGTH - continuedBelow.Length - 1;
            var chunks = new List<string>();
            var current = new StringBuilder();
            foreach (var rawLine in lines)
            {
                var line = rawLine.Length > limit - continuedHeader.Length - 1 ? rawLine.Substring(0, limit - continuedHeader.Length - 1) : rawLine;
                if (current.Length + line.Length + 1 > limit)
                {
                    current.Append(continuedBelow).Append('\n');
                    chunks.Add(current.ToString());
                    current.Clear();
                    current.Append(continuedHeader).Append('\n');
                }
                current.Append(line).Append('\n');
            }

            chunks.Add(current.ToString());
            return chunks;
        }

        #endregion

        private async Task updateAuctionMessages(List<Auction> auctions)
        {
            if (auctions.Count == 0)
                return;

            using var scope = scopeFactory.CreateScope();
            var communication = scope.ServiceProvider.GetRequiredService<ICommunicationService>();
            var messageFactory = scope.ServiceProvider.GetRequiredService<BidDiscordMessageFactory>();
            foreach (var auction in auctions)
            {
                try
                {
                    await communication.UpdateMessage(auction.GuildID, auction.ChannelID, auction.MessageID, messageFactory.CreateDiscordText(auction));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Couldn't update the message for {Item}.", auction.ItemName);
                }
            }
        }

        /// <summary>Call while holding gate after changing the queue.</summary>
        private void changed()
        {
            isDirty = true;
            persist();
        }

        /// <summary>Call while holding gate. Saves a copy, so saving never reads a list that's being changed.</summary>
        private void persist()
        {
            dataStore.LootQueue = queue?.Clone();
            _ = saveAsync();
        }

        private async Task saveAsync()
        {
            try
            {
                await dataStore.SaveChanges();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to save the loot queue.");
            }
        }
    }
}
