namespace MixTok.Core.Models
{
    public class MixerUser
    {
        public MixerSocial Social;
        public bool Verified;
        public string Bio;

        public void UpdateFromNewer(MixerUser fresh)
        {
            Verified = fresh.Verified;
            Bio = fresh.Bio;
        }
    }
}
