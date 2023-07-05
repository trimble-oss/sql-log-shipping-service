using Serilog;

namespace LogShippingService
{
    public class DatabaseInitializerFromDisk : DatabaseInitializerBase
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

            var isPartial = false;
            List<string> fullFiles;
            List<string> diffFiles = new();
            try
            {
                fullFiles = GetFilesForLastBackup(fullFolder, Config.ConnectionString, db, BackupHeader.BackupTypes.DatabaseFull);
                if (fullFiles.Count == 0)
                {
                    Log.Information("No full backups found for {db}.  Checking for partial backups.", db);
                    fullFiles = GetFilesForLastBackup(fullFolder, Config.ConnectionString, db, BackupHeader.BackupTypes.Partial);

                    if (fullFiles.Count == 0)
                    {
                        throw new Exception($"No backup files for {db} found in {fullFolder}");
                    }
                    else
                    {
                        var files = BackupFileListRow.GetFileList(fullFiles, Config.ConnectionString,
                            BackupHeader.DeviceTypes.Disk);
                        if (files.All(f => f.FileGroupID != 1))
                        {
                            throw new Exception($"Partial backup for {db} does not include PRIMARY filegroup.");
                        }
                        Log.Warning("Restoring {db} from PARTIAL backup.", db);
                        isPartial = true;
                    }
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
                    diffFiles = GetFilesForLastBackup(diffFolder, Config.ConnectionString, db, isPartial ? BackupHeader.BackupTypes.PartialDiff : BackupHeader.BackupTypes.DatabaseDiff);
                    if (diffFiles.Count == 0)
                    {
                        Log.Warning("No DIFF backups files for {db} found in {diffFolder}", db, diffFolder);
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

            ProcessRestore(db, fullFiles, diffFiles, BackupHeader.DeviceTypes.Disk);
        }

        public static List<string> GetFilesForLastBackup(string folder, string connectionString, string db, BackupHeader.BackupTypes backupType)
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
                    var headers =
                        BackupHeader.GetHeaders(file.FullName, connectionString, BackupHeader.DeviceTypes.Disk);
                    if (!ValidateHeader(headers, db, backupType, ref backupSetGuid, file.FullName)) continue;
                    fileList.Add(file.FullName);
                    previousFile = file;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error reading backup header for {file}", file.FullName);
                    continue;
                }
            }
            return fileList;
        }
    }
}