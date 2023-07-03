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

            if (Config.IncludedDatabases.Count > 0)
            {
                Parallel.ForEach(Config.IncludedDatabases,
                    new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads },
                    ProcessDB
                );
            }
            else
            {
                var folders = GetFoldersForAzBlob(dbRoot);
                Log.Debug("Folders: {count}", folders.Count);
                Parallel.ForEach(folders,
                    new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads },
                    ProcessDB
                );
            }
        }
        
        protected override void DoProcessDB(string db)
        {
            if (!IsValidForInitialization(db)) return;
            if(string.IsNullOrEmpty(Config.FullBackupPathTemplate)) return;

            Log.Information("Initializing {db}",db);

            var fullPrefix = Config.FullBackupPathTemplate.Replace(Config.DatabaseToken,db);
            var diffPrefix = Config.DiffBackupPathTemplate?.Replace(Config.DatabaseToken, db);

            var fullFiles =  GetFilesForLastBackupAzBlob(fullPrefix,Config.ConnectionString);
            var diffFiles = diffPrefix==null ? new List<string>() : GetFilesForLastBackupAzBlob(diffPrefix, Config.ConnectionString);

            ProcessRestore(db,fullFiles,diffFiles, BackupHeader.DeviceTypes.Url);
            
        }

        public static List<string> GetFilesForLastBackupAzBlob(string prefix,string connectionString)
        {
            var files = new List<string>();
            var containerUri = new Uri(Config.ContainerURL + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            var filteredBlobs = containerClient
                .GetBlobs(BlobTraits.Metadata, BlobStates.None, prefix)
                .Where(blobItem => blobItem.Properties.LastModified >
                                   DateTime.Now.AddDays(-Config.MaxBackupAgeForInitialization))
                .OrderByDescending(blobItem => blobItem.Properties.LastModified);
           
            var backupSetGuid = Guid.Empty;
            BlobItem? previousBlobItem=null;
            foreach (var blobItem in filteredBlobs.TakeWhile(blobItem => previousBlobItem == null || blobItem.Properties.LastModified >= previousBlobItem.Properties.LastModified?.AddMinutes(-60))) // Backups that are part of the same set should have similar last write time
            {
                var fullPath = Config.ContainerURL + "/" + blobItem.Name;
                try
                {
                    var header =
                        BackupHeader.GetHeaders(fullPath, connectionString, BackupHeader.DeviceTypes.Url);
                    if (header is { Count: 1 })
                    {
                        var thisGUID = header[0].BackupSetGUID;
                        if (backupSetGuid == Guid.Empty)
                        {
                            backupSetGuid = thisGUID; // First file in backup set
                        }
                        else if (backupSetGuid != thisGUID)
                        {
                            break; // Belongs to a different backup set, exit loop
                        }
                    }
                    else
                    {
                        throw new Exception($"Backup file contains multiple backups.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error reading backup header for {file}", fullPath);
                    continue;
                }

                files.Add(fullPath);
                previousBlobItem = blobItem;
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
