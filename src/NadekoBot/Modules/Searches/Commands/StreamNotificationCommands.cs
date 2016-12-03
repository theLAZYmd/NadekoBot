﻿using Discord.Commands;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using NadekoBot.Services;
using System.Threading;
using System.Collections.Generic;
using NadekoBot.Services.Database.Models;
using System.Net.Http;
using Discord.WebSocket;
using NadekoBot.Attributes;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NLog;
using NadekoBot.Services.Database;
using NadekoBot.Extensions;

namespace NadekoBot.Modules.Searches
{
    public partial class Searches
    {
        public class StreamStatus
        {
            public bool IsLive { get; set; }
            public string ApiLink { get; set; }
            public string Views { get; set; }
        }

        public class HitboxResponse {
            public bool Success { get; set; } = true;
            [JsonProperty("media_is_live")]
            public string MediaIsLive { get; set; }
            public bool IsLive  => MediaIsLive == "1";
            [JsonProperty("media_views")]
            public string Views { get; set; }
        }

        public class TwitchResponse
        {
            public string Error { get; set; } = null;
            public bool IsLive => Stream != null;
            public StreamInfo Stream { get; set; }

            public class StreamInfo
            {
                public int Viewers { get; set; }
            }
        }

        public class BeamResponse
        {
            public string Error { get; set; } = null;

            [JsonProperty("online")]
            public bool IsLive { get; set; }
            public int ViewersCurrent { get; set; }
        }

        public class StreamNotFoundException : Exception
        {
            public StreamNotFoundException(string message) : base("Stream '" + message + "' not found.")
            {
            }
        }

        [Group]
        public class StreamNotificationCommands
        {
            private Timer checkTimer { get; }
            private ConcurrentDictionary<string, StreamStatus> oldCachedStatuses = new ConcurrentDictionary<string, StreamStatus>();
            private ConcurrentDictionary<string, StreamStatus> cachedStatuses = new ConcurrentDictionary<string, StreamStatus>();
            private Logger _log { get; }

            private bool FirstPass { get; set; } = true;

