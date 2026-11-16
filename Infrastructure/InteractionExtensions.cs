using Discord;
using System;

namespace BidBot.Infrastructure
{
    public static class InteractionExtensions
    {
        public static readonly TimeSpan ShortReply = TimeSpan.FromSeconds(8);
        public static readonly TimeSpan ErrorReply = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Sends an ephemeral follow-up and deletes it after a while, so replies to the Loot Queue
        /// post's buttons don't pile up under it. The interaction token lasts 15 minutes, so keep
        /// the delay well under that. Never throws: a reply that can't be deleted just stays.
        /// </summary>
        public static async Task FollowupThenExpire(this IDiscordInteraction interaction, string text, TimeSpan after)
        {
            IUserMessage reply;
            try
            {
                reply = await interaction.FollowupAsync(text, ephemeral: true);
            }
            catch
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(after);
                    await reply.DeleteAsync();
                }
                catch
                {
                    // Already dismissed by the user, or the token expired. Nothing to clean up.
                }
            });
        }
    }
}
