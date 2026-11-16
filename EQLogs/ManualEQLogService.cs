using BidBot.DataAccess;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace BidBot.EQLogs
{
    public class ManualEQLogService
    {
        public const string SIMULATED_LOOTER = "Simulated";
        public const string MANUAL_LOOTER = "Manual";

        private readonly EQLogStore logStore;
        private readonly BidbotDataStore dataStore;
        private readonly ItemListStore itemListStore;

        public ManualEQLogService(EQLogStore logStore, BidbotDataStore dataStore, ItemListStore itemListStore)
        {
            this.logStore = logStore;
            this.dataStore = dataStore;
            this.itemListStore = itemListStore;
        }

        public void AddManualLogs(string manualLogs)
        {
            var lines = manualLogs.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                logStore.Enqueue(line);
            }
        }

        public void AddManualLogsFromFile(string url)
        {
            var client = new WebClient();
            var buffer = client.DownloadData(url);
            var download = Encoding.UTF8.GetString(buffer);
            AddManualLogs(download);
        }

        /// <summary>
        /// Writes one "was given to" EQ log line for an item typed in by an officer, so it goes through
        /// exactly the same path as real loot: sheet row, raid loot list and Loot Queue. Going through the
        /// log queue (rather than writing the sheet directly) keeps sheet row assignment in one place.
        /// </summary>
        public (bool WasSuccessful, string Message) AddManualItem(string itemName, string? holder)
        {
            itemName = itemName?.Trim() ?? string.Empty;
            holder = string.IsNullOrWhiteSpace(holder) ? MANUAL_LOOTER : holder.Trim();

            if (itemName.Length == 0)
                return (false, "Give an item name, for example `!bb additem Cloak of Flames` or `!bb additem Cloak of Flames | Soandso`.");

            if (itemName.Contains(" was given to ", StringComparison.OrdinalIgnoreCase))
                return (false, "The item name can't contain \"was given to\". Use `!bb manuallog` for raw log lines.");

            // The parser reads the holder up to the last period, so a period in the holder would cut it short.
            if (holder.Contains('.'))
                return (false, "The holder's name can't contain a period.");

            var raid = dataStore.ActiveRaid;
            if (raid == null || raid.EndDT.HasValue)
                return (false, "There's no active raid. Run /setsheet first.");

            var timestamp = DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy");
            logStore.Enqueue($"[{timestamp}] {itemName} was given to {holder}.");

            return (true, $"Added {itemName} (holder {holder}). It'll show up on the sheet and in the Loot Queue in a few seconds.");
        }

        /// <summary>
        /// Writes fake "was given to" EQ log lines for random items from past raids, so they go through
        /// exactly the same path as real loot: sheet row, raid loot list and Loot Queue. Items are picked
        /// with replacement, so the same item can show up more than once. Falls back to the item list
        /// when there's no raid history yet.
        /// </summary>
        public (bool WasSuccessful, string Message, List<string> Items) AddSimulatedLoot(int count)
        {
            var pool = dataStore.HistoricalAuctions.Values
                .Select(x => x.Item?.Name)
                .Concat(dataStore.Raids.Values.SelectMany(x => x.LootedItems ?? new List<(string item, string character)>()).Select(x => x.item))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var source = "past raids";
            if (pool.Count == 0)
            {
                pool = itemListStore.Get()?.Items.Select(x => x.Name).ToList() ?? new List<string>();
                source = "the item list (no raid history yet)";
            }

            if (pool.Count == 0)
                return (false, "There are no past raid items or item list entries to pick from.", new List<string>());

            var picked = Enumerable.Range(0, count).Select(_ => pool[Random.Shared.Next(pool.Count)]).ToList();

            // Same format EverQuest writes, so the parser treats it like real loot.
            var timestamp = DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy");
            foreach (var item in picked)
                logStore.Enqueue($"[{timestamp}] {item} was given to {SIMULATED_LOOTER}.");

            return (true, $"Simulated {picked.Count} loot drop{(picked.Count == 1 ? "" : "s")} from {source}", picked);
        }
    }
}
