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

            var dbRoot = Config.FullBackupPathTemplate[
                ..Config.FullBackupPathTemplate.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];

            if (Config.IncludedDatabases.Count > 0)
            {
                Parallel.ForEach(Config.IncludedDatabases,
                    new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads },
                    ProcessDB);
            }
            else
            {

                Parallel.ForEach(System.IO.Directory.EnumerateDirectories(dbRoot),
                    new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads },
                    dbFolder =>
                    {
                        var db = Path.GetFileName(dbFolder);
                        ProcessDB(db);
                    });
            }
        }

        protected override void DoProcessDB(string db)
        {
            var fullFolder = Config.FullBackupPathTemplate?.Replace(Config.DatabaseToken, db);
            var diffFolder = Config.DiffBackupPathTemplate?.Replace(Config.DatabaseToken, db);
            if (!Directory.Exists(fullFolder)) return;

            List<string> fullFiles;
            List<string> diffFiles = new();
            try
            {
                fullFiles = GetFilesForLastBackup(fullFolder, Config.ConnectionString);
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
                    diffFiles = GetFilesForLastBackup(diffFolder, Config.ConnectionString);
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

        public static List<string> GetFilesForLastBackup(string folder,string connectionString)
        {
            if (!Directory.Exists(folder)) throw new Exception($"GetFilesForLastBackup: Folder '{folder}' does not exist.");
            List<string> fileList = new();
            var directory = new DirectoryInfo(folder);

            var files = directory.GetFiles("*.bak")
                .Where(f => f.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-Config.MaxBackupAgeForInitialization))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            FileInfo? previousFile = null;
            var backupSetGuid = Guid.Empty;
            foreach (var file in files.TakeWhile(file => previousFile == null || file.LastWriteTime >= previousFile.LastWriteTime.AddMinutes(-60))) // Backups that are part of the same set should have similar last write time
            {
                try
                {
                    var header =
                        BackupHeader.GetHeaders(file.FullName, connectionString, BackupHeader.DeviceTypes.Disk);
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
                    Log.Error(ex,"Error reading backup header for {file}", file.FullName);
                    continue;
                }

                fileList.Add(file.FullName);
                previousFile = file;
            }
            return fileList;
        }

    }
}
