using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Infrastructure
{
    public class PermissionCalculator
    {
        public PermissionCalculator()
        {
            
        }

        public(bool HasPermission, string Message) CheckManagerPermissions(SocketCommandContext context, string command)
        {
            var user = context.User as IGuildUser;
            if (user == null) return (false, "Could not find user.");

            if (!user.GuildPermissions.ManageGuild)
                return (false, $"You must be at least a manager within the guild to perform {command}");

            return (true, string.Empty);
        }

        public (bool HasPermission, string Message) CheckManagerPermissions(SocketSlashCommand command, string commandName)
        {
            var user = command.User as IGuildUser;
            if(user == null) return (false, "Could not find user.");

            if(!user.GuildPermissions.ManageGuild)
                return (false, $"You must be at least a manager within the guild to perform {commandName}");

            return (true, string.Empty);
        }

        public (bool HasPermission, string Message) CheckManagerPermissions(IGuildUser user, string commandName)
        {
            if (user == null) return (false, "Could not find user.");

            if (!user.GuildPermissions.ManageGuild)
                return (false, $"You must be at least a manager within the guild to perform {commandName}");

            return (true, string.Empty);

        }
    }
}
