using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Infrastructure
{
    public class UserService
    {
        private readonly DiscordSocketClient client;

        public UserService(DiscordSocketClient client)
        {
            this.client = client;
        }

        public string GetUsername(ulong guildID, ulong userID)
        {
            var guild = client.GetGuild(guildID);
            var guildUser = guild?.GetUser(userID);
            return guildUser?.Nickname ?? guildUser?.DisplayName ?? guildUser?.Username;
        }
    }
}
