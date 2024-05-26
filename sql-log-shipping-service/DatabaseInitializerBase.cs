using Serilog;
using static LogShippingService.BackupHeader;
using System.Reflection.PortableExecutable;
using LogShippingService.FileHandling;
using SerilogTimings;

namespace LogShippingService
{
    public abstract class DatabaseInitializerBase
    {
        protected abstract void PollForNewDBs(CancellationToken stoppingToken);

        protected abstract void DoProcessDB(string sourceDb, string targetDb);

        private static Config Config => AppConfig.Config;

        protected void ProcessDB(string sourceDb, CancellationToken stoppingToken)
        {
            var targetDb = GetDestinationDatabaseName(sourceDb);
            if (!IsValidForInitialization(sourceDb, targetDb)) return;
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                if (LogShipping.InitializingDBs.TryAdd(sourceDb.ToLower(), sourceDb)) // To prevent log restores until initialization is complete
                {
                    DoProcessDB(sourceDb, targetDb);
                }
                else
                {
                    Log.Error("{sourceDb} is already initializing", sourceDb);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error initializing new database from backup {sourceDb}", sourceDb);
            }
            finally
            {
                LogShipping.InitializingDBs.TryRemove(sourceDb.ToLower(), out _); // Log restores can start after restore operations have completed
            }
        }

        protected List<DatabaseInfo>? DestinationDBs;

        public bool IsStopped { get; private set; }

        public abstract bool IsValidated { get; }

        public bool IsValidForInitialization(string sourceDb, string targetDb)
        {
            if (DestinationDBs == null || DestinationDBs.Exists(d => string.Equals(d.Name, targetDb, StringComparison.CurrentCultureIgnoreCase))) return false;
            var systemDbs = new[] { "master", "model", "msdb" };
            if (systemDbs.Any(s => s.Equals(sourceDb, StringComparison.OrdinalIgnoreCase))) return false;
            if (systemDbs.Any(s => s.Equals(targetDb, StringComparison.OrdinalIgnoreCase))) return false;
            return LogShipping.IsIncludedDatabase(sourceDb) || LogShipping.IsIncludedDatabase(targetDb);
        }

        public async Task RunPollForNewDBs(CancellationToken stoppingToken)
        {
            if (!IsValidated)
            {
                IsStopped = true;
                return;
            }

            long i = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForNextInitialization(i, stoppingToken);
                i++;
                if (stoppingToken.IsCancellationRequested) return;
                try
                {
                    DestinationDBs = DatabaseInfo.GetDatabaseInfo(Config.Destination);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting destination databases.");
                    break;
                }
                try
                {
                    using (var op = Operation.Begin($"Initialize new databases iteration {i}"))
                    {
                        PollForNewDBs(stoppingToken);
                        op.Complete();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error running poll for new DBs");
                }
            }
            Log.Information("Poll for new DBs is shutdown");
            IsStopped = true;
        }

        /// <summary>
        /// Wait for the required time before starting the next iteration.  Either a delay in milliseconds or a cron schedule can be used.  Also waits until active hours if configured.
        /// </summary>
        private static async Task WaitForNextInitialization(long count, CancellationToken stoppingToken)
        {
            var nextIterationStart = DateTime.Now.AddMinutes(Config.PollForNewDatabasesFrequency);
            if (Config.UsePollForNewDatabasesCron)
            {
                var next = Config.PollForNewDatabasesCronExpression?.GetNextOccurrence(DateTimeOffset.Now, TimeZoneInfo.Local);
                if (next.HasValue) // null can be returned if the value is unreachable. e.g. 30th Feb.  It's not expected, but log a warning and fall back to default delay if it happens.
                {
                    nextIterationStart = next.Value.DateTime;
                }
                else
                {
                    Log.Warning("No next occurrence found for PollForNewDatabasesCron.  Using default delay. {Delay}mins", Config.PollForNewDatabasesFrequency);
                }
            }

            if (Config.UsePollForNewDatabasesCron ||
                count > 0) // Only apply delay on first iteration if using a cron schedule
            {
                Log.Information("Next new database initialization will start at {nextIterationStart}", nextIterationStart);
                await Waiter.WaitUntilTime(nextIterationStart, stoppingToken);
            }
            // If active hours are configured, wait until the next active period
            await Waiter.WaitUntilActiveHours(stoppingToken);
        }

