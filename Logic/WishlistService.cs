using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using BidBot.Messaging;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Logic
{
    public class WishlistService
    {
        private readonly ICommunicationService communication;
        private readonly BidbotDataStore dataStore;
        private readonly ItemListStore itemStore;

        public WishlistService(ICommunicationService communication, BidbotDataStore dataStore, ItemListStore itemStore)
        {
            this.communication = communication;
            this.dataStore = dataStore;
            this.itemStore = itemStore;
        }

        public async Task<(bool WasSuccessful, string Message)> AddOrRemove(ulong userID, ulong? guildID, string itemName)
        {
            var items = itemStore.Get();
            var matchingItem = items.FindByName(itemName).FirstOrDefault();
            if (matchingItem == null)
                return (false, $"Can't locate item named {itemName}");

            var wishlistItem = dataStore.Wishlist.Values.FirstOrDefault(x => string.Equals(x.ItemName, itemName, StringComparison.OrdinalIgnoreCase)
                && x.UserID == userID && x.GuildID == guildID) ;

            StringBuilder sb = new StringBuilder();
            if (wishlistItem != null)
            {
                sb.AppendLine($"Removed {itemName} from your wishlist.");
                dataStore.Wishlist.TryRemove(wishlistItem.ID, out _);
            }
            else
            {
                sb.AppendLine($"Added {itemName} to your wishlist.");
                wishlistItem = new WishlistEntry()
                {
                    ID = Guid.NewGuid(),
                    GuildID = guildID,
                    UserID = userID,
                    ItemName = itemName
                };
                dataStore.Wishlist[wishlistItem.ID] = wishlistItem;
            }

            await dataStore.SaveChanges();
            var userWishList = dataStore.Wishlist.Values.Where(x => x.GuildID == guildID && x.UserID == userID).ToList();
            foreach(var item in userWishList)
            {
                sb.AppendLine($"* {item.ItemName}");
            }

            if (!userWishList.Any())
                sb.AppendLine("You have no items in your wish list.");

            await communication.SendTell(userID, sb.ToString());
            return (true, "Updated Wishlist...");
        }

        public async Task NotifyUsers(string itemName)
        {
            var wishListItems = dataStore.Wishlist.Values.Where(x => string.Equals(x.ItemName, itemName, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach(var item in wishListItems)
            {
                await communication.SendTell(item.UserID, $"Pssst, {itemName} is on your wishlist and it's up for bid!");
                await Task.Delay(1000);
            }
        }

    }
}
