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
            
            if (!shouldParseLogFile)
                return;

            if (string.IsNullOrWhiteSpace(logFileName))
            {
                logger.LogInformation("No Log Parse Configuration...");
                return;
            }

            long position = 0;
            using (var posFile = File.Open(logFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                position = posFile.Length;
            }



            while (!stoppingToken.IsCancellationRequested)
            {
                if (!string.IsNullOrWhiteSpace(logFileName))
                {
                    try
                    {
                        readLogFile(ref position);
                        await processQueue();

                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "EQLogWorker encountered an error while polling the log");
                    }
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

            var hadUpdates = false;
            while (logStore.Dequeue(out var result))
            {
                if (result.Contains("was given to", StringComparison.OrdinalIgnoreCase))
                {
                    hadUpdates = true;
                    if (tryParseGivenItem(result, out var itemName, out var playerName))
                    {
                        var currentRaid = store.ActiveRaid;
                        if (currentRaid == null || currentRaid.EndDT.HasValue)
                            continue;

                        currentRaid.LootedItems.Add((itemName, playerName));
                        try
                        {
                            var addResult = await sheetService.AddItemToSheet(itemName, playerName);
                            if(!addResult.WasSuccessful)
                            {
                                currentRaid.Errors.Add($"Error UpdatingSheet for item {itemName} : {addResult.Message} ");
                                logger.LogCritical(addResult.Message);
                            }
                        }
                        catch(Exception ex)
                        {
                            currentRaid.Errors.Add($"Error UpdatingSheet: {ex.Message} ");
                            logger.LogError(ex, "Error Updating Sheet.");
                        }
                    }
                    else
                    {
                        logger.LogWarning("Skipping malformed EQ loot line: {LogLine}", result);
                    }
                }
            }

            if (hadUpdates)
            {
                var task = store.SaveChanges(); // Don't care about waiting.
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
