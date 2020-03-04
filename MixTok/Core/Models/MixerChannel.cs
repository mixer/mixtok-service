using Newtonsoft.Json;

namespace MixTok.Core.Models
{
    public class MixerChannel
    {
        public int Id;
        public int ViewersCurrent;
        public bool Online;
        public bool Partnered;
        public int UserId;
        public string ChannelLogo;
        public bool VodsEnabled;
        public MixerUser User;

        [JsonProperty("languageId")]
        public string Language;

        [JsonProperty("token")]
        public string Name;

        public void UpdateFromNewer(MixerChannel fresh)
        {
            ViewersCurrent = fresh.ViewersCurrent;
            Online = fresh.Online;
            Partnered = fresh.Partnered;
            ChannelLogo = fresh.ChannelLogo;
            VodsEnabled = fresh.VodsEnabled;
            Language = fresh.Language;
            Name = fresh.Name;
        }
    }
}
