using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BidBot.Items
{
    public class ItemListParser
    {
        private readonly string _path;
        private readonly ILogger<ItemListParser> _logger;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        private ItemList? _cache;

        public ItemListParser(IConfiguration config, ILogger<ItemListParser> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var pathFromConfig = config?["Data:ItemListPath"];
            if (string.IsNullOrWhiteSpace(pathFromConfig))
            {
                _path = Path.Combine(AppContext.BaseDirectory, "Data", "itemlist.txt");
            }
            else
            {
                _path = Path.IsPathRooted(pathFromConfig) ? pathFromConfig : Path.Combine(AppContext.BaseDirectory, pathFromConfig);
            }
        }

        /// <summary>
        /// Loads and returns the ItemList. Subsequent calls return a cached instance until Reload is called.
        /// </summary>
        public async Task<ItemList> Load()
        {
            if (_cache != null)
                return _cache;

            await _lock.WaitAsync();
            try
            {
                if (_cache != null)
                    return _cache;

                var items = new List<ItemEntry>();

                if (!File.Exists(_path))
                {
                    _logger.LogWarning("Item list file not found at {Path}", _path);
                    _cache = new ItemList(items);
                    return _cache;
                }

                try
                {
                    // The first line is a header row -- skip it
                    foreach (var line in File.ReadLines(_path).Skip(1))
                    {
                        var trimmed = line?.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed))
                            continue;

                        // Expecting exactly three comma-separated values: id,name,lucylink
                        var parts = trimmed.Split(',');
                        if (parts.Length < 3)
                        {
                            _logger.LogWarning("Skipping malformed line in item list: {Line}", line);
                            continue;
                        }

                        var id = parts[0].Trim();
                        var name = TrimSurroundingQuotes(parts[1].Trim());
                        var lucy = TrimSurroundingQuotes(parts[2].Trim());

                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                        {
                            _logger.LogWarning("Skipping line with empty id or name: {Line}", line);
                            continue;
                        }

                        if (!int.TryParse(id, out int itemId))
                        {
                            _logger.LogWarning("Skipping line with invalid ID: {Line}", line);
                            continue;
                        }

                        items.Add(new ItemEntry(itemId, name, lucy));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read item list from {Path}", _path);
                }

                _cache = new ItemList(items);
                return _cache;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Clears the cached ItemList so the next Load will re-read the file.
        /// </summary>
        public void Reload()
        {
            lock (_lock)
            {
                _cache = null;
            }
        }

        private static string TrimSurroundingQuotes(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            // Remove a single pair of surrounding double or single quotes, if present
            if ((s.Length >= 2) && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            {
                return s.Substring(1, s.Length - 2);
            }

            return s;
        }
    }
}
