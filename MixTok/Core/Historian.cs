using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using MixTok.Core.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MixTok.Core
{
    public class HistorianBackup
    {
        public int Version;
        public List<MixerClip> database;
    }

    public class Historian
    {
        public void AttemptToRestore(ClipMine adder, int currentDbVersion)
        {
            adder.SetStatus("Attempting to restore from historian...");
            var start = DateTimeOffset.Now;
            try
            {
                // Look for a history file
                var cloudBlockBlob = GetHistoryFile();
                if(cloudBlockBlob == null)
                {
                    adder.SetStatus("Failed to get history file...");
                    Logger.Info($"Failed to get history file.");
                    return;
                }

                if (!cloudBlockBlob.Exists())
                {
                    Logger.Info($"No history file exits.");
                    return;
                }

                adder.SetStatus("Found history, downloading...");

                // Download and deseralize.
                var backup = default(HistorianBackup);
                using (var stream = new MemoryStream())
                {
                    cloudBlockBlob.DownloadToStream(stream);
                    stream.Position = 0;
                    var serializer = new JsonSerializer();
                    using (var sr = new StreamReader(stream))
                    {
                        using (var jsonTextReader = new JsonTextReader(sr))
                        {
                            backup = serializer.Deserialize<HistorianBackup>(jsonTextReader);
                        }
                    }
                }

                if(backup == null)
                {
                    Logger.Info("Failed to get or deseralize old history file.");
                    return;
                }

                if(backup.Version != currentDbVersion)
                {
                    Logger.Info("History file found, but the verison doesn't match ours.");
                    return;
                }

                adder.SetStatus("History is good, restoring...");

                // Push the database in!
                adder.AddToClipMine(backup.database, (DateTimeOffset.Now - start), true);
            }
            catch(Exception e)
            {
                Logger.Error("Exception thrown in historian restore.", e);
            }
        }

        public void BackupCurrentDb(IDictionary<string, MixerClip> db, ClipMine adder, int currentDbVersion)
        {
            adder.SetStatus("Backing up to historian...");

            try
            {
                // Convert the current map to a list.
                var clips = new List<MixerClip>();
                foreach(KeyValuePair<string, MixerClip> k in db)
                {
                    clips.Add(k.Value);
                }

                // Seralize our current database.
                var backup = new HistorianBackup()
                {
                    database = clips,
                    Version = currentDbVersion
                };
                var json = JsonConvert.SerializeObject(backup);

                // Try to get the history file
                var blob = GetHistoryFile();
                if(blob == null)
                {
                    Logger.Info("Failed to get history blob.");
                    return;
                }

                // Write the json to the file.
                blob.UploadText(json);
            }
            catch(Exception e)
            {
                Logger.Error("Exception in historian write db", e);
            }
        }

        private CloudBlockBlob GetHistoryFile()
        {
            var storageConnectionString = Environment.GetEnvironmentVariable("blobstorageconnectionstring");
            if (string.IsNullOrWhiteSpace(storageConnectionString))
            {
                Logger.Info("No blob storage connection found, not attempting a restore.");
                return null;
            }

            // Try to parse the details.
            var storageAccount = default(CloudStorageAccount);
            if (!CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
            {
                Logger.Info("Failed blob storage connection string.");
                return null;
            }

            // Get the container
            var cloudBlobClient = storageAccount.CreateCloudBlobClient();
            var cloudBlobContainer = cloudBlobClient.GetContainerReference("history");
            cloudBlobContainer.CreateIfNotExists();

            // Look for a history file
            return cloudBlobContainer.GetBlockBlobReference("history");
        }
    }
}
