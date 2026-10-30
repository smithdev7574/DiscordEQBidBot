using System.Threading.Tasks;
using Discord;
using Discord.Rest;

namespace BidBot.Messaging
{
    public interface ICommunicationService
    {
        Task<RestUserMessage> SendMessage(ulong guildID, ulong channelID, string message);

        Task<RestUserMessage> SendMessageByChannelName(ulong guildID, string channelName, string message, MessageComponent components = null, Embed embed = null, MessageFlags flags = MessageFlags.None);

        Task SendTell(ulong userID, string message);

        Task<IUserMessage> SendTell(ulong userID, string message, MessageComponent component);

        Task React(ulong guildID, ulong channelID, ulong messageID, string emojiString);

        Task DeleteMessage(ulong guildID, ulong channelID, ulong messageID);

        Task UpdateMessage(ulong guildID, ulong channelID, ulong messageID, string content);
        Task UpdateMessageAndRemoveComponents(ulong guildID, ulong channelID, ulong messageID, string content);
        Task<RestUserMessage> SendLogMessage(ulong? guildID, ulong userID, string message);
    }
}
