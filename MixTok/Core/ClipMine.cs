using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MixTok.Core.Models;

namespace MixTok.Core
{
    public interface IClipMineAdder
    {
        void AddToClipMine(List<MixerClip> newClips, TimeSpan updateDuration, bool isRestore);
    }

    public enum ClipMineSortTypes
    {
        ViewCount = 0,
        MixTokRank = 1,
        MostRecent = 2
    }

    public class ClipMine : IClipMineAdder
    {
        // This value is used for the save / restore.
        // If anything in any of the objects change, this should be updated.
        const int c_databaseVersion = 1;

        private Historian _historian;
        private ClipCrawler _crawler;
        private ReaderWriterLockSlim _clipMineLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly ConcurrentDictionary<string, MixerClip> _clipMine = new ConcurrentDictionary<string, MixerClip>();
        private readonly object _viewCountSortedLock = new object(); // We use these lock objects since we swap the list obejcts.
        private readonly object _mixTockSortedLock = new object();
        private readonly object _mostRecentSortedLock = new object();
        private LinkedList<MixerClip> _viewCountSortedList = new LinkedList<MixerClip>();
        private LinkedList<MixerClip> _mixTockSortedList = new LinkedList<MixerClip>();
        private LinkedList<MixerClip> _mostRecentList = new LinkedList<MixerClip>();
        private DateTimeOffset _lastUpdateTime = DateTimeOffset.Now;
        private TimeSpan _lastUpdateDuration = new TimeSpan(0);
        private DateTimeOffset _lastDatabaseBackup = DateTimeOffset.MinValue;
        private string _status;
        private TimeSpan _statusDuration = new TimeSpan(0);
        private DateTimeOffset _statusDurationSet = DateTimeOffset.MinValue;

        public ClipMine()
        {
            _historian = new Historian();
        }

        public void Start()
        {
            // Start a worker
            var worker = new Thread(() =>
            {
                // Ask the historian to try to restore our in
                // memory database from a previous database.
                _historian.AttemptToRestore(this, c_databaseVersion);

                // Now start the normal miner.
                _crawler = new ClipCrawler(this);
            });
            worker.Start();
        }

        public void AddToClipMine(List<MixerClip> newClips, TimeSpan updateDuration, bool isRestore)
        {
            var start = DateTimeOffset.Now;

            SetStatus($"Indexing {newClips.Count} new clips...");

            {
                // Lock the dictionary for writing so we make sure no one reads or writes while we are updating.
                _clipMineLock.EnterWriteLock();

                // Set all channels to offline and remove old clips.
                OfflineAndCleanUpClipMine();

                // Add all of the new clips.
                AddOrUpdateClips(newClips);

                _clipMineLock.ExitWriteLock();
            }

            {
                // The cooking operations are all ready-only, so use the read only lock.
                _clipMineLock.EnterReadLock();

                // Update
                UpdateCookedData();

                _clipMineLock.ExitReadLock();
            }

            _lastUpdateTime = DateTimeOffset.Now;
            _lastUpdateDuration = (_lastUpdateTime - start) + updateDuration;

            // Check if we should write our current database as a backup.
            if (!isRestore && DateTimeOffset.Now - _lastDatabaseBackup > new TimeSpan(0, 30, 0))
            {
                _historian.BackupCurrentDb(_clipMine, this, c_databaseVersion);
                _lastDatabaseBackup = DateTimeOffset.Now;
            }

        }

        // Needs to be called under lock!
        private void OfflineAndCleanUpClipMine()
        {
            var toRemove = new List<string>();
            foreach (KeyValuePair<string, MixerClip> p in _clipMine)
            {
                // Set the channel offline and the view count to 0.
                // When the currently online channel are added these will be
                // updated to the current values.
                p.Value.Channel.Online = false;
                p.Value.Channel.ViewersCurrent = 0;

                // If the clips is expired, remove it.
                if (DateTimeOffset.UtcNow > p.Value.ExpirationDate)
                {
                    toRemove.Add(p.Key);
                }
            }

            // Remove old clips
            foreach (string s in toRemove)
            {
                if (!_clipMine.TryRemove(s, out var clip))
                {
                    Logger.Error($"Could not remove clip {s}");
                }
            }

            Logger.Info($"Mine cleanup done, removed {toRemove.Count} old clips.");
        }

        // Needs to be called under lock!
        private void AddOrUpdateClips(List<MixerClip> freshClips)
        {
            int added = 0;
            int updated = 0;
            foreach (MixerClip c in freshClips)
            {
                if (_clipMine.TryGetValue(c.ContentId, out var clip))
                {
                    // The clips already exists, update the clip and channel info
                    // from this newer data.
                    clip.UpdateFromNewer(c);
                    updated++;
                }
                else
                {
                    // The clip doesn't exist, add it.
                    _clipMine.TryAdd(c.ContentId, c);
                    added++;
                }
            }
            Logger.Info($"Clip update done; {added} added, {updated} updated.");
        }

