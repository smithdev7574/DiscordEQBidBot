using BidBot.DataAccess;
using BidBot.Logic;
using BidBot.Messaging;
using BidBot.Sheets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace BidBot.EQLogs
{
    internal class EQLogWorker : BackgroundService
    {
        private readonly IServiceScopeFactory scopeFactory;
        private readonly ILogger<EQLogWorker> logger;
        private readonly EQLogStore logStore;
        private readonly BidbotDataStore store;
        private readonly string logFileName;
        private readonly bool shouldParseLogFile;
        private readonly int logParseInterval = 10;

        public EQLogWorker(IServiceScopeFactory scopeFactory, 
            IConfiguration config, 
            ILogger<EQLogWorker> logger,
            EQLogStore logStore,
            BidbotDataStore store)
        {
            if (bool.TryParse(config["EQLogParseSettings:ShouldParseLogFile"], out var shouldParseLogFile))
                this.shouldParseLogFile = shouldParseLogFile;

            this.scopeFactory = scopeFactory;
            this.logger = logger;
            this.logStore = logStore;
            this.store = store;
            if (shouldParseLogFile)
            {
                logFileName = config["EQLogParseSettings:EQLogFile"] ?? throw new ArgumentException("Missing App Settings EQLogParseSettings:EQLogFile");
                if (int.TryParse(config["EQLogParseSettings:LogParseInterval"], out var logParseInterval))
                    this.logParseInterval = logParseInterval;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var checkInterval = TimeSpan.FromSeconds(logParseInterval);

            // Even without a log file, lines from !bb manuallog and /simulateloot still need processing.
            var readFile = shouldParseLogFile && !string.IsNullOrWhiteSpace(logFileName);
            if (!readFile)
                logger.LogInformation("EQ log file parsing is off, only manual and simulated loot will be processed.");

            long position = 0;
            if (readFile)
            {
                using var posFile = File.Open(logFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                position = posFile.Length;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (readFile)
                        readLogFile(ref position);
                    await processQueue();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "EQLogWorker encountered an error while polling the log");
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
        }

        private async Task processQueue()
        {
            var scope = scopeFactory.CreateScope();
            var sheetService = scope.ServiceProvider.GetRequiredService<GoogleSheetService>();

            // Collect everything looted since the last pass, then write it to the sheet in one go.
            // One read and one write per pass keeps big loot drops under Google's per-minute limits.
            var loot = new List<(string ItemName, string PlayerName)>();
            var hadUpdates = false;
            while (logStore.Dequeue(out var result))
            {
                if (!result.Contains("was given to", StringComparison.OrdinalIgnoreCase))
                    continue;

                hadUpdates = true;
                if (tryParseGivenItem(result, out var itemName, out var playerName))
                    loot.Add((itemName, playerName));
                else
                    logger.LogWarning("Skipping malformed EQ loot line: {LogLine}", result);
            }

            var currentRaid = store.ActiveRaid;
            if (loot.Count > 0 && currentRaid != null && !currentRaid.EndDT.HasValue)
            {
                currentRaid.LootedItems.AddRange(loot);
                try
                {
                    var addResult = await sheetService.AddItemsToSheet(loot);
                    if (!addResult.WasSuccessful)
                    {
                        var names = string.Join(", ", loot.Select(x => x.ItemName));
                        currentRaid.Errors.Add($"Error UpdatingSheet for {names} : {addResult.Message} ");
                        logger.LogCritical("Couldn't add {Items} to the sheet: {Message}", names, addResult.Message);
                    }
                    else
                    {
                        for (int i = 0; i < loot.Count; i++)
                            await addToLootQueue(scope, currentRaid.GuildID, loot[i].ItemName, addResult.Rows[i]);
                    }
                }
                catch (Exception ex)
                {
                    currentRaid.Errors.Add($"Error UpdatingSheet: {ex.Message} ");
                    logger.LogError(ex, "Error Updating Sheet.");
                }
            }

            if (hadUpdates)
            {
                var task = store.SaveChanges(); // Don't care about waiting.
            }
        }

        private async Task addToLootQueue(IServiceScope scope, ulong guildID, string itemName, int rowNumber)
        {
            // A loot queue problem must never stop loot from being logged to the sheet.
            try
            {
                var lootQueue = scope.ServiceProvider.GetRequiredService<LootQueueService>();
                await lootQueue.AddLoot(guildID, itemName, rowNumber);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding {Item} (row {Row}) to the loot queue.", itemName, rowNumber);
            }
        }

        private void readLogFile(ref long position)
        {
            using var file = File.Open(logFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var fileLength = file.Length;
            if (position > fileLength)
            {
                logger.LogWarning("EQ log {LogFile} shrank from read position {Position} to {Length} bytes. Reading from the beginning.",
                    logFileName, position, fileLength);
                position = 0;
            }

            if (position == fileLength)
                return;

            file.Position = position;
            using var line = new MemoryStream();

            // Commit the position only after a line ending. A later poll can then
            // reread an unfinished line, including any partial UTF-8 character.
            int nextByte;
            while ((nextByte = file.ReadByte()) != -1)
            {
                if (nextByte != '\r' && nextByte != '\n')
                {
                    line.WriteByte((byte)nextByte);
                    continue;
                }

                if (line.Length > 0)
                {
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                    logStore.Enqueue(position == 0 ? text.TrimStart('\uFEFF') : text);
                    line.SetLength(0);
                }

                position = file.Position;
            }
        }

        private bool tryParseGivenItem(string logLine, out string itemName, out string playerName)
        {
            itemName = null;
            playerName = null;

            const string marker = " was given to ";

            int markerIndex = logLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex == -1)
                return false;

            int tsEnd = logLine.IndexOf("] ");
            int itemStart = tsEnd + 2;
            if (tsEnd == -1 || markerIndex <= itemStart)
                return false;

            string rawItem = logLine.Substring(itemStart, markerIndex - itemStart).Trim();
            if (rawItem.Length == 0)
                return false;

            // Strip EQ grammatical prefixes ONLY when appropriate
            if (rawItem.StartsWith("A ", StringComparison.OrdinalIgnoreCase))
            {
                // If next character is uppercase, it's a real item name
                if (rawItem.Length > 2 && char.IsUpper(rawItem[2]))
                    rawItem = rawItem.Substring(2);
            }
            else if (rawItem.StartsWith("An ", StringComparison.OrdinalIgnoreCase))
            {
                if (rawItem.Length > 3 && char.IsUpper(rawItem[3]))
                    rawItem = rawItem.Substring(3);
            }
            else if (rawItem.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            {
                if (rawItem.Length > 4 && char.IsUpper(rawItem[4]))
                    rawItem = rawItem.Substring(4);
            }

            int playerStart = markerIndex + marker.Length;
            int periodIndex = logLine.LastIndexOf('.');
            if (periodIndex <= playerStart)
                return false;

            string player = logLine.Substring(playerStart, periodIndex - playerStart).Trim();
            if (player.Length == 0)
                return false;

            itemName = rawItem;
            playerName = player;
            return true;
        }

    }
}
