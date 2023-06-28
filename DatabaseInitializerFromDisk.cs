using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace LogShippingService
{
    public class DatabaseInitializerFromDisk: DatabaseInitializerBase
    {
     

        public override bool IsValidated
        {
            get
            {
                if (string.IsNullOrEmpty(Config.FullBackupPathTemplate)) return false;
                if (!Config.FullBackupPathTemplate.Contains(Config.DatabaseToken)) return false;
                return true;
            }
        }

        protected override void PollForNewDBs()
        {
            if (string.IsNullOrEmpty(Config.FullBackupPathTemplate)) return;
      
            var dbRoot = Config.FullBackupPathTemplate[..Config.FullBackupPathTemplate.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];

            Parallel.ForEach(System.IO.Directory.EnumerateDirectories(dbRoot),
                new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads },
                dbFolder =>
                {
                    var db = Path.GetFileName(dbFolder);
                    if (IsStopRequested) return;
                    try
                    {
                        ProcessDB(db);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error initializing {db} from disk", db);
                    }
                });
        }

        private void ProcessDB(string db)
        {
            if (!IsValidForInitialization(db)) return;

            var fullFolder = Config.FullBackupPathTemplate?.Replace(Config.DatabaseToken, db);
            var diffFolder = Config.DiffBackupPathTemplate?.Replace(Config.DatabaseToken, db);
            if (!Directory.Exists(fullFolder)) return;

            List<string> fullFiles;
            List<string> diffFiles = new();
            try
            {
                fullFiles = GetFilesForLastBackup(fullFolder);
                if (fullFiles.Count == 0)
                {
                    throw new Exception($"No backup files for {db} found in {fullFolder}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting files for last FULL backup for {db} in {fullFolder}", db, fullFolder);
                return;
            }

            try
            {
                if (!string.IsNullOrEmpty(diffFolder) && Directory.Exists(diffFolder))
                {
                    diffFiles = GetFilesForLastBackup(diffFolder);
                    if (diffFiles.Count == 0)
                    {
                        Log.Warning("No DIFF backups files for {db} found in {diffFolder}",db,diffFolder);
                    }
                }
                else
                {
                    Log.Warning("Diff backup folder {folder} does not exist.", diffFolder);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error getting files for last DIFF backup for {db} in {diffFolder}. ", db, diffFolder);
            }

            ProcessRestore(db,fullFiles,diffFiles, BackupHeader.DeviceTypes.Disk);
            
        }

        public static List<string> GetFilesForLastBackup(string folder)
        {
            if (!Directory.Exists(folder)) throw new Exception($"GetFilesForLastBackup: Folder '{folder}' does not exist.");
            List<string> fileList = new();
            var directory = new DirectoryInfo(folder);

            var files = directory.GetFiles("*.bak")
                .Where(f => f.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-Config.MaxBackupAgeForInitialization))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            FileInfo? previousFile = null;
            foreach (var file in files)
            {
                if (previousFile == null || previousFile.LastWriteTime == file.LastWriteTime)
                {
                    fileList.Add(file.FullName);
                }
                else
                {
                    break;
                }
                previousFile = file;
            }
            return fileList;
        }

    }
}
