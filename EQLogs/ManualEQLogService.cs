using BidBot.DataAccess;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace BidBot.EQLogs
{
    public class ManualEQLogService
    {
        private readonly EQLogStore logStore;

        public ManualEQLogService(EQLogStore logStore)
        {
            this.logStore = logStore;
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
    }
}
