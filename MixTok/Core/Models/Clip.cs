using System;

namespace MixTok.Core.Models
{
    public class Clip
    {
        public double Rank;
        public MixerChannel Channel;

        public int Views;
        public int TypeId;
        public string Title;
        public string ClipUrl;
        public string ContentId;
        public DateTime Created;
        public string ShareableUrl;
    }
}
