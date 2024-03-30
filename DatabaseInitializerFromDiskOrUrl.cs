using System.Text;
using Accessibility;
using Serilog;

namespace LogShippingService
{
    public class DatabaseInitializerFromDiskOrUrl : DatabaseInitializerBase
    {
        public BackupHeader.DeviceTypes DeviceType = FileHandler.DeviceType;
        private static Config Config => AppConfig.Config;

        public override bool IsValidated
        {
            get
            {
                if (string.IsNullOrEmpty(Config.FullFilePath)) return false;
                if (!Config.FullFilePath.Contains(Config.DatabaseToken)) return false;
                if (Config.UsePollForNewDatabasesCron)
                {
                    Log.Information("New DBs initialized from {type} on cron schedule: {cron}", DeviceType, Config.PollForNewDatabasesCron);
                }
                else
                {
                    Log.Information("New DBs initialized from {type} every {interval} mins.", DeviceType, Config.PollForNewDatabasesFrequency);
                }
                return true;
            }
        }

        protected override void PollForNewDBs(CancellationToken stoppingToken)
        {
            if (string.IsNullOrEmpty(Config.FullFilePath)) return;

            Parallel.ForEach(FileHandler.GetDatabases(),
                new ParallelOptions { MaxDegreeOfParallelism = Config.MaxThreads },
                (database, state) =>
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        state.Stop(); // Stop the loop if cancellation is requested
                    }

                    ProcessDB(database, stoppingToken);
                });
        }

        protected override void DoProcessDB(string db)
        {
            var fullFolder = Config.FullFilePath?.Replace(Config.DatabaseToken, db);
            var diffFolder = Config.DiffFilePath?.Replace(Config.DatabaseToken, db);
            var readOnlyFolder = Config.ReadOnlyFilePath?.Replace(Config.DatabaseToken, db);
            if (fullFolder == null) { return; }
            if (DeviceType == BackupHeader.DeviceTypes.Disk && !Directory.Exists(fullFolder))
            {
                Log.Warning("Skipping {db}.  Directory {path} doesn't exist", db, fullFolder);
                return;
            }

            var isPartial = false;
            List<BackupFile> fullFiles;
            List<BackupFile> diffFiles = new();
            string? readOnlySQL = null;
            try
            {
                fullFiles = GetFilesForLastBackup(fullFolder, db, BackupHeader.BackupTypes.DatabaseFull);
                if (fullFiles.Count == 0 && (!string.IsNullOrEmpty(readOnlyFolder) || Config.RecoverPartialBackupWithoutReadOnly))
                {
                    Log.Information("No full backups found for {db}.  Checking for partial backups.", db);
                    fullFiles = GetFilesForLastBackup(fullFolder, db, BackupHeader.BackupTypes.Partial);
                    if (fullFiles.Count == 0)
                    {
                        throw new Exception($"No backup files for {db} found in {fullFolder}");
                    }
                    else
                    {
                        isPartial = true;
                        var files = fullFiles[0].BackupFileList;
                        if (files.All(f => f.IsPresent)) // We did a partial backup without any readonly filegroups
                        {
                            Log.Warning("Partial backup was used for {db} but backup includes all files.", db);
                        }
                        else if (!string.IsNullOrEmpty(readOnlyFolder))
                        {
                            readOnlySQL = GetReadOnlyRestoreCommand(fullFiles, readOnlyFolder, db, DeviceType);
                            Log.Debug("Restore command for readonly: {ReadOnlySQL}", readOnlySQL);
                            if (string.IsNullOrEmpty(readOnlySQL) & !Config.RecoverPartialBackupWithoutReadOnly)
                            {
                                throw new Exception($"Unable to find readonly backups for {db}.  To recover databases anyway use 'RecoverPartialBackupWithoutReadOnly'");
                            }
                            else if (string.IsNullOrEmpty(readOnlySQL))
                            {
                                Log.Warning("Unable to find readonly backups for {db}.  Restore of partial backup will proceed.  Restore READONLY filegroups manually.", db);
                            }
                        }
                        Log.Warning("Restoring {db} from PARTIAL backup.", db);
                    }
                }
                else if (fullFiles.Count == 0)
                {
                    throw new Exception($"No full backups found for {db}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting files for last FULL backup for {db} in {fullFolder}", db, fullFolder);
                return;
            }

            try
            {
                if (!string.IsNullOrEmpty(diffFolder))
                {
                    diffFiles = GetFilesForLastBackup(diffFolder, db, isPartial ? BackupHeader.BackupTypes.PartialDiff : BackupHeader.BackupTypes.DatabaseDiff);
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
                Log.Warning(ex, "Error getting files for last DIFF backup for {db} in {diffFolder}. Restore will continue with FULL backup", db, diffFolder);
            }

            ProcessRestore(db, fullFiles.GetFileList(), diffFiles.GetFileList(), DeviceType);
            if (!string.IsNullOrEmpty(readOnlySQL))
            {
                Log.Information("Restoring READONLY backups for {db}", db);
                DataHelper.ExecuteWithTiming(readOnlySQL, Config.Destination);
            }
        }

        public static string GetReadOnlyRestoreCommand(List<BackupFile> fullFiles, string path, string db, BackupHeader.DeviceTypes deviceType)
        {
            List<ReadOnlyBackupSet> fileSets = new();
            // Get files that are don't exist in our partial backup
            var missing = fullFiles[0].BackupFileList.Where(f => !f.IsPresent).ToList();

            if (missing.Count == 0)
            {
                Log.Warning("All files are present in the backup.  Nothing to restore");
                return string.Empty;
            }
            Log.Information("Looking for readonly backups for {db}.  Missing files: {files}", db, missing.Select(f => f.LogicalName).ToList());
            var familyGUID = fullFiles[0].FirstHeader.FamilyGUID;

            ReadOnlyBackupSet? backupSet = null;
            foreach (var backupFile in FileHandler.GetFiles(path, "*.BAK", DateTime.MinValue))
            {
                if (backupSet != null && backupSet.BackupFiles[0].FirstHeader.BackupSetGUID == backupFile.FirstHeader.BackupSetGUID) // File is part of the same set as previous.  Add to previous backupset and continue
                {
                    backupSet.BackupFiles.Add(backupFile);
                    continue;
                }
                else if (missing.Count == 0) // We can restore all out
                {
                    break;
                }
                if (backupFile.FirstHeader.FamilyGUID == familyGUID) // Backup belongs to the DB we are restoring
                {
                    backupSet = new ReadOnlyBackupSet
                    {
                        ToRestore = missing.Where(mf => backupFile.BackupFileList.Any(f =>
                            mf.UniqueId == f.UniqueId & mf.ReadOnlyLSN == f.ReadOnlyLSN)).ToList()
                    };
                    backupSet.BackupFiles.Add(backupFile);

                    if (backupSet.ToRestore.Any())
                    {
                        fileSets.Add(backupSet);
                        missing = missing.Where(m => backupSet.ToRestore.All(f => f.UniqueId != m.UniqueId)).ToList();
                    }
                }
            }

            if (missing.Count != 0)
            {
                Log.Error("File restore error for {db}.  Missing backup for files {files}", db, missing.Select(f => f.LogicalName).ToList());
                return string.Empty;
            }
            StringBuilder builder = new();
            foreach (var fileSet in fileSets)
            {
                builder.AppendLine($"RESTORE DATABASE {db.SqlQuote()}");
                builder.AppendLine(string.Join(",\n", fileSet.ToRestore.Select(f => "FILE = " + f.LogicalName.SqlSingleQuote())));
                builder.AppendLine(DataHelper.GetFromDisk(fileSet.BackupFiles.GetFileList(), deviceType));
                builder.AppendLine("WITH NORECOVERY");
                builder.AppendLine();
            }
            return builder.ToString();
        }

        public static List<BackupFile> GetFilesForLastBackup(string folder, string db, BackupHeader.BackupTypes backupType)
        {
            List<BackupFile> fileList = new();
            var directory = new DirectoryInfo(folder);

            var files = FileHandler.GetFiles(folder, "*.bak",
                DateTime.Now.AddDays(-Config.MaxBackupAgeForInitialization));

            BackupFile? previousFile = null;
            var backupSetGuid = Guid.Empty;
            foreach (var file in files.TakeWhile(file => previousFile == null || file.LastModifiedUtc >= previousFile.LastModifiedUtc.AddMinutes(-60))) // Backups that are part of the same set should have similar last write time
            {
                try
                {
                    if (!ValidateHeader(file, db, ref backupSetGuid, backupType)) continue;
                    fileList.Add(file);
                    previousFile = file;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error reading backup header for {file}", file.FilePath);
                    continue;
                }
            }
            return fileList;
        }
    }
}