        protected static void ProcessRestore(string sourceDb, string targetDb, List<string> fullFiles, List<string> diffFiles, BackupHeader.DeviceTypes deviceType)
        {
            var fullHeader = BackupHeader.GetHeaders(fullFiles, Config.Destination, deviceType);

            if (fullHeader.Count > 1)
            {
                Log.Error("Backup header returned multiple rows");
                return;
            }
            else if (fullHeader.Count == 0)
            {
                Log.Error("Error reading backup header. 0 rows returned.");
                return;
            }
            else if (!string.Equals(fullHeader[0].DatabaseName, sourceDb, StringComparison.CurrentCultureIgnoreCase))
            {
                Log.Error("Backup is for {sourceDb}.  Expected {expectedDB}. {fullFiles}", fullHeader[0].DatabaseName, sourceDb, fullFiles);
                return;
            }
            else if (fullHeader[0].RecoveryModel == "SIMPLE" && !Config.InitializeSimple)
            {
                Log.Warning("Skipping initialization of {sourceDb} due to SIMPLE recovery model. InitializeSimple can be set to alter this behaviour for disaster recovery purposes.", sourceDb);
                return;
            }
            else if (fullHeader[0].BackupType is not (BackupHeader.BackupTypes.DatabaseFull or BackupHeader.BackupTypes.Partial))
            {
                Log.Error("Unexpected backup type {type}. {fullFiles}", fullHeader[0].BackupType, fullFiles);
            }
            if (fullHeader[0].BackupType == BackupHeader.BackupTypes.Partial)
            {
                Log.Warning("Warning. Initializing {sourceDb} from a PARTIAL backup. Additional steps might be required to restore READONLY filegroups.  Check sys.master_files to ensure no files are in RECOVERY_PENDING state.", sourceDb);
            }

            var moves = DataHelper.GetFileMoves(fullFiles, deviceType, Config.Destination, Config.MoveDataFolder, Config.MoveLogFolder,
                Config.MoveFileStreamFolder, sourceDb, targetDb);
            var restoreScript = DataHelper.GetRestoreDbScript(fullFiles, targetDb, deviceType, true, moves);
            // Restore FULL
            DataHelper.ExecuteWithTiming(restoreScript, Config.Destination);

            if (diffFiles.Count <= 0) return;

            // Check header for DIFF
            var diffHeader =
                BackupHeader.GetHeaders(diffFiles, Config.Destination, deviceType);

            if (IsDiffApplicable(fullHeader, diffHeader))
            {
                // Restore DIFF is applicable
                restoreScript = DataHelper.GetRestoreDbScript(diffFiles, targetDb, deviceType, false);
                DataHelper.ExecuteWithTiming(restoreScript, Config.Destination);
            }
        }

        public static bool IsDiffApplicable(List<BackupHeader> fullHeaders, List<BackupHeader> diffHeaders)
        {
            if (fullHeaders.Count == 1 && diffHeaders.Count == 1)
            {
                return IsDiffApplicable(fullHeaders[0], diffHeaders[0]);
            }
            return false;
        }

        public static bool IsDiffApplicable(BackupHeader full, BackupHeader diff) => full.CheckpointLSN == diff.DifferentialBaseLSN && full.BackupSetGUID == diff.DifferentialBaseGUID && diff.BackupType is BackupHeader.BackupTypes.DatabaseDiff or BackupHeader.BackupTypes.PartialDiff;

        protected static bool ValidateHeader(BackupFile file, string db, ref Guid backupSetGuid, BackupTypes backupType)
        {
            if (file.Headers is { Count: 1 })
            {
                var header = file.FirstHeader;
                if (!string.Equals(header.DatabaseName, db, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Skipping {file}.  Backup is for {HeaderDB}.  Expected {ExpectedDB}", file.FilePath, header.DatabaseName, db);
                    return false;
                }

                if (header.BackupType != backupType)
                {
                    Log.Warning("Skipping {file} for {sourceDb}.  Backup type is {BackupType}.  Expected {ExpectedBackupType}", file.FilePath, db, header.BackupType, backupType);
                    return false;
                }
                var thisGUID = header.BackupSetGUID;
                if (backupSetGuid == Guid.Empty)
                {
                    backupSetGuid = thisGUID; // First file in backup set
                }
                else if (backupSetGuid != thisGUID)
                {
                    return false; // Belongs to a different backup set
                }
                return true;
            }
            else
            {
                Log.Warning($"Backup file contains multiple backups and will be skipped. {file.FilePath}");
                return false;
            }
        }

        public static string GetDestinationDatabaseName(string sourceDB)
        {
            return Config.RestoreDatabaseNamePrefix + sourceDB + Config.RestoreDatabaseNameSuffix;
        }

        public static string GetSourceDatabaseName(string destinationDB)
        {
            var prefix = Config.RestoreDatabaseNamePrefix ?? string.Empty;
            var suffix = Config.RestoreDatabaseNameSuffix ?? string.Empty;

            // remove the prefix
            var sourceDB = destinationDB.StartsWith(prefix) ? destinationDB[prefix.Length..] : destinationDB;

            // remove the suffix
            sourceDB = sourceDB.EndsWith(suffix) ? sourceDB[..^suffix.Length] : sourceDB;

            return sourceDB;
        }
    }
}