        // Needs to be called under lock!
        private void UpdateCookedData()
        {
            var start = DateTimeOffset.Now;
            SetStatus($"Updating MixTok ranks...");

            // Update the mixtock rank on all the clips we know of.
            // We do this for all clips since it effects offline channels.
            UpdateMixTokRanks();

            // Update the view count sorted list.
            // First build a temp list outside of lock and then swap them.
            LinkedList<MixerClip> temp = BuildList(_clipMine, ClipMineSortTypes.ViewCount, "view count");
            lock (_viewCountSortedLock)
            {
                _viewCountSortedList = temp;
            }

            // Update the mixtok sorted list.
            temp = BuildList(_clipMine, ClipMineSortTypes.MixTokRank, "Mixtok rank");
            lock (_mixTockSortedLock)
            {
                _mixTockSortedList = temp;
            }

            // Update the most recent list.
            temp = BuildList(_clipMine, ClipMineSortTypes.MostRecent, "most recent");
            lock (_mostRecentSortedLock)
            {
                _mostRecentList = temp;
            }

            SetStatus($"Cooking data done:  {Util.FormatTime(DateTimeOffset.Now - start)}", new TimeSpan(0, 0, 10));
            Logger.Info($"Cooking data done: {DateTimeOffset.Now - start}");
        }

        private LinkedList<MixerClip> BuildList(ConcurrentDictionary<string, MixerClip> db, ClipMineSortTypes type, string indexType)
        {
            int count = 0;
            var tempList = new LinkedList<MixerClip>();
            foreach (KeyValuePair<string, MixerClip> p in db)
            {
                InsertSort(ref tempList, p.Value, type);

                // Do to issues with API perf while we are sorting, we need to manually yield the thread to 
                // give the APIs time to process.
                count++;
                if (count % 1000 == 0)
                {
                    SetStatus($"Cooking {indexType} [{String.Format("{0:n0}", count)}/{String.Format("{0:n0}", db.Count)}]...");
                    Thread.Sleep(5);
                }
            }
            return tempList;
        }

        private void InsertSort(ref LinkedList<MixerClip> list, MixerClip c, ClipMineSortTypes type)
        {
            var node = list.First;
            while (node != null)
            {
                bool result = false;
                switch (type)
                {
                    case ClipMineSortTypes.MostRecent:
                        result = c.UploadDate > node.Value.UploadDate;
                        break;
                    case ClipMineSortTypes.MixTokRank:
                        result = c.MixTokRank > node.Value.MixTokRank;
                        break;
                    default:
                    case ClipMineSortTypes.ViewCount:
                        result = c.ViewCount > node.Value.ViewCount;
                        break;
                }
                if (result)
                {
                    list.AddBefore(node, c);
                    return;
                }
                node = node.Next;
            }
            list.AddLast(c);
        }

