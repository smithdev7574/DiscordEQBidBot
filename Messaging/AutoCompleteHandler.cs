using BidBot.DataAccess;
using BidBot.Infrastructure;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Messaging
{
    public class AutoCompleteHandler
    {
        private readonly ItemListStore itemStore;

        public AutoCompleteHandler(ItemListStore itemStore)
        {
            this.itemStore = itemStore;
        }

        public async Task<IEnumerable<AutocompleteResult>> GetAutoComplete(SocketAutocompleteInteraction arg)
        {
            var data = new List<AutocompleteResult>();
            var commandName = arg.Data.Current.Name;
            switch (commandName)
            {
                case SlashCommandHandler.ITEM_NAME_ARGUMENT:
                    data = getItemNameMatches(arg.Data.Current.Value?.ToString());
                    break;
            }

            return data;
        }

        private List<AutocompleteResult> getItemNameMatches(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length < 3)
                return new List<AutocompleteResult>();

            var itemList = itemStore.Get();
            var matchingItems = itemList.Items.Where(x => x.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(x => new AutocompleteResult(x.Name, x.Name))
                .ToList();

            return matchingItems;

        }
    }
}
