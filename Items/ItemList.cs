using BidBot.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BidBot.Items
{
    public sealed class ItemEntry :ISelectItem
    {
        public int ID { get; }
        public string Name { get; }
        public string LucyLink { get; }

        public string Label => $"{ID} - {Name}";
        public string Value => ID.ToString();

        public ItemEntry(int id, string name, string lucyLink)
        {
            ID = id;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            LucyLink = lucyLink ?? string.Empty;
        }
    }

    public sealed class ItemList
    {
        private readonly IReadOnlyList<ItemEntry> items;

        public ItemList(IEnumerable<ItemEntry> items)
        {
            this.items = (items ?? Enumerable.Empty<ItemEntry>()).ToList().AsReadOnly();
        }

        public IReadOnlyList<ItemEntry> Items => items;

        public bool TryGetById(int ID, out ItemEntry? entry)
        {
            entry = items.FirstOrDefault(x => x.ID == ID);
            return entry != null;
        }

        public IEnumerable<ItemEntry> FindByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Enumerable.Empty<ItemEntry>();

            //return items.Where(x => x.Name.ToLower().Contains(name.ToLower())).ToList();

            return items.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }
}
