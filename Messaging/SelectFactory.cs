using BidBot.Infrastructure;
using Discord;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Messaging
{
    public class SelectFactory
    {
        private readonly ICommunicationService communication;

        public SelectFactory(ICommunicationService communication)
        {
            this.communication = communication;
        }

        public async Task SendSelectMessage(ulong userID, string messageContent, List<MessageComponent> selectComponents)
        {
            bool isFirstPass = true;
            foreach (var selectComponent in selectComponents)
            {
                if (!isFirstPass)
                    messageContent = string.Empty;

                var message = await communication.SendTell(userID, messageContent, selectComponent);
                isFirstPass = false;
            }
        }

        public MessageComponent BuildSingleSelect(string suffixData, string customIDPrefix, string placeholder, IEnumerable<ISelectItem> dataList)
        {
            var builder = new ComponentBuilder();
            var menuBuilder = new SelectMenuBuilder()
                .WithPlaceholder(placeholder)
                .WithCustomId($"{customIDPrefix}:{suffixData}")
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (var item in dataList)
            {
                menuBuilder.AddOption(item.Label, item.Value);
            }
            builder = builder.WithSelectMenu(menuBuilder);

            return builder.Build();
        }

        public List<MessageComponent> BuildSelect(Guid requestID, string customIdPrefix, string placeholder, IEnumerable<ISelectItem> dataList)
        {
            List<MessageComponent> components = new List<MessageComponent>();

            var lists = dataList.SplitByLength(25); //Discord has a limit of 25 items per drop down.

            var builder = new ComponentBuilder();
            int i = 0;
            foreach (var list in lists)
            {
                i++;
                var menuBuilder = new SelectMenuBuilder()
                    .WithPlaceholder(placeholder)
                    .WithCustomId($"{customIdPrefix}:[{i}]{requestID}")
                    .WithMinValues(1)
                    .WithMaxValues(1);

                foreach (var item in list)
                {
                    menuBuilder.AddOption(item.Label, item.Value);
                }
                builder = builder.WithSelectMenu(menuBuilder);

                if (i % 4 == 0)
                {
                    components.Add(builder.Build());
                    builder = new ComponentBuilder();
                }
            }

            if (i % 4 != 0)
            {
                components.Add(builder.Build());
            }
            return components;
        }

    }
}
