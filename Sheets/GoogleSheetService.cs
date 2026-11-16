using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace BidBot.Sheets
{
    public class GoogleSheetService
    {
        private readonly BidbotDataStore store;
        private SheetsService service;
        private readonly string sheetName;
        private readonly string winnerColumn;
        private readonly string amountColumn;
        private readonly string itemNameColumnn;
        private readonly string heldByColumn;
        private readonly string? auctionStartedColumn;
        private readonly string? paidColumn;
        private readonly ILogger<GoogleSheetService> logger;
        private (string SheetID, string Title)? cachedTabTitle;

        // Loot adds can wait out a full quota minute, nothing else is blocked by them.
        private static readonly TimeSpan[] LongRetry = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32) };
        // Auction start/finish updates retry briefly so they don't hold up other auctions closing.
        private static readonly TimeSpan[] ShortRetry = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

        public bool HasPaidColumn => !string.IsNullOrWhiteSpace(paidColumn);
        public string CurrentSheetID => store.Settings.CurrentSheetID;
        public bool IsSheetCurrent => DateTime.UtcNow < store.Settings.SheetDT.AddDays(1);

        public GoogleSheetService(BidbotDataStore store, IConfiguration config, ILogger<GoogleSheetService> logger)
        {
            this.store = store;
            this.logger = logger;

            this.sheetName = config["SheetSettings:SheetName"] ?? throw new ArgumentException("App Settings Missing SheetSettings:SheetName");
            this.winnerColumn = config["SheetSettings:WinnerColumn"] ?? throw new ArgumentException("App Settings missing SheetSettings:WinnerColumn");
            this.amountColumn = config["SheetSettings:AmountColumn"] ?? throw new ArgumentException("App Settings missing SheetSettings:AmountColumn");
            this.itemNameColumnn = config["SheetSettings:ItemNameColumn"] ?? throw new ArgumentException("App Settings missing SheetSettings:ItemNameColumn");
            this.heldByColumn = config["SheetSettings:HeldByColumn"] ?? throw new ArgumentException("App Settings missing SheetSettings:HeldByColumn");
            this.auctionStartedColumn = config["SheetSettings:AuctionStartedColumn"]; // Optional
            this.paidColumn = config["SheetSettings:PaidColumn"]; // Optional, payment reminders are off without it

            var credentialFile = config["SheetSettings:CredentialFile"] ?? throw new ArgumentException("App Settings Missing SheetSettings:CredentialFile");
            service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = getCredential(credentialFile),
                ApplicationName = "Bidbot"
            });
        }

        public async Task<(bool WasSuccessful, string Message)> AddItemToSheet(string itemName, string playerName)
        {
            var result = await AddItemsToSheet(new[] { (itemName, playerName) });
            return (result.WasSuccessful, result.Message);
        }

        /// <summary>
        /// Writes looted items to the next open rows and returns the row each one went to, in order.
        /// However many items there are, this is one read (the item column) and one write, which keeps
        /// big loot drops and /simulateloot well under Google's per-minute limits.
        /// </summary>
        public async Task<(bool WasSuccessful, string Message, List<int> Rows)> AddItemsToSheet(IReadOnlyList<(string ItemName, string PlayerName)> items)
        {
            if (items.Count == 0)
                return (true, string.Empty, new List<int>());

            if (DateTime.UtcNow >= store.Settings.SheetDT.AddDays(1))
                return (false, $"The sheet hasn't been changed in over 24 hours, please run the SetSheet command.", new List<int>());

            try
            {
                var rows = await getOpenRows(items.Count);
                var data = new List<ValueRange>();
                for (int i = 0; i < items.Count; i++)
                {
                    data.Add(new ValueRange
                    {
                        Range = $"'{sheetName}'!{itemNameColumnn}{rows[i]}",
                        Values = new List<IList<object>> { new List<object> { items[i].ItemName } }
                    });
                    data.Add(new ValueRange
                    {
                        Range = $"'{sheetName}'!{heldByColumn}{rows[i]}",
                        Values = new List<IList<object>> { new List<object> { items[i].PlayerName } }
                    });
                }

                var batch = new BatchUpdateValuesRequest { ValueInputOption = "USER_ENTERED", Data = data };
                await withRetry(() => service.Spreadsheets.Values.BatchUpdate(batch, store.Settings.CurrentSheetID).ExecuteAsync(), LongRetry);
                return (true, string.Empty, rows);
            }
            catch (Exception ex)
            {
                cachedTabTitle = null; // In case the tab was renamed, look it up again next time.
                return (false, $"Error updating Sheet: {ex.Message}", new List<int>());
            }
        }

        /// <summary>
        /// Checks (or unchecks) the auction-started checkbox (SheetSettings:AuctionStartedColumn) for a row,
        /// so the sheet shows which auctions have been started. Skipped when the setting is blank.
        /// </summary>
        public async Task<(bool WasSuccessful, string Message)> SetAuctionStarted(int? rowNumber, bool started)
        {
            if (string.IsNullOrWhiteSpace(auctionStartedColumn))
                return (true, "No auction started column configured.");

            if (!rowNumber.HasValue || rowNumber <= 1)
                return (false, $"Row number is invalid: {rowNumber}");

            if (DateTime.UtcNow >= store.Settings.SheetDT.AddDays(1))
                return (false, $"The sheet hasn't been changed in over 24 hours, please run the SetSheet command.");

            var values = new ValueRange
            {
                Values = new List<IList<object>> { new List<object> { started } }
            };

            try
            {
                await updateValues($"'{sheetName}'!{auctionStartedColumn}{rowNumber}", values);
            }
            catch (Exception ex)
            {
                return (false, $"Error updating Sheet: {ex.Message}");
            }

            return (true, "Sheet Updated");
        }

        /// <summary>
        /// Empties a looted item's row (item, holder, winner, amount, and the auction-started box) for loot
        /// that won't be auctioned. Clears instead of deleting, because deleting would shift every row
        /// below it and break the row numbers queued items and running auctions point at. The empty row
        /// is reused by the next loot.
        /// </summary>
        public async Task<(bool WasSuccessful, string Message)> ClearItemRow(int rowNumber)
        {
            if (rowNumber < 5)
                return (false, $"Row {rowNumber} is above the loot rows, nothing was cleared.");

            if (!IsSheetCurrent)
                return (false, "The sheet hasn't been changed in over 24 hours, please run the SetSheet command.");

            var data = new List<ValueRange>();
            foreach (var column in new[] { itemNameColumnn, heldByColumn, winnerColumn, amountColumn })
            {
                data.Add(new ValueRange
                {
                    Range = $"'{sheetName}'!{column}{rowNumber}",
                    Values = new List<IList<object>> { new List<object> { string.Empty } }
                });
            }

            // Checkbox cells keep their checkbox, they're just unchecked, so the next loot starts clean.
            foreach (var checkboxColumn in new[] { auctionStartedColumn, paidColumn })
            {
                if (string.IsNullOrWhiteSpace(checkboxColumn))
                    continue;

                data.Add(new ValueRange
                {
                    Range = $"'{sheetName}'!{checkboxColumn}{rowNumber}",
                    Values = new List<IList<object>> { new List<object> { false } }
                });
            }

            var batch = new BatchUpdateValuesRequest { ValueInputOption = "USER_ENTERED", Data = data };
            try
            {
                await withRetry(() => service.Spreadsheets.Values.BatchUpdate(batch, store.Settings.CurrentSheetID).ExecuteAsync(), ShortRetry);
            }
            catch (Exception ex)
            {
                return (false, $"Error updating Sheet: {ex.Message}");
            }

            return (true, "Sheet Updated");
        }

        public async Task<(bool WasSuccessful, string Message)> UpdateWinner(Auction auction)
        {
            if (DateTime.UtcNow >= store.Settings.SheetDT.AddDays(1))
                return (false, $"The sheet hasn't been changed in over 24 hours, please run the SetSheet command.");

            if (!auction.RowNumber.HasValue && auction.RowNumber <= 1)
                return (false, $"Row number is invalid: {auction.RowNumber}");


            var batch = new BatchUpdateValuesRequest
            {
                ValueInputOption = "USER_ENTERED",
                Data = new List<ValueRange>
                {
                    new ValueRange
                    {
                        Range = $"'{sheetName}'!{winnerColumn}{auction.RowNumber}",
                        Values = new List<IList<object>> { new List<object> { auction.Winner } }
                    },
                    new ValueRange
                    {
                        Range = $"'{sheetName}'!{amountColumn}{auction.RowNumber}",
                        Values = new List<IList<object>> { new List<object> { auction.WinAmount.ToString("#,###") } }
                    }
                }
            };


            try
            {
                await withRetry(() => service.Spreadsheets.Values.BatchUpdate(batch, store.Settings.CurrentSheetID).ExecuteAsync(), ShortRetry);
            }
            catch (Exception ex)
            {
                return (false, $"Error updating Sheet: {ex.Message}");
            }

            return (true, "Sheet Updated");
        }

        /// <summary>
        /// Reads the Winner and Paid cells for the given rows in one call. A row is paid when its
        /// Paid checkbox is checked (TRUE).
        /// </summary>
        public async Task<(bool WasSuccessful, string Message, Dictionary<int, (string Winner, bool IsPaid)> Rows)> GetPaymentStatus(IEnumerable<int> rowNumbers)
        {
            var result = new Dictionary<int, (string Winner, bool IsPaid)>();

            if (!HasPaidColumn)
                return (false, "SheetSettings:PaidColumn is not set.", result);

            if (!IsSheetCurrent)
                return (false, "The sheet hasn't been changed in over 24 hours, please run the SetSheet command.", result);

            var rows = rowNumbers.Where(x => x > 1).Distinct().ToList();
            if (rows.Count == 0)
                return (true, string.Empty, result);

            var lastRow = rows.Max();
            var request = service.Spreadsheets.Values.BatchGet(store.Settings.CurrentSheetID);
            request.Ranges = new[]
            {
                $"'{sheetName}'!{winnerColumn}1:{winnerColumn}{lastRow}",
                $"'{sheetName}'!{paidColumn}1:{paidColumn}{lastRow}"
            };

            try
            {
                var response = await withRetry(() => request.ExecuteAsync(), ShortRetry);
                var winnerValues = response.ValueRanges?.ElementAtOrDefault(0)?.Values;
                var paidValues = response.ValueRanges?.ElementAtOrDefault(1)?.Values;

                foreach (var row in rows)
                {
                    var winner = cellText(winnerValues, row);
                    var paid = cellText(paidValues, row);
                    result[row] = (winner, string.Equals(paid, "TRUE", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                return (false, $"Error reading Sheet: {ex.Message}", result);
            }

            return (true, string.Empty, result);
        }

        private static string cellText(IList<IList<object>>? values, int rowNumber)
        {
            // Sheets leaves trailing empty rows and cells out of the response.
            var index = rowNumber - 1;
            if (values == null || index >= values.Count || values[index].Count == 0)
                return string.Empty;

            return values[index][0]?.ToString()?.Trim() ?? string.Empty;
        }

        private async Task updateValues(string range, ValueRange values)
        {
            var updateRequest = service.Spreadsheets.Values.Update(values, store.Settings.CurrentSheetID, range);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            await withRetry(() => updateRequest.ExecuteAsync(), ShortRetry);
        }

        /// <summary>
        /// Finds the first count empty item cells from row 5 down (filling gaps first, then after the last row),
        /// with one read. The tab name comes from the cache, so there's no metadata read after the first time.
        /// </summary>
        private async Task<List<int>> getOpenRows(int count)
        {
            var title = await getFirstTabTitle();
            var range = $"'{title}'!{itemNameColumnn}:{itemNameColumnn}";
            var response = await withRetry(() => service.Spreadsheets.Values.Get(store.Settings.CurrentSheetID, range).ExecuteAsync(), LongRetry);
            var values = response.Values ?? new List<IList<object>>();

            var rows = new List<int>();
            // Start scanning at row 5 (index 4). Google returns [] or [""] for cleared rows.
            for (int i = 4; i < values.Count && rows.Count < count; i++)
            {
                if (values[i].Count == 0 || string.IsNullOrWhiteSpace(values[i][0]?.ToString()))
                    rows.Add(i + 1);
            }

            var next = Math.Max(values.Count + 1, 5);
            while (rows.Count < count)
                rows.Add(next++);

            return rows;
        }

        /// <summary>
        /// The first tab's real name (handles double spaces, trailing spaces, punctuation). Cached per
        /// spreadsheet, since it costs a read request every time and almost never changes mid-raid.
        /// </summary>
        private async Task<string> getFirstTabTitle()
        {
            var sheetID = store.Settings.CurrentSheetID;
            var cached = cachedTabTitle;
            if (cached != null && cached.Value.SheetID == sheetID)
                return cached.Value.Title;

            var meta = await withRetry(() => service.Spreadsheets.Get(sheetID).ExecuteAsync(), LongRetry);
            var title = meta.Sheets.First().Properties.Title;
            cachedTabTitle = (sheetID, title);
            return title;
        }

        /// <summary>
        /// Google allows 60 reads and 60 writes per minute. When it says too many requests (or has a
        /// server hiccup), wait and try again instead of losing the update. The delays double each time.
        /// </summary>
        private async Task<T> withRetry<T>(Func<Task<T>> call, TimeSpan[] delays)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await call();
                }
                catch (GoogleApiException ex) when (attempt < delays.Length && isTransient(ex))
                {
                    logger.LogWarning("Google Sheets said {Status}, trying again in {Seconds} seconds.", ex.HttpStatusCode, delays[attempt].TotalSeconds);
                    await Task.Delay(delays[attempt]);
                }
            }
        }

        private static bool isTransient(GoogleApiException ex)
        {
            return ex.HttpStatusCode == HttpStatusCode.TooManyRequests || (int)ex.HttpStatusCode >= 500;
        }

        private GoogleCredential getCredential(string fileName)
        {
            var credential = GoogleCredential.
                FromFile(fileName)
                .CreateScoped(SheetsService.Scope.Spreadsheets);

            return credential;
        }

        public async Task<string> SetSheet(string url, ulong? guildId)
        {
            const string marker = "/d/";
            var start = url.IndexOf(marker);

            if (start == -1)
                throw new ArgumentException("Invalid Google Sheets URL.");

            start += marker.Length;

            var end = url.IndexOf('/', start);
            if (end == -1)
                end = url.Length;

            var sheetID = url.Substring(start, end - start);
            store.Settings.CurrentSheetID = sheetID;
            store.Settings.GuildID = guildId;
            store.Settings.SheetDT = DateTime.UtcNow;
            var Task = store.SaveChanges(); // Don't care about waiting...
            return sheetID;
        }

    }
}
