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
            var isPartial = false;
            var fullFiles =  GetFilesForLastBackupAzBlob(fullPrefix,Config.ConnectionString,db, BackupHeader.BackupTypes.DatabaseFull);
            if (fullFiles.Count == 0)
            {
                Log.Information("No full backups found for {db}.  Checking for partial backups.", db);
                fullFiles = GetFilesForLastBackupAzBlob(fullPrefix, Config.ConnectionString, db, BackupHeader.BackupTypes.Partial);
                if (fullFiles.Count == 0)
                {
                    throw new Exception($"No backup files for {db} found in {fullPrefix}");
                }
                else
                {
                    var files = BackupFileListRow.GetFileList(fullFiles, Config.ConnectionString,
                        BackupHeader.DeviceTypes.Url);
                    if (files.All(f => f.FileGroupID != 1))
                    {
                        throw new Exception($"Partial backup for {db} does not include PRIMARY filegroup.");
                    }
                    Log.Warning("Restoring {db} from PARTIAL backup.", db);
                    isPartial = true;
                }
            }
            var diffFiles = diffPrefix==null ? new List<string>() : GetFilesForLastBackupAzBlob(diffPrefix, Config.ConnectionString, db, isPartial ? BackupHeader.BackupTypes.PartialDiff : BackupHeader.BackupTypes.DatabaseDiff);

            ProcessRestore(db,fullFiles,diffFiles, BackupHeader.DeviceTypes.Url);
            
        }

        public static List<string> GetFilesForLastBackupAzBlob(string prefix,string connectionString, string db, BackupHeader.BackupTypes type)
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
                    if (ValidateHeader(header, db, type, ref backupSetGuid, fullPath))
                    {
                        files.Add(fullPath);
                        previousBlobItem = blobItem;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error reading backup header for {file}", fullPath);
                    continue;
                }
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
