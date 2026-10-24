using BidBot.Items;
using System;
using System.Collections.Generic;

namespace BidBot.DataAccess
{
    /// <summary>
    /// Simple holder for the loaded ItemList so it can be registered as a singleton
    /// and populated at runtime (for example when the Worker starts).
    /// </summary>
    public sealed class ItemListStore
    {
        private ItemList? list;

        public void Set(ItemList list)
        {
            this.list = list ?? throw new ArgumentNullException(nameof(list));
        }

        public ItemList Get()
        {
            return list ?? new ItemList(Array.Empty<ItemEntry>());
        }

        public bool TryGet(out ItemList? list)
        {
            list = this.list;
            return this.list != null;
        }
    }
}
