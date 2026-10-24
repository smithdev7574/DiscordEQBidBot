using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace BidBot.Infrastructure
{
    public static class Helpers
    {
        public static List<List<T>> SplitByLength<T>(this IEnumerable<T> list, int listLength)
        {
            var retVal = new List<List<T>>();
            var listCount = Math.Ceiling(list.Count() / (decimal)listLength);

            for (int x = 0; x < listCount; x++)
            {
                var listToAdd = list.Skip(x * listLength).Take(listLength).ToList();
                retVal.Add(listToAdd);
            }
            return retVal;
        }

        public static string GetRequestFromCustomID(this string customID, string prefix)
        {
            var requestIDStr = customID.Replace(prefix, string.Empty);
            var bracketIndex = requestIDStr.IndexOf(']');
            requestIDStr = requestIDStr.Substring(bracketIndex + 1);
            return requestIDStr;
        }

        public static string TrimToLength(this string value, int length)
        {
            var trimmedString = value.Length > length
                ? value.Substring(0, length)
                : value;

            return trimmedString;
        }

        public static string SplitIntoColumns(this IEnumerable<string> displayValues, int columnCount)
        {
            var sb = new StringBuilder();
            var lineSb = new StringBuilder();

            int x = 0;
            foreach (var value in displayValues)
            {
                x++;
                lineSb.Append($"{value} | ");

                if (x % columnCount == 0)
                {
                    sb.AppendLine($"* {lineSb.ToString()}");
                    lineSb.Clear();
                }
            }
            if (lineSb.Length > 0)
            {
                sb.AppendLine($"* {lineSb.ToString()}");
            }
            return sb.ToString();
        }

        public static (bool WasSuccessful, string Message) TryGetRequiredInt(this SocketModal modal, string fieldID, out int value)
        {
            value = 0;
            var component = modal.Data.Components.ToList().FirstOrDefault(x => x.CustomId.StartsWith(fieldID));
            if (component == null)
            {
                return (false, "Error: Malformed Modal, please start over."); ;
            }

            if (string.IsNullOrWhiteSpace(component.Value))
            {
                return (false, $"{fieldID} is a required element.");
            }

            if (!int.TryParse(component.Value, out value))
            {
                return (false, $"{fieldID} must be numeric.");
            }

            return (true, "");

        }
    }
}