        public List<MixerClip> GetClips(ClipMineSortTypes sortType,
            int limit = 100,
            DateTimeOffset? fromTime = null, DateTimeOffset? toTime = null,
            int? ViewCountMin = null,
            int? channelIdFilter = null, string channelName = null, int? hypeZoneChannelId = null,
            bool? currentlyLive = null, bool? partnered = null,
            string gameTitle = null, int? gameId = null,
            string languageFilter = null)
        {
            // Get the pre-sorted list we want.
            var list = default(LinkedList<MixerClip>);
            var lockList = new object();

            switch (sortType)
            {
                default:
                case ClipMineSortTypes.ViewCount:
                    list = _viewCountSortedList;
                    lockList = _viewCountSortedLock;
                    break;
                case ClipMineSortTypes.MixTokRank:
                    list = _mixTockSortedList;
                    lockList = _mixTockSortedLock;
                    break;
                case ClipMineSortTypes.MostRecent:
                    list = _mostRecentList;
                    lockList = _mostRecentSortedLock;
                    break;
            }

            var output = new List<MixerClip>();
            // Lock the list so it doesn't change while we are using it.
            lock (lockList)
            {
                // Go through the current sorted list from the highest to the lowest.
                // Apply the filtering and then build the output list.
                var node = list.First;
                while (output.Count < limit && node != null)
                {
                    // Get the node and advance here, becasue this will continue early
                    // if the search filters it out.
                    var c = node.Value;
                    node = node.Next;

                    if (channelIdFilter.HasValue)
                    {
                        // Check if this is the channel we want.
                        if (c.Channel.Id != channelIdFilter.Value)
                        {
                            continue;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(channelName))
                    {
                        // Check if the channel name has the current filter.                        
                        if (c.Channel.Name.IndexOf(channelName, 0, StringComparison.InvariantCultureIgnoreCase) == -1)
                        {
                            continue;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(gameTitle))
                    {
                        // Check if the game title has the current filter string.
                        if (c.GameTitle.IndexOf(gameTitle, 0, StringComparison.InvariantCultureIgnoreCase) == -1)
                        {
                            continue;
                        }
                    }
                    if (gameId.HasValue)
                    {
                        if (c.TypeId != gameId)
                        {
                            continue;
                        }
                    }
                    if (fromTime.HasValue)
                    {
                        // Check if this is in the time range we want.
                        if (c.UploadDate < fromTime.Value)
                        {
                            continue;
                        }
                    }
                    if (toTime.HasValue)
                    {
                        // Check if this is in the time range we want.
                        if (c.UploadDate > toTime.Value)
                        {
                            continue;
                        }
                    }
                    if (ViewCountMin.HasValue)
                    {
                        if (c.ViewCount < ViewCountMin)
                        {
                            continue;
                        }
                    }
                    if (partnered.HasValue)
                    {
                        if (partnered.Value != c.Channel.Partnered)
                        {
                            continue;
                        }
                    }
                    if (currentlyLive.HasValue)
                    {
                        if (currentlyLive.Value != c.Channel.Online)
                        {
                            continue;
                        }
                    }
                    if (hypeZoneChannelId.HasValue)
                    {
                        if (hypeZoneChannelId.Value != c.HypeZoneChannelId)
                        {
                            continue;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(languageFilter))
                    {
                        if (!c.Channel.Language.Equals(languageFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    // If we got to then end this didn't get filtered.
                    // So add it.
                    output.Add(c);
                }
            }
            return output;
        }

        private void UpdateMixTokRanks()
        {
            // The min age a clip can be.
            var s_minClipAge = new TimeSpan(0, 10, 0);

            // For each clip, update the rank
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            foreach (KeyValuePair<string, MixerClip> p in _clipMine)
            {
                var clip = p.Value;

                // Compute the view rank
                var viewRank = (double)clip.ViewCount;

                // Decay the view rank by time
                var age = (nowUtc - clip.UploadDate);

                // Clamp by the min age to give all clips some time
                // to pick up viewers.
                if (age < s_minClipAge)
                {
                    age = s_minClipAge;
                }
                double decayedRank = viewRank / (Math.Pow(age.TotalDays, 1.5));

                clip.MixTokRank = decayedRank;
            }
        }

        public int GetClipsCount()
        {
            int result = 0;
            if (_clipMineLock.TryEnterReadLock(5))
            {
                result = _clipMine.Count;
                _clipMineLock.ExitReadLock();
            }
            return result;
        }

        public Tuple<int, int> GetChannelCount()
        {
            var result = new Tuple<int, int>(0, 0);
            if (_clipMineLock.TryEnterReadLock(5))
            {
                var channelMap = new ConcurrentDictionary<int, bool>();
                foreach (KeyValuePair<string, MixerClip> p in _clipMine)
                {
                    if (!channelMap.ContainsKey(p.Value.Channel.Id))
                    {
                        channelMap.TryAdd(p.Value.Channel.Id, p.Value.Channel.Online);
                    }
                }
                int online = 0;
                foreach (KeyValuePair<int, bool> p in channelMap)
                {
                    if (p.Value)
                    {
                        online++;
                    }
                }
                result = new Tuple<int, int>(channelMap.Count, online);

                _clipMineLock.ExitReadLock();
            }
            return result;
        }

        public int ClipsCreatedInLastTime(TimeSpan ts)
        {
            int result = 0;
            if (_clipMineLock.TryEnterReadLock(5))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (KeyValuePair<string, MixerClip> p in _clipMine)
                {
                    if (now - p.Value.UploadDate < ts)
                    {
                        result++;
                    }
                }
                _clipMineLock.ExitReadLock();
            }
            return result;
        }

        public DateTimeOffset GetLastUpdateTime()
        {
            return _lastUpdateTime;
        }

        public TimeSpan GetLastUpdateDuration()
        {
            return _lastUpdateDuration;
        }

        public DateTimeOffset GetLastBackupTime()
        {
            return _lastDatabaseBackup;
        }

        public void SetStatus(string str, TimeSpan? duration = null)
        {
            // Check if we have a lingering message
            if (!duration.HasValue)
            {
                if ((DateTimeOffset.Now - _statusDurationSet) < _statusDuration)
                {
                    return;
                }
            }

            _status = str;

            // Set the new duration if not.
            if (duration.HasValue)
            {
                _statusDurationSet = DateTimeOffset.Now;
                _statusDuration = duration.Value;
            }
        }

        public string GetStatus()
        {
            return _status;
        }
    }
}
