using Discord;
using Discord.Rest;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Channels;

namespace BidBot.Messaging
{
    public class CommunicationService: ICommunicationService
    {
        private DiscordSocketClient client;
        private readonly string logChannelName;
        private readonly bool shouldLogToChannel;

        public CommunicationService(DiscordSocketClient client, IConfiguration configuration)
        {
            this.client = client;
            this.logChannelName = configuration["Settings:LogChannelName"];
            bool.TryParse(configuration["Settings:ShouldLogToChannel"], out shouldLogToChannel);
        }

        public async Task<RestUserMessage> SendMessage(ulong guildID, ulong channelID, string message)
        {
            var guild = client.GetGuild(guildID);
            var channel = guild?.GetTextChannel(channelID);

            if(channel == null)
                throw new ArgumentException($"Channel with ID {channelID} not found in guild {guildID}");

            var returnMessage = await channel.SendMessageAsync(message);
            return returnMessage;
        }

        public async Task<RestUserMessage> SendMessageByChannelName(ulong guildID, string channelName, string message, MessageComponent components = null, Embed embed = null)
        {
            var guild = client.GetGuild(guildID);
            var channel = guild?.TextChannels.FirstOrDefault(c => c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));
            if (channel == null)
                throw new ArgumentException($"Channel with name {channelName} not found in guild {guildID}");
            var returnMessage = await channel.SendMessageAsync(message, components: components, embed: embed);
            return returnMessage;
        }

        public async Task<RestUserMessage> SendLogMessage(ulong? guildID, ulong userID, string message)
        {
            if (!shouldLogToChannel || string.IsNullOrWhiteSpace(logChannelName) || guildID == null)
                return null;

            var mention = MentionUtils.MentionUser(userID);
            message = $"{mention} - {message}";
            return await SendMessageByChannelName(guildID.Value, logChannelName, message);
        }

        public async Task SendTell(ulong userID, string message)
        {
            var user = client.GetUser(userID);
            if (user == null)
                throw new ArgumentException($"User with ID {userID} not found");

            await user.SendMessageAsync(message);
        }

        public async Task<IUserMessage> SendTell(ulong userID, string message, MessageComponent component)
        {
            var user = client.GetUser(userID);
            if (user == null)
                throw new ArgumentException($"User with ID {userID} not found");
            var messageResponse = await user.SendMessageAsync(message, components: component);
            return messageResponse;
        }

        public async Task React(ulong guildID, ulong channelID, ulong messageID, string emojiString)
        {
            var guild = client.GetGuild(guildID);
            var channel = guild?.GetTextChannel(channelID);
            if (channel == null)
                throw new ArgumentException($"Channel with ID {channelID} not found in guild {guildID}");

            var message = await channel.GetMessageAsync(messageID);
            if (message == null)
                throw new ArgumentException($"Message with ID {messageID} not found in channel {channelID}");

            var emoji = new Emoji(emojiString);
            await message.AddReactionAsync(emoji);
        }

        public async Task DeleteMessage(ulong guildID, ulong channelID, ulong messageID)
        {
            var guild = client.GetGuild(guildID);
            var channel = guild?.TextChannels.FirstOrDefault(c => c.Id == channelID);

            if (channel == null)
                throw new ArgumentException($"Channel with ID {channelID} not found in guild {guildID}");

            var message = await channel.GetMessageAsync(messageID);
            if (message == null)
                throw new ArgumentException($"Message with ID {messageID} not found in channel {channelID}");

            await message.DeleteAsync();
        }

        public async Task UpdateMessage(ulong guildID, ulong channelID, ulong messageID, string content)
        {
            var guild = client.GetGuild(guildID);
            var channel = guild?.GetTextChannel(channelID);

            if(channel != null)
            {
                await channel.ModifyMessageAsync(messageID, msg =>
                {
                    msg.Content = content;
                });
            }
        }

        public async Task UpdateMessageAndRemoveComponents(ulong guildID, ulong channelID, ulong messageID, string content)
        {
            var guild = client.GetGuild(guildID);
            var channel = guild?.GetTextChannel(channelID);

            if (channel != null)
            {
                await channel.ModifyMessageAsync(messageID, msg =>
                {
                    msg.Content = content;
                    msg.Components = new ComponentBuilder().Build();
                });
            }

        }
    }
}
