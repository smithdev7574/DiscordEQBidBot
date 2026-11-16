using BidBot.DataAccess;
using BidBot.DataAccess.Entities;
using BidBot.Logic;
using Discord;
using Discord.Audio;
using Discord.LibDave;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace BidBot.Voice
{
    /// <summary>
    /// Plays voice announcements in the configured voice channel, one at a time.
    /// During a raid (/setsheet to /endraid) the bot stays in the channel. Otherwise, when
    /// something is queued the bot joins the channel, plays the queue in order, waits a few
    /// seconds for anything else, then leaves. Nothing in here is allowed to throw back into
    /// the rest of the bot: if voice breaks, auctions keep working.
    /// </summary>
    public class VoiceCommunicationService : BackgroundService
    {
        public const string VOICE_TEST_TEXT = "Bid Bot voice check. Can you hear me?";
        private static readonly string[] REQUIRED_NATIVE_LIBRARIES = { "opus", "libsodium", "libdave" };
        private static readonly TimeSpan RAID_CHECK_INTERVAL = TimeSpan.FromMinutes(1);
        private const string CONNECT_FAILURE_HINT =
            "Check that Windows Firewall allows BidBot (voice uses UDP), and set Voice:DebugLogging to true and restart for detailed voice logs.";

        private readonly ILogger<VoiceCommunicationService> logger;
        private readonly DiscordSocketClient client;
        private readonly VoiceSettings settings;
        private readonly VoiceService voiceService;
        private readonly AuctionStore auctionStore;
        private readonly BidbotDataStore dataStore;
        private readonly Channel<VoiceAnnouncement> queue;

        // Raid session state, set from command handlers and read by the queue loop.
        private readonly object raidLock = new object();
        private ulong? raidGuildID;
        private DateTime? raidStartedDT;

        private SocketVoiceChannel? connectedChannel;
        private IAudioClient? audioClient;
        private AudioOutStream? audioStream;
        private DaveLogSinkDelegate? daveLogSink;

        public bool IsAvailable { get; private set; }
        public string UnavailableReason { get; private set; } = "Voice is still starting up.";

        public VoiceCommunicationService(ILogger<VoiceCommunicationService> logger,
            DiscordSocketClient client,
            VoiceSettings settings,
            VoiceService voiceService,
            AuctionStore auctionStore,
            BidbotDataStore dataStore)
        {
            this.logger = logger;
            this.client = client;
            this.settings = settings;
            this.voiceService = voiceService;
            this.auctionStore = auctionStore;
            this.dataStore = dataStore;

            // If a flood of auctions start at once, drop the oldest announcements rather than grow forever.
            this.queue = Channel.CreateBounded<VoiceAnnouncement>(new BoundedChannelOptions(settings.MaxQueueSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        public (bool WasSuccessful, string Message) AnnounceAuction(Auction auction)
        {
            try
            {
                if (!settings.IsEnabled)
                    return (false, "Voice announcements are turned off.");

                var announcement = new VoiceAnnouncement()
                {
                    GuildID = auction.GuildID,
                    AuctionID = auction.AuctionID,
                    CacheName = auction.Item.Name,
                    Text = voiceService.CreateAnnouncementText(auction.Item.Name)
                };

                return Enqueue(announcement);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error queueing voice announcement.");
                return (false, ex.Message);
            }
        }

        public (bool WasSuccessful, string Message) AnnounceTest(ulong guildID, string? text)
        {
            var spokenText = string.IsNullOrWhiteSpace(text) ? VOICE_TEST_TEXT : text.Trim();
            var announcement = new VoiceAnnouncement()
            {
                GuildID = guildID,
                CacheName = "voicetest",
                Text = spokenText
            };

            return Enqueue(announcement);
        }

        /// <summary>
        /// Joins the voice channel and stays for the raid (started with /setsheet).
        /// </summary>
        public (bool WasSuccessful, string Message) StartRaidPresence(ulong guildID, DateTime? raidStartDT = null)
        {
            try
            {
                if (!settings.IsEnabled || !settings.StayConnectedDuringRaid)
                    return (false, "Staying in voice during raids is turned off.");

                lock (raidLock)
                {
                    raidGuildID = guildID;
                    raidStartedDT = raidStartDT ?? DateTime.UtcNow;
                }

                return Enqueue(new VoiceAnnouncement() { Command = VoiceCommand.JoinChannel, GuildID = guildID });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error starting raid voice session.");
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Leaves the voice channel at the end of the raid (/endraid).
        /// </summary>
        public (bool WasSuccessful, string Message) EndRaidPresence()
        {
            try
            {
                bool wasInRaid;
                lock (raidLock)
                {
                    wasInRaid = raidGuildID != null;
                    raidGuildID = null;
                    raidStartedDT = null;
                }

                if (!wasInRaid)
                    return (false, "Not in a raid voice session.");

                return Enqueue(new VoiceAnnouncement() { Command = VoiceCommand.LeaveChannel });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error ending raid voice session.");
                return (false, ex.Message);
            }
        }

        public (bool WasSuccessful, string Message) Enqueue(VoiceAnnouncement announcement)
        {
            try
            {
                if (!IsAvailable)
                    return (false, UnavailableReason);

                if (!queue.Writer.TryWrite(announcement))
                    return (false, "The voice queue is not accepting announcements.");

                return (true, "Announcement queued.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error queueing voice announcement.");
                return (false, ex.Message);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // An exception escaping ExecuteAsync would stop the whole host, so everything is caught here.
            try
            {
                var check = checkAvailability();
                if (!check.WasSuccessful)
                {
                    UnavailableReason = check.Message;
                    logger.LogWarning("Voice announcements are disabled: {Reason}", check.Message);
                    return;
                }

                if (settings.DebugLogging)
                    enableLibDaveLogging();

                IsAvailable = true;
                UnavailableReason = string.Empty;
                logger.LogInformation("Voice announcements enabled for voice channel '{Channel}'.", settings.VoiceChannelName);

                _ = restoreRaidPresence(stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!await queue.Reader.WaitToReadAsync(stoppingToken))
                            break;

                        await playQueue(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error playing voice announcements.");
                    }
                    finally
                    {
                        await leaveVoice();
                    }
                }
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                UnavailableReason = $"Voice stopped after an error: {ex.Message}";
                logger.LogError(ex, "Voice announcements stopped after an unexpected error.");
            }
        }

        private async Task playQueue(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (queue.Reader.TryRead(out var item))
                {
                    await handleQueueItem(item, stoppingToken);
                    continue;
                }

                // Queue is empty. During a raid stay in the channel (rejoining if Discord dropped us),
                // otherwise hang around briefly in case another auction starts, then leave.
                var stayInGuildID = getRaidGuildToStayIn();
                if (stayInGuildID != null && !isConnected())
                    await ensureConnected(stayInGuildID.Value, stoppingToken);

                var waitTime = stayInGuildID != null ? RAID_CHECK_INTERVAL : TimeSpan.FromSeconds(settings.IdleLeaveSeconds);
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idleCts.CancelAfter(waitTime);

                try
                {
                    if (!await queue.Reader.WaitToReadAsync(idleCts.Token))
                        return;
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Not in a raid, time to leave. In a raid, loop to re-check MaxRaidHours and the connection.
                    if (stayInGuildID == null)
                        return;
                }
            }
        }

        private async Task playAnnouncement(VoiceAnnouncement announcement, CancellationToken stoppingToken)
        {
            try
            {
                if (isStale(announcement))
                {
                    logger.LogInformation("Skipping voice announcement for {Item}, the auction is already over.", announcement.CacheName);
                    return;
                }

                var file = await voiceService.GenerateFile(announcement.CacheName, announcement.Text);
                if (!file.WasSuccessful)
                {
                    logger.LogWarning("Unable to create voice file for {Item}: {Message}", announcement.CacheName, file.Message);
                    return;
                }

                var pcm = await WaveFileReader.ReadAsDiscordPcm(file.FilePath, settings.Volume);
                if (!pcm.WasSuccessful)
                {
                    logger.LogWarning("Unable to read voice file {FilePath}: {Message}", file.FilePath, pcm.Message);
                    return;
                }

                var stream = await ensureConnected(announcement.GuildID, stoppingToken);
                if (stream == null)
                    return;

                await stream.WriteAsync(pcm.Pcm, stoppingToken);
                await stream.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error playing voice announcement for {Item}.", announcement.CacheName);

                // Start fresh on the next announcement in case the connection is in a bad state.
                await leaveVoice();
            }
        }

        private bool isStale(VoiceAnnouncement announcement)
        {
            return announcement.AuctionID.HasValue && auctionStore.TryGet(announcement.AuctionID.Value) == null;
        }

        private bool isConnected()
        {
            return audioClient != null
                && audioStream != null
                && audioClient.ConnectionState == ConnectionState.Connected;
        }

        private async Task handleQueueItem(VoiceAnnouncement item, CancellationToken stoppingToken)
        {
            switch (item.Command)
            {
                case VoiceCommand.JoinChannel:
                    // Only join if the raid is still on, it may have ended while this was queued.
                    if (getRaidGuildToStayIn() == item.GuildID)
                        await ensureConnected(item.GuildID, stoppingToken);
                    break;
                case VoiceCommand.LeaveChannel:
                    if (getRaidGuildToStayIn() == null)
                        await leaveVoice();
                    break;
                default:
                    await playAnnouncement(item, stoppingToken);
                    break;
            }
        }

        /// <summary>
        /// Returns the guild the bot should stay connected to for a raid, or null when it should
        /// join and leave per announcement. Ends the raid session once it passes MaxRaidHours.
        /// </summary>
        private ulong? getRaidGuildToStayIn()
        {
            lock (raidLock)
            {
                if (raidGuildID == null || raidStartedDT == null)
                    return null;

                if (DateTime.UtcNow - raidStartedDT.Value > TimeSpan.FromHours(settings.MaxRaidHours))
                {
                    logger.LogInformation("Raid has been running longer than Voice:MaxRaidHours ({Hours}), leaving the voice channel.", settings.MaxRaidHours);
                    raidGuildID = null;
                    raidStartedDT = null;
                    return null;
                }

                return raidGuildID;
            }
        }

        private async Task restoreRaidPresence(CancellationToken stoppingToken)
        {
            try
            {
                if (!settings.StayConnectedDuringRaid)
                    return;

                var raid = dataStore.ActiveRaid;
                if (raid == null || DateTime.UtcNow - raid.StartDT > TimeSpan.FromHours(settings.MaxRaidHours))
                    return;

                // Give Discord time to finish connecting and load the guild after a restart.
                for (int i = 0; i < 60 && client.GetGuild(raid.GuildID) == null; i++)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

                logger.LogInformation("A raid was still active when BidBot started, rejoining the voice channel.");
                StartRaidPresence(raid.GuildID, raid.StartDT);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to rejoin voice for the active raid.");
            }
        }

        private async Task<AudioOutStream?> ensureConnected(ulong guildID, CancellationToken stoppingToken)
        {
            if (isConnected() && connectedChannel?.Guild.Id == guildID)
                return audioStream;

            await leaveVoice();

            var guild = client.GetGuild(guildID);
            var channel = guild?.VoiceChannels.FirstOrDefault(c => c.Name.Equals(settings.VoiceChannelName, StringComparison.OrdinalIgnoreCase));
            if (channel == null)
            {
                logger.LogWarning("Voice channel '{Channel}' not found in guild {GuildID}.", settings.VoiceChannelName, guildID);
                return null;
            }

            var permissionCheck = checkChannelPermissions(guild!, channel);
            if (!permissionCheck.WasSuccessful)
            {
                logger.LogWarning("Can't join voice channel '{Channel}': {Reason}", channel.Name, permissionCheck.Message);
                return null;
            }

            logger.LogInformation("Joining voice channel '{Channel}'...", channel.Name);

            // Discord.Net has its own 15 second timeout, ours is only a safety net in case that never returns.
            var connectTask = channel.ConnectAsync(selfDeaf: true);
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds), stoppingToken));
            if (completed != connectTask)
            {
                logger.LogWarning("Timed out joining voice channel '{Channel}'. {Hint}", channel.Name, CONNECT_FAILURE_HINT);

                // Observe the abandoned task so a late failure doesn't surface as an unobserved exception.
                _ = connectTask.ContinueWith(t => logger.LogWarning(t.Exception, "Late voice connection failure."), TaskContinuationOptions.OnlyOnFaulted);
                await disconnectChannel(channel);
                return null;
            }

            try
            {
                audioClient = await connectTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Discord did not finish connecting to voice channel '{Channel}'. {Hint}", channel.Name, CONNECT_FAILURE_HINT);
                await disconnectChannel(channel);
                return null;
            }

            connectedChannel = channel;
            audioClient.Disconnected += AudioClient_Disconnected;
            audioStream = audioClient.CreatePCMStream(AudioApplication.Voice);

            logger.LogInformation("Joined voice channel '{Channel}'.", channel.Name);
            return audioStream;
        }

        private static (bool WasSuccessful, string Message) checkChannelPermissions(SocketGuild guild, SocketVoiceChannel channel)
        {
            var botUser = guild.CurrentUser;
            if (botUser == null)
                return (false, "The bot's guild member information isn't loaded yet.");

            var permissions = botUser.GetPermissions(channel);
            var missing = new List<string>();
            if (!permissions.ViewChannel)
                missing.Add("View Channel");
            if (!permissions.Connect)
                missing.Add("Connect");
            if (!permissions.Speak)
                missing.Add("Speak");

            if (missing.Any())
                return (false, $"The bot is missing these permissions on the channel: {string.Join(", ", missing)}.");

            if (channel.UserLimit.HasValue && channel.UserLimit > 0
                && channel.ConnectedUsers.Count >= channel.UserLimit && !permissions.MoveMembers)
                return (false, $"The channel is full ({channel.UserLimit} user limit).");

            return (true, string.Empty);
        }

        private void enableLibDaveLogging()
        {
            try
            {
                // Keep a reference to the delegate, native code calls it and it must not be garbage collected.
                daveLogSink = (severity, file, line, message) =>
                {
                    // Verbose lines are huge hex dumps of keys and welcome messages, skip them.
                    if (severity == Discord.LibDave.Binding.LoggingSeverity.Verbose)
                        return;

                    logger.LogInformation("libdave {Severity}: {Message} ({File}:{Line})", severity, message, Path.GetFileName(file), line);
                };
                Dave.SetLogSink(daveLogSink);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to enable libdave logging.");
            }
        }

        private Task AudioClient_Disconnected(Exception ex)
        {
            if (ex != null)
                logger.LogWarning(ex, "Voice connection closed.");

            return Task.CompletedTask;
        }

        private async Task leaveVoice()
        {
            try
            {
                if (audioStream != null)
                {
                    await audioStream.FlushAsync();
                    await audioStream.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error closing the voice stream.");
            }
            finally
            {
                audioStream = null;
            }

            if (audioClient != null)
                audioClient.Disconnected -= AudioClient_Disconnected;
            audioClient = null;

            if (connectedChannel != null)
            {
                await disconnectChannel(connectedChannel);
                logger.LogInformation("Left voice channel '{Channel}'.", connectedChannel.Name);
            }
            connectedChannel = null;
        }

        private async Task disconnectChannel(SocketVoiceChannel channel)
        {
            try
            {
                await channel.DisconnectAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error leaving voice channel '{Channel}'.", channel.Name);
            }
        }

        private (bool WasSuccessful, string Message) checkAvailability()
        {
            if (!settings.IsEnabled)
                return (false, "Voice:IsEnabled is false in appsettings.");

            if (!settings.HasSpeechCredentials)
                return (false, "Voice:SpeechKey and Voice:SpeechRegion are not set.");

            if (!settings.HasVoiceChannel)
                return (false, "Voice:VoiceChannelName is not set.");

            var missing = new List<string>();
            foreach (var library in REQUIRED_NATIVE_LIBRARIES)
            {
                if (!NativeLibrary.TryLoad(library, typeof(VoiceCommunicationService).Assembly, null, out _))
                    missing.Add(library);
            }

            if (missing.Any())
                return (false, $"Missing native voice libraries next to BidBot: {string.Join(", ", missing)}. See the Voice Announcements section of the README.");

            return (true, string.Empty);
        }
    }
}
