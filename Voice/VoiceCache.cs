using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace BidBot.Voice
{
    /// <summary>
    /// Local cache of generated announcement audio so we only call Azure the first time
    /// a given phrase is spoken. Files are named by item name plus a hash of the spoken text,
    /// so changing the announcement template or pronunciations creates a fresh file.
    /// </summary>
    public class VoiceCache
    {
        public const string DEFAULT_CACHE_FOLDER = "VoiceCache";
        public const string FILE_EXTENSION = ".wav";
        private const int MAX_NAME_LENGTH = 60;
        private const int MIN_VALID_FILE_SIZE = 64; // a WAV header alone is 44 bytes

        private readonly string cacheDirectory;
        private readonly ILogger<VoiceCache> logger;

        public VoiceCache(VoiceSettings settings, IConfiguration config, ILogger<VoiceCache> logger)
        {
            this.logger = logger;
            this.cacheDirectory = getCacheDirectory(settings, config);
        }

        public string GetFilePath(string itemName, string spokenText)
        {
            var fileName = $"{sanitize(itemName)}-{hash(spokenText)}{FILE_EXTENSION}";
            return Path.Combine(cacheDirectory, fileName);
        }

        public bool TryGet(string itemName, string spokenText, out string filePath)
        {
            filePath = GetFilePath(itemName, spokenText);

            try
            {
                var info = new FileInfo(filePath);
                return info.Exists && info.Length >= MIN_VALID_FILE_SIZE;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to read voice cache file {FilePath}", filePath);
                return false;
            }
        }

        public async Task<string> Save(string itemName, string spokenText, byte[] audioData)
        {
            if (!Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            var filePath = GetFilePath(itemName, spokenText);

            // Write to a temp file first so a crash mid-write never leaves a broken file in the cache.
            var tempPath = filePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, audioData);
            File.Move(tempPath, filePath, overwrite: true);

            return filePath;
        }

        private static string getCacheDirectory(VoiceSettings settings, IConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(settings.CachePath) && !settings.CachePath.StartsWith("__"))
                return settings.CachePath;

            var dataFilePath = config?["Data:DataFilePath"];
            if (!string.IsNullOrWhiteSpace(dataFilePath) && !dataFilePath.StartsWith("__"))
                return Path.Combine(dataFilePath, DEFAULT_CACHE_FOLDER);

            var path = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            return Path.Combine(path, "Data", DEFAULT_CACHE_FOLDER);
        }

        private static string sanitize(string itemName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();

            foreach (var c in itemName ?? string.Empty)
            {
                if (invalid.Contains(c) || char.IsWhiteSpace(c))
                    sb.Append('_');
                else
                    sb.Append(c);
            }

            var name = sb.ToString().Trim('_', '.');
            if (string.IsNullOrWhiteSpace(name))
                name = "announcement";

            return name.Length > MAX_NAME_LENGTH ? name[..MAX_NAME_LENGTH] : name;
        }

        private static string hash(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
        }
    }
}
