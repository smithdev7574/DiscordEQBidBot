using BidBot.Logic;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Messaging
{
    public class SelectHandler
    {
        public const string ITEM_SELECT = "itemSelect";

        public SelectHandler(BidService bidService)
        {
            BidService = bidService;
        }

        public BidService BidService { get; }

        public async Task Handle(SocketMessageComponent component)
        {
            if (!component.Data.CustomId.Contains(":"))
                return;

            var prefix = component.Data.CustomId.Split(":").First();
            switch (prefix)
            {
                case ITEM_SELECT:
                    await component.DeferAsync();

                    break;
                default:
                    break;
            }
        }
    }
}
