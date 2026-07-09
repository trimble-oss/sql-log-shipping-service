using Serilog;
using static LogShippingService.BackupHeader;
using System.Reflection.PortableExecutable;
using LogShippingService.FileHandling;
using SerilogTimings;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LogShippingService
{
    public abstract class DatabaseInitializerBase
    {
        protected abstract void PollForNewDBs(CancellationToken stoppingToken);

        protected abstract void DoProcessDB(string sourceDb, string targetDb);

        private static Config Config => AppConfig.Config;

        // Shared queue for initialization AND re-initialization.  Both perform a FULL restore, so they share a single bounded
        // consumer pool (Config.MaxConcurrentInitializations) rather than competing with the log-restore worker pool.
        private readonly Channel<InitRequest> _initQueue =
            Channel.CreateUnbounded<InitRequest>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        // Databases currently queued or being (re)initialized, keyed by targetDb.  Used to dedup enqueue requests.
        private readonly ConcurrentDictionary<string, InitReason> _inFlight = new(StringComparer.OrdinalIgnoreCase);

        // A failed re-initialization is requeued (with a delay) up to this many attempts.  New-database initialization is not
        // requeued here - it is naturally retried by the next poll iteration.
        private const int MaxReinitAttempts = 3;

        private static readonly TimeSpan ReinitRetryDelay = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Queue a database to be (re)initialized from a FULL backup.  Deduplicated by target database and gated so log restores
        /// are skipped until the (re)initialization completes.  Non-blocking - the actual restore runs on a consumer thread.
        /// </summary>
        internal void EnqueueInitialization(string sourceDb, string targetDb, InitReason reason, string? detail)
        {
            if (!IsValidated)
            {
                AuditLog.Reinitialization.Error("Cannot {reason} {targetDb}. Initialization is not configured.  Handle it manually.  Detail: {detail}", reason, targetDb, detail);
                return;
            }
            if (!_inFlight.TryAdd(targetDb, reason)) // Already queued or in progress
            {
                Log.Debug("Skipping {reason} for {targetDb}; already initializing.", reason, targetDb);
                return;
            }
            LogShipping.InitializingDBs[targetDb.ToLower()] = targetDb; // Prevent log restores until (re)initialization is complete
            if (!_initQueue.Writer.TryWrite(new InitRequest(sourceDb, targetDb, reason, detail)))
            {
                ReleaseGate(targetDb);
                Log.Error("Failed to queue {reason} for {targetDb}", reason, targetDb);
            }
        }

        /// <summary>Clear the dedup entry and the log-restore gate for a database once (re)initialization is terminal.</summary>
        private void ReleaseGate(string targetDb)
        {
            _inFlight.TryRemove(targetDb, out _);
            LogShipping.InitializingDBs.TryRemove(targetDb.ToLower(), out _); // Log restores can resume
        }

        /// <summary>Start the fixed-size pool of consumers that drain the (re)initialization queue.</summary>
        private List<Task> StartInitializationConsumers(CancellationToken stoppingToken)
        {
            var count = Math.Max(1, Config.MaxConcurrentInitializations);
            Log.Information("Starting {count} initialization consumer(s)", count);
            var consumers = new List<Task>(count);
            for (var i = 0; i < count; i++)
            {
                consumers.Add(Task.Run(() => ConsumeInitializationsAsync(stoppingToken), stoppingToken));
            }
            return consumers;
        }

        /// <summary>
        /// Consume (re)initialization requests until cancellation.  In-flight restores are never cancelled mid-operation
        /// (the cancellation token is only observed between requests).  A failed re-initialization is requeued with a delay.
        /// </summary>
        private async Task ConsumeInitializationsAsync(CancellationToken stoppingToken)
        {
            while (await _initQueue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_initQueue.Reader.TryRead(out var req))
                {
                    var requeued = false;
                    try
                    {
                        var success = req.Reason == InitReason.Reinitialize
                            ? RunReInitialize(req.SourceDb, req.TargetDb, req.Detail ?? string.Empty)
                            : RunInitialize(req.SourceDb, req.TargetDb, stoppingToken);

                        if (!success && req.Reason == InitReason.Reinitialize && req.Attempt < MaxReinitAttempts)
                        {
                            requeued = RequeueWithDelay(req with { Attempt = req.Attempt + 1 }, stoppingToken);
                        }
                        else if (!success && req.Reason == InitReason.Reinitialize)
                        {
                            AuditLog.Reinitialization.Error("Giving up re-initializing {targetDb} after {attempts} attempts.  A later log restore will re-detect the broken chain.  Detail: {detail}", req.TargetDb, req.Attempt, req.Detail);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Unhandled error processing {reason} for {targetDb}", req.Reason, req.TargetDb);
                    }
                    finally
                    {
                        if (!requeued)
                        {
                            ReleaseGate(req.TargetDb); // Terminal outcome (success or gave up) - a requeued request keeps the gate held
                        }
                    }
                }
            }
        }

        /// <summary>Requeue a failed re-initialization after a delay, keeping the gate held so log restores stay blocked.</summary>
        private bool RequeueWithDelay(InitRequest req, CancellationToken stoppingToken)
        {
            AuditLog.Reinitialization.Warning("Re-initialization of {targetDb} did not complete.  Retrying (attempt {attempt}/{max}) in {delay}.", req.TargetDb, req.Attempt, MaxReinitAttempts, ReinitRetryDelay);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ReinitRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    ReleaseGate(req.TargetDb);
                    return;
                }
                if (!_initQueue.Writer.TryWrite(req))
                {
                    ReleaseGate(req.TargetDb);
                }
            }, stoppingToken);
            return true;
        }

        /// <summary>
        /// Initialize a new database from a FULL backup.  Returns true when terminal (nothing more to do) - new-database
        /// initialization is not requeued on failure; the next poll iteration retries it.
        /// </summary>
        private bool RunInitialize(string sourceDb, string targetDb, CancellationToken stoppingToken)
        {
            if (!IsValidForInitialization(sourceDb, targetDb)) return true; // Already exists / excluded - nothing to do
            if (stoppingToken.IsCancellationRequested) return true;
            try
            {
                DoProcessDB(sourceDb, targetDb);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error initializing new database from backup {sourceDb}", sourceDb);
            }
            return true;
        }

        /// <summary>
        /// Drop and re-initialize a database whose source log chain can no longer be continued - the source database was
        /// re-created (dropped &amp; re-created with the same name), or the log chain was otherwise broken (e.g. the recovery
        /// model was switched to SIMPLE and back to FULL).  Returns false if the restore did not complete so it can be retried.
        /// </summary>
        private bool RunReInitialize(string sourceDb, string targetDb, string reason)
        {
            // Only drop a database that is in a RESTORING or STANDBY state.  RESTORING is sys.databases.state = 1.
            // A database in STANDBY mode is ONLINE (state = 0) with is_in_standby = 1.
            // This guards against dropping a regular ONLINE database (e.g. one that isn't actually log shipped).
            const byte onlineState = 0;
            const byte restoringState = 1;
            var state = DataHelper.GetDatabaseState(targetDb, Config.Destination);
            var isRestoringOrStandby = state.HasValue && (state.Value.State == restoringState || (state.Value.State == onlineState && state.Value.IsInStandby));
            if (!isRestoringOrStandby)
            {
                AuditLog.Reinitialization.Error("Cannot re-initialize {targetDb}. Expected the database to be in a RESTORING or STANDBY state but sys.databases state is {state}, is_in_standby: {isInStandby}. {targetDb} has NOT been dropped.", targetDb, state?.State.ToString() ?? "not found (database does not exist)", state?.IsInStandby ?? false, targetDb);
                return true; // Not in a droppable state - don't retry
            }

            try
            {
                AuditLog.Reinitialization.Warning("Dropping {targetDb} so it can be re-initialized.  Reason: {reason}", targetDb, reason);
                LogShipping.KillUserConnections(targetDb); // Clear any open connections (e.g. STANDBY) so the drop can proceed
                DataHelper.DropDatabase(targetDb, Config.Destination);
                DoProcessDB(sourceDb, targetDb);
                // DoProcessDB handles initialization errors internally (logged without throwing) - verify the database exists before reporting success
                if (DataHelper.GetDatabaseState(targetDb, Config.Destination) == null)
                {
                    AuditLog.Reinitialization.Error("{targetDb} was dropped but re-initialization did NOT complete.  Check the log for initialization errors.", targetDb);
                    return false; // Database was dropped - retry until it is restored
                }
                AuditLog.Reinitialization.Warning("{targetDb} has been dropped and re-initialized from a current backup.", targetDb);
                return true;
            }
            catch (Exception ex)
            {
                AuditLog.Reinitialization.Error(ex, "Error re-initializing {targetDb}.  Reason for re-initialization: {reason}", targetDb, reason);
                return false; // Retry
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

        public async Task RunPollForNewDBsAsync(CancellationToken stoppingToken)
        {
            if (!IsValidated)
            {
                IsStopped = true;
                return;
            }

            long i = 0;
            var consumers = StartInitializationConsumers(stoppingToken);
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await WaitForNextInitializationAsync(i, stoppingToken);
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
            }
            finally
            {
                // Stop accepting new work, then wait for consumers to finish (in-flight restores are not cancelled mid-operation).
                _initQueue.Writer.TryComplete();
                try
                {
                    await Task.WhenAll(consumers);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Initialization consumers shutdown due to cancellation request");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unexpected error during initialization consumer shutdown");
                }
            }
            Log.Information("Poll for new DBs is shutdown");
            IsStopped = true;
        }

        /// <summary>
        /// Wait for the required time before starting the next iteration.  Either a delay in milliseconds or a cron schedule can be used.  Also waits until active hours if configured.
        /// </summary>
        private static async Task WaitForNextInitializationAsync(long count, CancellationToken stoppingToken)
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
                await Waiter.WaitUntilTimeAsync(nextIterationStart, stoppingToken);
            }
            // If active hours are configured, wait until the next active period
            await Waiter.WaitUntilActiveHoursAsync(stoppingToken);
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
                Log.Warning("Warning. Initializing {targetDb} from a PARTIAL backup. Additional steps might be required to restore READONLY filegroups.  Check sys.master_files to ensure no files are in RECOVERY_PENDING state.", targetDb);
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
            if (Config.SourceToDestinationMapping.TryGetValue(sourceDB.ToLower(), out var targetDB))
            {
                return targetDB;
            }
            return Config.RestoreDatabaseNamePrefix + sourceDB + Config.RestoreDatabaseNameSuffix;
        }

        public static string GetSourceDatabaseName(string destinationDB)
        {
            if (Config.DestinationToSourceMapping.TryGetValue(destinationDB.ToLower(), out var _sourceDB))
            {
                return _sourceDB;
            }
            var prefix = Config.RestoreDatabaseNamePrefix ?? string.Empty;
            var suffix = Config.RestoreDatabaseNameSuffix ?? string.Empty;

            // remove the prefix
            var sourceDB = destinationDB.StartsWith(prefix) ? destinationDB[prefix.Length..] : destinationDB;

            // remove the suffix
            sourceDB = sourceDB.EndsWith(suffix) ? sourceDB[..^suffix.Length] : sourceDB;

            return sourceDB;
        }

        public static string GetDatabaseIdentifier(string sourceDb, string targetDb) =>
            string.Equals(sourceDb, targetDb, StringComparison.OrdinalIgnoreCase) ? targetDb : $"{targetDb} [From: {sourceDb}]";
    }
}