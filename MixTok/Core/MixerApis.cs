using MixTok.Core.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace MixTok.Core
{
    

    public class MixerApis
    {
        private static HttpClient _client = new HttpClient();
        private static ConcurrentDictionary<int, string> _gameNameCache = new ConcurrentDictionary<int, string>();

        public static async Task<List<MixerChannel>> GetOnlineChannels(int viwersInclusiveLimit = 5, string languageFilter = null)
        {
            var totalChannels = new List<MixerChannel>();
            int i = 0;
            while (i < 1000)
            {
                try
                {
                    var response = await MakeMixerHttpRequest($"api/v1/channels?limit=100&page={i}&order=online:desc,viewersCurrent:desc&fields=token,id,viewersCurrent,online,userId,user,languageId,vodsEnabled,partnered");
                    var channels = JsonConvert.DeserializeObject<List<MixerChannel>>(response);
                    totalChannels.AddRange(channels);

                    // Check if we hit the end.
                    if (channels.Count == 0)
                    {
                        break;
                    }

                    // Check if we are on channels that are under our viewer limit
                    if (channels[0].ViewersCurrent < viwersInclusiveLimit)
                    {
                        break;
                    }

                    // Check if we hit the end of online channels.
                    if (!channels[0].Online)
                    {
                        break;
                    }

                    // Sleep a little so we don't hit the API too hard.
                    await Task.Delay(10);
                }
                catch (Exception e)
                {
                    Logger.Error($"Failed to query channel API.", e);
                    break;
                }
                i++;
            }

            var final = new List<MixerChannel>();
            foreach (var channel in totalChannels)
            {
                if (string.IsNullOrWhiteSpace(channel.Language))
                {
                    channel.Language = "unknown";
                }
                if (!string.IsNullOrWhiteSpace(languageFilter) && !channel.Language.Equals(languageFilter))
                {
                    continue;
                }
                if (channel.Online)
                {
                    channel.ChannelLogo = $"https://mixer.com/api/v1/users/{channel.UserId}/avatar";
                    final.Add(channel);
                }
            }
            return final;
        }

        public static async Task<List<MixerClip>> GetClips(int channelId)
        {
            var response = await MakeMixerHttpRequest($"api/v1/clips/channels/{channelId}");
            var list = JsonConvert.DeserializeObject<List<MixerClip>>(response);

            // Add some meta data.
            foreach (var mixerClip in list)
            {
                // Add the game title
                mixerClip.GameTitle = await GetGameName(mixerClip.TypeId);

                // Pull out the HLS url.
                foreach (var clipContent in mixerClip.ContentLocators)
                {
                    if (clipContent.LocatorType.Equals("HlsStreaming"))
                    {
                        mixerClip.ClipUrl = clipContent.Uri;
                        break;
                    }
                }

                // Create the deep link url.
                mixerClip.ShareableUrl = $"https://mixer.com/{channelId}?clip={mixerClip.ShareableId}";

                // Pull out the hypezone channel id if there is one.
                mixerClip.HypeZoneChannelId = 0;
                if (mixerClip.Tags != null && mixerClip.Tags.Count > 0)
                {
                    foreach (string s in mixerClip.Tags)
                    {
                        if (s.StartsWith("HZ-"))
                        {
                            int hypeChanId = 0;
                            string end = s.Substring(3);
                            if (int.TryParse(end, out hypeChanId))
                            {
                                mixerClip.HypeZoneChannelId = hypeChanId;
                            }
                        }
                    }
                }
            }
            return list;
        }

        public static async Task<string> GetGameName(int typeId)
        {
            // Check the cache
            if (_gameNameCache.TryGetValue(typeId, out var value))
            {
                return value;
            }

            try
            {
                var response = await MakeMixerHttpRequest($"api/v1/types/{typeId}");
                var name = JsonConvert.DeserializeObject<MixerType>(response).Name;
                
                _gameNameCache.TryAdd(typeId, name);
                return name;
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to get game name for {typeId}: {e.Message}");
            }
            return "Unknown";
        }

        public static async Task<int> GetChannelId(string channelName)
        {
            try
            {
                string response = await MakeMixerHttpRequest($"api/v1/channels/{channelName}");
                return JsonConvert.DeserializeObject<MixerChannel>(response).Id;
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to get channel id for {channelName}: {e.Message}");
            }
            return 0;
        }

        public async static Task<string> MakeMixerHttpRequest(string url)
        {
            int rateLimitBackoff = 1;
            int i = 0;
            while (i < 1000)
            {
                var request = new HttpRequestMessage();
                request.RequestUri = new Uri($"https://mixer.com/{url}");

                var response = await _client.SendAsync(request);
                if (response.StatusCode == (HttpStatusCode)429)
                {
                    // If we get rate limited wait for a while.
                    int backoffMs = 500 * (int)Math.Pow(rateLimitBackoff, 2);
                    Logger.Info($"[Request Throttled] URL backing off for {backoffMs}ms, URL:{url}");
                    rateLimitBackoff++;
                    await Task.Delay(backoffMs);

                    // And try again.
                    continue;
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception($"Mixer backend returned status code {response.StatusCode}");
                }
                return await response.Content.ReadAsStringAsync();
            }
            return string.Empty;
        }
    }
}
