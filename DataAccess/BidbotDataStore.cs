using BidBot.DataAccess.Entities;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace BidBot.DataAccess
{
    public class BidbotDataStore
    {
        public const string DATA_FILE_NAME = "bidbotdata.json";
        private BidbotData bidbotData;
        string fileName;
        private readonly IConfiguration config;
        protected readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);


        public ConcurrentDictionary<Guid, WishlistEntry> Wishlist => bidbotData.Wishlist;
        public ConcurrentDictionary<int, ItemHtmlEntry> ItemHtmlEntries => bidbotData.ItemHtmlEntries;
        public ConcurrentDictionary<Guid, Auction> HistoricalAuctions => bidbotData.HistoricalAuctions;
        public ConcurrentDictionary<Guid, Raid> Raids => bidbotData.Raids;
        public BidBotSettings Settings => bidbotData.Settings;

        public BidbotDataStore(IConfiguration config)
        {
            this.config = config;
            fileName = getFileName();
            bidbotData = loadFromDataStore();
        }

        private string getFileName()
        {
            string path;
            var dataFilePath = config?["Data:DataFilePath"];

            if (string.IsNullOrWhiteSpace(dataFilePath) || dataFilePath.StartsWith("__"))
            {
                path = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                path = Path.Combine(path, "Data");
            }
            else
            {
                path = dataFilePath;
            }

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            string fileName = Path.Combine(path, DATA_FILE_NAME);
            return fileName;
        }

        public Raid ActiveRaid
        {
            get
            {
                var raid = Raids.Values.OrderByDescending(x => x.StartDT).FirstOrDefault(x => x.EndDT == null);
                return raid;
            }
        }

        private BidbotData loadFromDataStore()
        {
            var data = new BidbotData();
            if(File.Exists(fileName))
            {
                string jsonBlob = File.ReadAllText(fileName);
                data = JsonConvert.DeserializeObject<BidbotData>(jsonBlob);
            }
            return data;
        }

        public async Task SaveChanges()
        {
            await _lock.WaitAsync();
            try
            {
                var jsonBlob = JsonConvert.SerializeObject(bidbotData);
                File.WriteAllText(fileName, jsonBlob);
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
