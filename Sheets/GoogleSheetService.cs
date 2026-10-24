using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.IO.Pipelines;
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

        public GoogleSheetService(BidbotDataStore store, IConfiguration config)
        {
            this.store = store;

            this.sheetName = config["SheetSettings:SheetName"] ?? throw new ArgumentException("App Settings Missing SheetSettings:SheetName");
            this.winnerColumn = config["SheetSettings:WinnerColumn"] ?? throw new ArgumentException("App Settings missing SheetSettings:WinnerColumn");
            this.amountColumn = config["SheetSettings:AmountColumn"] ?? throw new ArgumentException("App Settings missing SheetSettings:AmountColumn");
            this.itemNameColumnn = config["SheetSettings:ItemNameColumn"] ?? throw new ArgumentException("App Settings missing SheetSettings:ItemNameColumn");
            this.heldByColumn = config["SheetSettings:HeldByColumn"] ?? throw new ArgumentException("App Settings missing SheetSettings:HeldByColumn");

            var credentialFile = config["SheetSettings:CredentialFile"] ?? throw new ArgumentException("App Settings Missing SheetSettings:CredentialFile");
            service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = getCredential(credentialFile),
                ApplicationName = "Bidbot"
            });
        }

        public async Task<(bool WasSuccessful, string Message)> AddItemToSheet(string itemName, string playerName)
        {
            if (DateTime.UtcNow >= store.Settings.SheetDT.AddDays(1))
                return (false, $"The sheet hasn't been changed in over 24 hours, please run the SetSheet command.");

            var row = await getFirstOpenRow();

            var batch = new BatchUpdateValuesRequest
            {
                ValueInputOption = "USER_ENTERED",
                Data = new List<ValueRange>
                {
                    new ValueRange
                    {
                        Range = $"'{sheetName}'!{itemNameColumnn}{row}",
                        Values = new List<IList<object>> { new List<object> { itemName } }
                    },
                    new ValueRange
                    {
                        Range = $"'{sheetName}'!{heldByColumn}{row}",
                        Values = new List<IList<object>> { new List<object> { playerName } }
                    }
                }
            };

            try
            {
                await service.Spreadsheets.Values.BatchUpdate(batch, store.Settings.CurrentSheetID).ExecuteAsync();
            }
            catch (Exception ex)
            {
                return (false, $"Error updating Sheet: {ex.Message}");
            }

            return (true, string.Empty);
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
                await service.Spreadsheets.Values.BatchUpdate(batch, store.Settings.CurrentSheetID).ExecuteAsync();
            }
            catch (Exception ex)
            {
                return (false, $"Error updating Sheet: {ex.Message}");
            }

            return (true, "Sheet Updated");
        }

        private async Task updateValues(string range, ValueRange values)
        {
            var updateRequest = service.Spreadsheets.Values.Update(values, store.Settings.CurrentSheetID, range);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            await updateRequest.ExecuteAsync();
        }

        private async Task<int> getFirstOpenRow()
        {
            // Always fetch the real sheet name (handles double spaces, trailing spaces, punctuation)
            var meta = await service.Spreadsheets.Get(store.Settings.CurrentSheetID).ExecuteAsync();
            string sheetName = meta.Sheets.First().Properties.Title;

            string range = $"'{sheetName}'!{itemNameColumnn}:{itemNameColumnn}";

            var request = service.Spreadsheets.Values.Get(store.Settings.CurrentSheetID, range);
            var response = await request.ExecuteAsync();

            // If column A has no values at all, first usable row is 5
            if (response.Values == null || response.Values.Count == 0)
                return 5;

            // Start scanning at row 5 (index 4)
            for (int i = 4; i < response.Values.Count; i++)
            {
                // Empty row cases:
                // - Google Sheets returns [] for a cleared row
                // - Or it returns [""] or ["   "]
                if (response.Values[i].Count == 0 ||
                    string.IsNullOrWhiteSpace(response.Values[i][0]?.ToString()))
                {
                    return i + 1; // Convert 0-based index to 1-based row number
                }
            }

            // No gaps found → append after last row
            return response.Values.Count + 1;
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