            public StreamNotificationCommands()
            {

                _log = NLog.LogManager.GetCurrentClassLogger();
                checkTimer = new Timer(async (state) =>
                {
                    oldCachedStatuses = new ConcurrentDictionary<string, StreamStatus>(cachedStatuses);
                    cachedStatuses.Clear();
                    IEnumerable<FollowedStream> streams;
                    using (var uow = DbHandler.UnitOfWork())
                    {
                        streams = uow.GuildConfigs.GetAllFollowedStreams();
                    }

                    await Task.WhenAll(streams.Select(async fs =>
                                    {
                                        try
                                        {
                                            var newStatus = await GetStreamStatus(fs).ConfigureAwait(false);
                                            if (FirstPass)
                                            {
                                                return;
                                            }

                                            StreamStatus oldStatus;
                                            if (oldCachedStatuses.TryGetValue(newStatus.ApiLink, out oldStatus) &&
                                                oldStatus.IsLive != newStatus.IsLive)
                                            {
                                                var msg = $"`{fs.Username}`'s stream is now " +
                                                    $"**{(newStatus.IsLive ? "ONLINE" : "OFFLINE")}** with " +
                                                    $"**{newStatus.Views}** viewers.";

                                                var server = NadekoBot.Client.GetGuild(fs.GuildId);
                                                var channel = server?.GetTextChannel(fs.ChannelId);
                                                if (channel == null)
                                                    return;
                                                if (newStatus.IsLive)
                                                    msg += "\n" + fs.GetLink();
                                                try { await channel.SendMessageAsync(msg).ConfigureAwait(false); } catch { }
                                            }
                                        }
                                        catch (Exception ex)
                                        {

                                        }
                                    }));

                    FirstPass = false;
                }, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            }

            private async Task<StreamStatus> GetStreamStatus(FollowedStream stream, bool checkCache = true)
            {
                string response;
                StreamStatus result;
                switch (stream.Type)
                {
                    case FollowedStream.FollowedStreamType.Hitbox:
                        var hitboxUrl = $"https://api.hitbox.tv/media/status/{stream.Username}";
                        if (checkCache && cachedStatuses.TryGetValue(hitboxUrl, out result))
                            return result;
                        using (var http = new HttpClient())
                        {
                            response = await http.GetStringAsync(hitboxUrl).ConfigureAwait(false);
                        }
                        var hbData = JsonConvert.DeserializeObject<HitboxResponse>(response);
                        if (!hbData.Success)
                            throw new StreamNotFoundException($"{stream.Username} [{stream.Type}]");
                        result = new StreamStatus()
                        {
                            IsLive = hbData.IsLive,
                            ApiLink = hitboxUrl,
                            Views = hbData.Views
                        };
                        cachedStatuses.AddOrUpdate(hitboxUrl, result, (key, old) => result);
                        return result;
                    case FollowedStream.FollowedStreamType.Twitch:
                        var twitchUrl = $"https://api.twitch.tv/kraken/streams/{Uri.EscapeUriString(stream.Username)}?client_id=67w6z9i09xv2uoojdm9l0wsyph4hxo6";
                        if (checkCache && cachedStatuses.TryGetValue(twitchUrl, out result))
                            return result;
                        using (var http = new HttpClient())
                        {
                            response = await http.GetStringAsync(twitchUrl).ConfigureAwait(false);
                        }
                        var twData = JsonConvert.DeserializeObject<TwitchResponse>(response);
                        if (twData.Error != null)
                        {
                            throw new StreamNotFoundException($"{stream.Username} [{stream.Type}]");
                        }
                        result = new StreamStatus()
                        {
                            IsLive = twData.IsLive,
                            ApiLink = twitchUrl,
                            Views = twData.Stream?.Viewers.ToString() ?? "0"
                        };
                        cachedStatuses.AddOrUpdate(twitchUrl, result, (key, old) => result);
                        return result;
                    case FollowedStream.FollowedStreamType.Beam:
                        var beamUrl = $"https://beam.pro/api/v1/channels/{stream.Username}";
                        if (checkCache && cachedStatuses.TryGetValue(beamUrl, out result))
                            return result;
                        using (var http = new HttpClient())
                        {
                            response = await http.GetStringAsync(beamUrl).ConfigureAwait(false);
                        }

                        var bmData = JsonConvert.DeserializeObject<BeamResponse>(response);
                        if (bmData.Error != null)
                            throw new StreamNotFoundException($"{stream.Username} [{stream.Type}]");
                        result = new StreamStatus()
                        {
                            IsLive = bmData.IsLive,
                            ApiLink = beamUrl,
                            Views = bmData.ViewersCurrent.ToString()
                        };
                        cachedStatuses.AddOrUpdate(beamUrl, result, (key, old) => result);
                        return result;
                    default:
                        break;
                }
                return null;
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequirePermission(GuildPermission.ManageMessages)]
            public async Task Hitbox(IUserMessage msg, [Remainder] string username) =>
                await TrackStream((ITextChannel)msg.Channel, username, FollowedStream.FollowedStreamType.Hitbox)
                    .ConfigureAwait(false);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequirePermission(GuildPermission.ManageMessages)]
            public async Task Twitch(IUserMessage msg, [Remainder] string username) =>
                await TrackStream((ITextChannel)msg.Channel, username, FollowedStream.FollowedStreamType.Twitch)
                    .ConfigureAwait(false);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequirePermission(GuildPermission.ManageMessages)]
            public async Task Beam(IUserMessage msg, [Remainder] string username) =>
                await TrackStream((ITextChannel)msg.Channel, username, FollowedStream.FollowedStreamType.Beam)
                    .ConfigureAwait(false);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task ListStreams(IUserMessage imsg)
            {
                var channel = (ITextChannel)imsg.Channel;

                IEnumerable<FollowedStream> streams;
                using (var uow = DbHandler.UnitOfWork())
                {
                    streams = uow.GuildConfigs
                                 .For(channel.Guild.Id, 
                                      set => set.Include(gc => gc.FollowedStreams))
                                 .FollowedStreams;
                }

                if (!streams.Any())
                {
                    await channel.SendMessageAsync("You are not following any streams on this server.").ConfigureAwait(false);
                    return;
                }

                var text = string.Join("\n", streams.Select(snc =>
                {
                    return $"`{snc.Username}`'s stream on **{channel.Guild.GetTextChannel(snc.ChannelId)?.Name}** channel. 【`{snc.Type.ToString()}`】";
                }));

                await channel.SendMessageAsync($"You are following **{streams.Count()}** streams on this server.\n\n" + text).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequirePermission(GuildPermission.ManageMessages)]
            public async Task RemoveStream(IUserMessage msg, FollowedStream.FollowedStreamType type, [Remainder] string username)
            {
                var channel = (ITextChannel)msg.Channel;

                username = username.ToLowerInvariant().Trim();

                var fs = new FollowedStream()
                {
                    ChannelId = channel.Id,
                    Username = username,
                    Type = type
                };

                bool removed;
                using (var uow = DbHandler.UnitOfWork())
                {
                    var config = uow.GuildConfigs.For(channel.Guild.Id, set => set.Include(gc => gc.FollowedStreams));
                    removed = config.FollowedStreams.Remove(fs);
                    if (removed)
                        await uow.CompleteAsync().ConfigureAwait(false);
                }
                if (!removed)
                {
                    await channel.SendMessageAsync(":anger: No such stream.").ConfigureAwait(false);
                    return;
                }
                await channel.SendMessageAsync($":ok: Removed `{username}`'s stream ({type}) from notifications.").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task CheckStream(IUserMessage imsg, FollowedStream.FollowedStreamType platform, [Remainder] string username)
            {
                var channel = (ITextChannel)imsg.Channel;

                var stream = username?.Trim();
                if (string.IsNullOrWhiteSpace(stream))
                    return;
                try
                {
                    var streamStatus = (await GetStreamStatus(new FollowedStream
                    {
                        Username = stream,
                        Type = platform,
                    }));
                    if (streamStatus.IsLive)
                    {
                        await channel.SendMessageAsync($"`Streamer {username} is online with {streamStatus.Views} viewers.`");
                    }
                    else
                    {
                        await channel.SendMessageAsync($"`Streamer {username} is offline.`");
                    }
                }
                catch
                {
                    await channel.SendMessageAsync("No channel found.");
                }
            }

            private async Task TrackStream(ITextChannel channel, string username, FollowedStream.FollowedStreamType type)
            {
                username = username.ToLowerInvariant().Trim();
                var stream = new FollowedStream
                {
                    GuildId = channel.Guild.Id,
                    ChannelId = channel.Id,
                    Username = username,
                    Type = type,
                };

                StreamStatus data;
                try
                {
                    data = await GetStreamStatus(stream).ConfigureAwait(false);
                }
                catch
                {
                    await channel.SendMessageAsync(":anger: Stream probably doesn't exist.").ConfigureAwait(false);
                    return;
                }

                using (var uow = DbHandler.UnitOfWork())
                {
                    uow.GuildConfigs.For(channel.Guild.Id, set => set.Include(gc => gc.FollowedStreams))
                                    .FollowedStreams
                                    .Add(stream);
                    await uow.CompleteAsync().ConfigureAwait(false);
                }
                var msg = $"Stream is currently **{(data.IsLive ? "ONLINE" : "OFFLINE")}** with **{data.Views}** viewers";
                if (data.IsLive)
                    msg += stream.GetLink();
                msg = $":ok: I will notify this channel when status changes.\n{msg}";
                await channel.SendMessageAsync(msg).ConfigureAwait(false);
            }
        }
    }

    public static class FollowedStreamExtensions
    {
        public static string GetLink(this FollowedStream fs)
        {
            //todo C#7
            if (fs.Type == FollowedStream.FollowedStreamType.Hitbox)
                return $"\n`Here is the Link:`【 http://www.hitbox.tv/{fs.Username}/ 】";
            else if (fs.Type == FollowedStream.FollowedStreamType.Twitch)
                return $"\n`Here is the Link:`【 http://www.twitch.tv/{fs.Username}/ 】";
            else if (fs.Type == FollowedStream.FollowedStreamType.Beam)
                return $"\n`Here is the Link:`【 https://beam.pro/{fs.Username}/ 】";
            return "???";
        }
    }
}