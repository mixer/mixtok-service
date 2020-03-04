using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MixTok.Core.Models;

namespace MixTok.Core
{
    public class ClipCrawler
    {
        private readonly int _minViewerCount = 2;

        private IClipMineAdder _adder;
        private Thread _updater;


        public ClipCrawler(IClipMineAdder adder)
        {
            // For local testing, set the min view count to be higher.
            var test = Environment.GetEnvironmentVariables();
            string var = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if(!String.IsNullOrWhiteSpace(var) && var == "Development")
            {
                _minViewerCount = 500;
            }

            _adder = adder;
            _updater = new Thread(UpdateThread);
            _updater.Priority = ThreadPriority.BelowNormal;
            _updater.Start();
        }

        private async void UpdateThread()
        {
            while(true)
            {
                try
                {
                    // Update
                    var start = DateTimeOffset.Now;
                    var clips = await GetTockClips();

                    _adder.AddToClipMine(clips, DateTimeOffset.Now - start, false);
                }
                catch(Exception e)
                {
                    Program.s_ClipMine.SetStatus($"<strong>Failed to update clips!</strong> "+e.Message + "; stack: "+e.StackTrace, new TimeSpan(0, 0, 30));
                    Logger.Error($"Failed to update!", e);
                }

                // After we successfully get clips,
                // update every 5 minutes
                var nextUpdate = DateTimeOffset.Now.AddMinutes(5);
                while(nextUpdate > DateTimeOffset.Now)
                {
                    // Don't set the text if we are showing an error.
                    Program.s_ClipMine.SetStatus($"Next update in {Util.FormatTime(nextUpdate - DateTimeOffset.Now)}");
                    Thread.Sleep(500);
                }
            }
        }
     
        private async Task<List<MixerClip>> GetTockClips()
        {
            // Get the online channels
            var start = DateTimeOffset.Now;

            Program.s_ClipMine.SetStatus($"Finding online channels...");

            // We must limit how many channels we pull, so we will only get channels with at least 2 viewers.
            var channels = await MixerApis.GetOnlineChannels(_minViewerCount, null);
            Logger.Info($"Found {channels.Count} online channels in {Util.FormatTime(DateTimeOffset.Now - start)}");
            Program.s_ClipMine.SetStatus($"Found {Util.FormatInt(channels.Count)} online channels in {Util.FormatTime(DateTimeOffset.Now - start)}", new TimeSpan(0, 0, 10));

            // Get the clips for the channels
            var clips = new List<MixerClip>();
            start = DateTimeOffset.Now;
            int count = 0;
            foreach (var channel in channels)
            {
                try
                {
                    // Get the clips for this channel.
                    var channelClips = await MixerApis.GetClips(channel.Id);

                    // For each clip, attach the most recent channel object.
                    foreach(MixerClip c in channelClips)
                    {
                        c.Channel = channel;
                    }

                    // Add the clips to our output list.
                    clips.AddRange(channelClips);                   
                }
                catch (Exception e)
                {
                    Logger.Error("Failed to get clips for channel " + channel.Name, e);
                }
                
                count++;
                if(count % 5 == 0)
                {
                    //Logger.Info($"Got {count}/{channels.Count} channel clips...");
                    Program.s_ClipMine.SetStatus($"Getting clip data [{Util.FormatInt(count)}/{Util.FormatInt(channels.Count)}]...");                    
                }
            }

            Logger.Info($"Found {count} clips in {(DateTimeOffset.Now - start)}");
            Program.s_ClipMine.SetStatus($"Found {Util.FormatInt(count)} clips in {Util.FormatTime(DateTimeOffset.Now - start)}", new TimeSpan(0, 0, 10));

            return clips;
        }
    }
}
