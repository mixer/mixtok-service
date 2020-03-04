using System;
using System.Collections.Generic;

namespace MixTok.Core.Models
{
    public class MixerClip
    {
        public string Title;
        public double MixTokRank;
        public int ViewCount;
        public int TypeId;
        public int HypeZoneChannelId;
        public string ClipUrl;
        public string ShareableUrl;
        public string GameTitle;
        public string ContentId;
        public string ShareableId;
        public int ContentMaturity;
        public int DurationInSeconds;
        public DateTime UploadDate;
        public DateTime ExpirationDate;
        public List<ClipContent> ContentLocators;
        public MixerChannel Channel;
        public List<string> Tags;

        public void UpdateFromNewer(MixerClip fresh)
        {
            ViewCount = fresh.ViewCount;
            Channel.UpdateFromNewer(fresh.Channel);
        }
    }
}
