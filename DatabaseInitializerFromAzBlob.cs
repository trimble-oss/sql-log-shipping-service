using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace LogShippingService
{
    internal class DatabaseInitializerFromAzBlob: DatabaseInitializerBase
    {

        protected override void PollForNewDBs()
        {
            if (string.IsNullOrEmpty(Config.FullBackupPathTemplate)) return;

            var dbRoot = Config.FullBackupPathTemplate[..Config.FullBackupPathTemplate.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];
            Log.Debug("Az DB Path : {path}", dbRoot);

            var folders = GetFoldersForAzBlob(dbRoot);
            Log.Debug("Folders: {count}", folders.Count);
            Parallel.ForEach(folders,
                new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads },
                TryProcessDB
            );
        }

        private void TryProcessDB(string db)
        {
            try
            {
                ProcessDB(db);
            }
            catch (Exception ex)
            {
                Log.Error(ex,"Error initializing {db} from azure blob",db);
            }
        }

        private void ProcessDB(string db)
        {
            if (!IsValidForInitialization(db)) return;
            if(string.IsNullOrEmpty(Config.FullBackupPathTemplate)) return;

            Log.Information("Initializing {db}",db);

            var fullPrefix = Config.FullBackupPathTemplate.Replace(Config.DatabaseToken,db);
            var diffPrefix = Config.DiffBackupPathTemplate?.Replace(Config.DatabaseToken, db);

            var fullFiles =  GetFilesForLastBackupAzBlob(fullPrefix);
            var diffFiles = diffPrefix==null ? new List<string>() : GetFilesForLastBackupAzBlob(diffPrefix);

            ProcessRestore(db,fullFiles,diffFiles, BackupHeader.DeviceTypes.Url);
            
        }

        public static List<string> GetFilesForLastBackupAzBlob(string prefix)
        {
            var files = new List<string>();
            var containerUri = new Uri(Config.ContainerURL + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            var filteredBlobs = containerClient
                .GetBlobs(BlobTraits.Metadata, BlobStates.None, prefix)
                .Where(blobItem => blobItem.Properties.LastModified >
                                   DateTime.Now.AddDays(-Config.MaxBackupAgeForInitialization))
                .OrderByDescending(blobItem => blobItem.Properties.LastModified);
            DateTimeOffset? lastmod = null;
            foreach (var blobItem in filteredBlobs)
            {
                if (lastmod == null || lastmod == blobItem.Properties.LastModified)
                {
                    files.Add(Config.ContainerURL + "/" +  blobItem.Name);
                }
                else
                {
                    break;
                }
                lastmod = blobItem.Properties.LastModified;
            }
            return files;
        }


        public override bool IsValidated
        {
            get
            {
                if (string.IsNullOrEmpty(Config.ContainerURL)) return false;
                if (string.IsNullOrEmpty(Config.SASToken)) return false;
                if (string.IsNullOrEmpty(Config.FullBackupPathTemplate)) return false;
                if (!Config.FullBackupPathTemplate.Contains(Config.DatabaseToken)) return false;
                return true;
            }
        }

        private static List<string> GetFoldersForAzBlob(string prefix)
        {
            List<string> folders = new();
            var containerUri = new Uri(Config.ContainerURL + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            var results = containerClient.GetBlobsByHierarchy(prefix: prefix, delimiter: "/").AsPages();

            foreach (Azure.Page<BlobHierarchyItem> blobPage in results)
            {
                foreach (BlobHierarchyItem item in blobPage.Values)
                {
                    var folder = item.Prefix.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Last();
                    folders.Add(folder);
                }
            }

            return folders;
        }
    }
}
