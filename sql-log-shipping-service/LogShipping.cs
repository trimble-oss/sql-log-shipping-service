using LogShippingService.FileHandling;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Serilog;
using SerilogTimings;
using System.Collections.Concurrent;
using System.Data;
using System.Numerics;
using System.Threading.Channels;

namespace LogShippingService
{
    internal class LogShipping : BackgroundService
    {
        public static ConcurrentDictionary<string, string> InitializingDBs = new();

        private readonly DatabaseInitializerBase? _initializer;

        private static readonly object locker = new();

        private static Config Config => AppConfig.Config;

        // Persistent work queue (unbounded to allow all databases to be queued per iteration)
        private static readonly Channel<QueueItem> WorkQueue =
            Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });

        private static readonly ConcurrentDictionary<string, byte> InProgress = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> Enqueued = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Task> _workers = new();

        // Monitoring counters
        private static long _enqueuedCount;

        private static long _enqueueSkipAlreadyEnqueuedCount;
        private static long _enqueueSkipInProgressCount;
        private static long _dequeuedCount;
        private static long _processedCount;
        private static long _errorCount;

        public LogShipping()
        {
            if (string.IsNullOrEmpty(Config.LogFilePath))
            {
                var message = "LogFilePath was not specified";
                Log.Error(message); throw new Exception(message);
            }
            if (!string.IsNullOrEmpty(Config.SourceConnectionString))
            {
                if (Config.UsePollForNewDatabasesCron)
                {
                    Log.Information("New DBs initialized from msdb history on cron schedule: {cron}", Config.PollForNewDatabasesCron);
                }
                else
                {
                    Log.Information("New DBs initialized from msdb history every {interval} mins.", Config.PollForNewDatabasesFrequency);
                }
                _initializer = new DatabaseInitializerFromMSDB();
            }
            else
            {
                _initializer = new DatabaseInitializerFromDiskOrUrl();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(Stop);

            // Start fixed-size worker pool once
            StartWorkers(stoppingToken);

            var logRestoreTask = StartProcessingAsync(stoppingToken);

            try
            {
                if (_initializer != null)
                {
                    await Task.WhenAll(logRestoreTask, _initializer.RunPollForNewDBsAsync(stoppingToken));
                }
                else
                {
                    await logRestoreTask;
                }
            }
            catch (TaskCanceledException)
            {
                Log.Information("Processing stopped due to cancellation request");
                await Log.CloseAndFlushAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Processing stopped due to unexpected error");
                await Log.CloseAndFlushAsync();
                Environment.Exit(1);
            }
            finally
            {
                // Complete queue to release waiting workers and allow graceful shutdown
                WorkQueue.Writer.TryComplete();
                try
                {
                    await Task.WhenAll(_workers);
                }
                catch (OperationCanceledException)
                {
                    // Log worker shutdown due to cancellation (expected)
                    Log.Information("Workers shutdown due to cancellation request");
                }
                catch (Exception ex)
                {
                    // Log any unexpected errors during shutdown
                    Log.Error(ex, "Unexpected error during worker shutdown");
                }
            }
        }

        private void StartWorkers(CancellationToken stoppingToken)
        {
            for (int i = 0; i < Config.MaxThreads; i++)
            {
                _workers.Add(Task.Run(() => WorkerDequeueAndProcessAsync(stoppingToken), stoppingToken));
            }
        }

        // Processes queued databases until cancellation.
        private async Task WorkerDequeueAndProcessAsync(CancellationToken stoppingToken)
        {
            while (await WorkQueue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (WorkQueue.Reader.TryRead(out var item))
                {
                    // Count dequeue events
                    Interlocked.Increment(ref _dequeuedCount);

                    Enqueued.TryRemove(item.TargetDb, out _);

                    if (!InProgress.TryAdd(item.TargetDb, 0))
                    {
                        // Count skip because DB is already processing
                        // The database will be re-enqueued on the next iteration when QueueDatabasesForProcessing runs
                        Interlocked.Increment(ref _enqueueSkipInProgressCount);
                        Log.Debug("Skipping {targetDb}. Previous processing still running.", item.TargetDb);
                        continue;
                    }

                    try
                    {
                        if (stoppingToken.IsCancellationRequested || !Waiter.CanRestoreLogsNow)
                        {
                            break;
                        }
                        await ProcessDatabaseAsync(item, stoppingToken);
                        // Count successful process
                        Interlocked.Increment(ref _processedCount);
                    }
                    catch (Exception ex)
                    {
                        // Count errors
                        Interlocked.Increment(ref _errorCount);
                        Log.Error(ex, "Unhandled exception in background processing for {targetDb}", item.TargetDb);
                    }
                    finally
                    {
                        InProgress.TryRemove(item.TargetDb, out _);
                    }
                }
            }
        }

        private async Task StartProcessingAsync(CancellationToken stoppingToken)
        {
            long i = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForNextIterationAsync(i, stoppingToken);
                i++;
                using (Operation.Time($"Log restore iteration {i}"))
                {
                    Log.Information("Starting log restore iteration {0}", i);
                    try
                    {
                        await QueueDatabasesForProcessing(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Unexpected error processing log restores");
                    }
                    finally
                    {
                        // Emit iteration summary
                        var s = GetStats();
                        Log.Information("Iteration {iteration} stats: Enqueued={Enqueued}, Dequeued={Dequeued}, Processed={Processed}, SkippedAlreadyEnqueued={SkipEnqueued}, SkippedInProgress={SkipInProgress}, Errors={Errors}, InProgressNow={InProgress}",
                            i, s.Enqueued, s.Dequeued, s.Processed, s.EnqueueSkipAlreadyEnqueued, s.EnqueueSkipInProgress, s.Errors, s.InProgressCount);
                    }
                }
            }
            Log.Information("Finished processing LOG restores");
        }

        /// <summary>
        /// Wait for the required time before starting the next iteration.  Either a delay in milliseconds or a cron schedule can be used.  Also waits until active hours if configured.
        /// </summary>
        private static async Task WaitForNextIterationAsync(long count, CancellationToken stoppingToken)
        {
            var nextIterationStart = DateTime.Now.AddMilliseconds(Config.DelayBetweenIterationsMs);
            if (Config.UseLogRestoreScheduleCron)
            {
                var next = Config.LogRestoreCron?.GetNextOccurrence(DateTimeOffset.Now, TimeZoneInfo.Local);
                if (next.HasValue) // null can be returned if the value is unreachable. e.g. 30th Feb.  It's not expected, but log a warning and fall back to default delay if it happens.
                {
                    nextIterationStart = next.Value.DateTime;
                }
                else
                {
                    Log.Warning("No next occurrence found for LogRestoreScheduleCron.  Using default delay.");
                }
            }

            if (Config.UseLogRestoreScheduleCron ||
                count > 0) // Only apply delay on first iteration if using a cron schedule
            {
                Log.Information("Next log restore iteration will start at {nextIterationStart}", nextIterationStart);
                await Waiter.WaitUntilTimeAsync(nextIterationStart, stoppingToken);
            }
            // If active hours are configured, wait until the next active period
            await Waiter.WaitUntilActiveHoursAsync(stoppingToken);
        }

        public void Stop()
        {
            Log.Information("Initiating shutdown...");
        }

        // Enqueue databases to be processed by workers.
        private Task QueueDatabasesForProcessing(CancellationToken stoppingToken)
        {
            DataTable dt;
            using (Operation.Time("GetDatabases"))
            {
                try
                {
                    dt = GetDatabases();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting databases");
                    return Task.CompletedTask;
                }
            }

            foreach (var row in dt.AsEnumerable())
            {
                if (stoppingToken.IsCancellationRequested || !Waiter.CanRestoreLogsNow) break;

                var targetDb = (string)row["Name"];
                var sourceDb = DatabaseInitializerBase.GetSourceDatabaseName(targetDb);

                if (InitializingDBs.ContainsKey(targetDb.ToLower()))
                {
                    Log.Information("Skipping log restores for {targetDb} due to initialization", targetDb);
                    continue;
                }

                // Skip if currently being processed
                if (InProgress.ContainsKey(targetDb))
                {
                    Interlocked.Increment(ref _enqueueSkipInProgressCount);
                    Log.Debug("Skipping {targetDb}. Already in-progress.", targetDb);
                    continue;
                }

                var fromDate = row["backup_finish_date"] as DateTime? ?? DateTime.Now.AddDays(-Config.MaxBackupAgeForInitialization);
                fromDate = fromDate.AddMinutes(Config.OffsetMins);

                var item = new QueueItem(sourceDb, targetDb, fromDate);

                // Atomically mark as enqueued before writing to the channel.
                // If TryAdd fails, it is already enqueued from a previous iteration.
                if (!Enqueued.TryAdd(targetDb, 0))
                {
                    Interlocked.Increment(ref _enqueueSkipAlreadyEnqueuedCount);
                    Log.Debug("Skipping {targetDb}. Already enqueued from a previous iteration.", targetDb);
                    continue;
                }

                // Try to enqueue. If enqueue fails for any reason, remove the 'Enqueued' mark to avoid staleness.
                var posted = WorkQueue.Writer.TryWrite(item);
                if (posted)
                {
                    Interlocked.Increment(ref _enqueuedCount);
                }
                else
                {
                    Enqueued.TryRemove(targetDb, out _);
                    Log.Error("UNEXPECTED: Queue write failed for {targetDb} on unbounded channel. This may indicate the queue was completed (possibly during shutdown), memory/resource pressure, or an internal error. Check for shutdown signals or resource exhaustion. If this error persists, investigate system health and consider restarting the service. Will retry next iteration.", targetDb);
                }
            }

            // Do NOT wait for queue to drain; iteration ends immediately
            return Task.CompletedTask;
        }

        public static bool IsIncludedDatabase(string db)
        {
            var isExcluded = Config.ExcludedDatabases.Count > 0 && Config.ExcludedDatabases.Any(e => e.Equals(db, StringComparison.OrdinalIgnoreCase));
            var isIncluded = Config.IncludedDatabases.Count == 0 || Config.IncludedDatabases.Any(e => e.Equals(db, StringComparison.OrdinalIgnoreCase));

            return !isExcluded && isIncluded;
        }

        private async Task ProcessDatabaseAsync(QueueItem item, CancellationToken stoppingToken, int processCount = 1, bool reProcess = false)
        {
            var expectedTarget = DatabaseInitializerBase.GetDestinationDatabaseName(item.SourceDb);
            if (!string.Equals(expectedTarget, item.TargetDb, StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("Skipping {targetDb}. Expected target to be {expectedName}", item.TargetDb, expectedTarget);
                return;
            }
            if (!IsIncludedDatabase(item.TargetDb) && !IsIncludedDatabase(item.SourceDb))
            {
                Log.Debug("Skipping {targetDb}. Database is excluded.", item.TargetDb);
                return;
            }
            var logFiles = GetFilesForDb(item.SourceDb, item.FromDate);
            using (var op = Operation.Begin("Restore Logs for {DatabaseName}", item.TargetDb))
            {
                try
                {
                    await RestoreLogsAsync(logFiles, item.SourceDb, item.TargetDb, item.FromDate, reProcess, stoppingToken);
                    op.Complete();
                }
                catch (TimeoutException ex) when (ex.Message == "Max processing time exceeded")
                {
                    Log.Warning(
                        "Max processing time exceeded. Log processing will continue for {targetDb} on the next iteration.",
                        item.TargetDb);
                    op.SetException(ex);
                }
                catch (SqlException ex) when (ex.Number == 4305)
                {
                    await HandleTooRecentAsync(ex, item, processCount, stoppingToken);
                }
                catch (HeaderVerificationException ex) when (ex.VerificationStatus ==
                                                             BackupHeader.HeaderVerificationStatus.TooRecent)
                {
                    await HandleTooRecentAsync(ex, item, processCount, stoppingToken);
                }
                catch (ReinitializeRequiredException ex)
                {
                    op.SetException(ex);
                    ReInitializeDatabase(ex.SourceDb, ex.TargetDb, ex.Reason);
                }
            }
        }

        /// <summary>
        /// Queue a database to be dropped and re-initialized because its source log chain can no longer be continued - the
        /// source database was re-created (dropped &amp; re-created with the same name), or the log chain was otherwise broken
        /// (e.g. the recovery model was switched to SIMPLE and back to FULL).  The restore runs on the shared initialization
        /// queue so the log-restore worker is not blocked.
        /// </summary>
        private void ReInitializeDatabase(string sourceDb, string targetDb, string reason)
        {
            if (_initializer == null)
            {
                AuditLog.Reinitialization.Error("Cannot re-initialize {targetDb}.  No initializer is configured.  Drop and re-initialize it manually.  Reason for re-initialization: {reason}", targetDb, reason);
                return;
            }
            _initializer.EnqueueInitialization(sourceDb, targetDb, InitReason.Reinitialize, reason);
        }

        private async Task HandleTooRecentAsync(Exception ex, QueueItem item, int processCount, CancellationToken stoppingToken)
        {
            switch (processCount)
            {
                // Too recent
                case 1:
                    Log.Warning(ex, "The log file is too recent to apply.  Adjusting fromDate by 60min.");
                    item = new QueueItem(item.SourceDb, item.TargetDb, item.FromDate.AddMinutes(-60));
                    await ProcessDatabaseAsync(item, stoppingToken, processCount + 1, true);
                    break;

                case 2:
                    Log.Warning(ex, "The log file is too recent to apply.  Adjusting fromDate by 1 day.");
                    item = new QueueItem(item.SourceDb, item.TargetDb, item.FromDate.AddMinutes(-1440));
                    await ProcessDatabaseAsync(item, stoppingToken, processCount + 1, true);
                    break;

                default:
                    Log.Error(ex, "Log file too recent to apply.  Manual intervention might be required.");
                    break;
            }
        }

        private static Task RestoreLogsAsync(IEnumerable<BackupFile> logFiles, string sourceDb, string targetDb, DateTime fromDate, bool reProcess, CancellationToken stoppingToken)
        {
            BigInteger? redoStartOrPreviousLastLSN = null;
            if (Config.CheckHeaders)
            {
                redoStartOrPreviousLastLSN = DataHelper.GetRedoStartLSNForDB(targetDb, Config.Destination);
                Log.Debug("{targetDb} Redo Start LSN: {RedoStartLSN}", targetDb, redoStartOrPreviousLastLSN);
            }

            var maxTime = DateTime.Now.AddMinutes(Config.MaxProcessingTimeMins);
            bool breakProcessingFlag = false;
            var stopAt = Config.StopAt > DateTime.MinValue && Config.StopAt < DateTime.MaxValue ? ", STOPAT=" + Config.StopAt.ToString("yyyy-MM-ddTHH:mm:ss.fff").SqlSingleQuote() : "";
            var earlierLogFound = false;

            // DatabaseCreationDate of the last log that belongs to the current chain (last restored / tip).
            // Used to detect when the source database has been dropped & re-created: a re-created database has a newer creation date.
            DateTime? currentChainCreationDate = null;
            // BackupFinishDate of the last log that belongs to the current chain.  A broken/re-created chain is only ever acted on if the
            // triggering log was genuinely taken after this - guards the destructive re-initialization against stale/out-of-order files.
            DateTime? currentChainBackupFinishDate = null;
            // Set when a log is found that breaks the current chain (source database re-created or the log chain was otherwise reset).
            BackupHeader? brokenChainHeader = null;
            string? brokenChainLogPath = null;
            var brokenChainStatus = LogChainStatus.Ok;
            foreach (var logBackup in logFiles)
            {
                if (DateTime.Now > maxTime)
                {
                    RestoreWithStandby(targetDb); // Return database to standby mode
                    // Stop processing logs if max processing time is exceeded. Prevents a single DatabaseName that has fallen behind from impacting other DBs
                    throw new TimeoutException("Max processing time exceeded");
                }
                if (stoppingToken.IsCancellationRequested)
                {
                    RestoreWithStandby(targetDb); // Return database to standby mode
                    Log.Information("Halt log restores for {targetDb} due to stop request", targetDb);
                    break;
                }
                if (!Waiter.CanRestoreLogsNow)
                {
                    RestoreWithStandby(targetDb); // Return database to standby mode
                    Log.Information("Halt log restores for {targetDb} due to Hours configuration", targetDb);
                    break;
                }
                if (breakProcessingFlag)
                {
                    break;
                }

                var file = logBackup.FilePath.SqlSingleQuote();
                var urlOrDisk = Config.DeviceType == BackupHeader.DeviceTypes.Disk ? "DISK" : "URL";
                var sql = $"RESTORE LOG {targetDb.SqlQuote()} FROM {urlOrDisk} = {file} WITH NORECOVERY{stopAt}";

                if (Config.CheckHeaders)
                {
                    List<BackupHeader> headers;
                    try
                    {
                        headers = logBackup.Headers;
                    }
                    catch (SqlException ex)
                    {
                        Log.Error(ex, "Error reading backup header for {logPath}.  Skipping file.", logBackup.FilePath);
                        continue;
                    }

                    if (headers.Count > 1) // Multiple logical backups in single file. This is now handled, but log a warning as it's unexpected.
                    {
                        Log.Warning("Log File {logPath} contains {count} backups.  Expected 1, but each will be processed.", logBackup.FilePath, headers.Count);
                    }

                    foreach (var header in headers)
                    {
                        sql = $"RESTORE LOG {targetDb.SqlQuote()} FROM {urlOrDisk} = {file} WITH NORECOVERY, FILE = {header.Position}{stopAt}";
                        if (!string.Equals(header.DatabaseName, sourceDb, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new HeaderVerificationException(
                                $"Header verification failed for {logBackup}.  Database: {header.DatabaseName}. Expected a backup for {targetDb}", BackupHeader.HeaderVerificationStatus.WrongDatabase);
                        }

                        var action = ClassifyLog(header, currentChainCreationDate, currentChainBackupFinishDate ?? fromDate, redoStartOrPreviousLastLSN, DateTime.Now, Config.RestoreDelayMins);
                        switch (action)
                        {
                            case LogRestoreAction.WaitDelay:
                                Log.Information("Waiting to restore {logPath} & subsequent files.  Backup Finish Date: {BackupFinishDate}. Eligible for restore after {RestoreAfter}, RestoreDelayMins:{RestoreDelay}", logBackup.FilePath, header.BackupFinishDate, header.BackupFinishDate.AddMinutes(Config.RestoreDelayMins), Config.RestoreDelayMins);
                                breakProcessingFlag = true;
                                break;

                            case LogRestoreAction.SourceRecreated:
                            case LogRestoreAction.ChainBroken:
                                // The log can't be applied to the current chain because the source chain was reset
                                // (the source database was re-created, or its recovery model was switched to SIMPLE and back to FULL).
                                // Record only the first detection (the earliest divergence) but keep scanning the remaining files -
                                // a later file may still connect to the redo point (e.g. out-of-order modified dates / reProcess walk-back),
                                // in which case acting now would be premature.  Logging once avoids a warning per newer-incarnation file.
                                if (brokenChainHeader == null)
                                {
                                    brokenChainStatus = action == LogRestoreAction.SourceRecreated ? LogChainStatus.Recreated : LogChainStatus.Broken;
                                    brokenChainHeader = header;
                                    brokenChainLogPath = logBackup.FilePath;
                                    LogBrokenChainDetected(brokenChainStatus, sourceDb, logBackup.FilePath, header, currentChainCreationDate, redoStartOrPreviousLastLSN);
                                }
                                continue;

                            case LogRestoreAction.LastRestored:
                                currentChainCreationDate = header.DatabaseCreationDate; // This is the last log we restored - use its creation date as the reference for the current chain
                                currentChainBackupFinishDate = header.BackupFinishDate;
                                if (reProcess) // Reprocess previous file if we got a too recent error, otherwise skip it
                                {
                                    Log.Information("Re-processing {logPath}, FILE={Position}. FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logBackup.FilePath, header.Position, header.FirstLSN, header.LastLSN);
                                }
                                else
                                {
                                    Log.Information("Skipping {logPath}, FILE={Position}. Found last log file restored.  FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logBackup.FilePath, header.Position, header.FirstLSN, header.LastLSN);
                                }
                                continue;

                            case LogRestoreAction.TooOld:
                                earlierLogFound = true;
                                Log.Information("Skipping {logPath}.  A later LSN is required: {RequiredLSN}, FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logBackup.FilePath, redoStartOrPreviousLastLSN, header.FirstLSN, header.LastLSN);
                                continue;

                            case LogRestoreAction.TooRecent:
                                if (earlierLogFound && reProcess)
                                {
                                    // The current log is too recent. We previously adjusted the search date looking for an earlier log.  Now we have found log files that are too early to apply, then this log that is too recent
                                    // The log chain appears to be broken, but log an error and continue processing in case the file has a later modified date than expected.
                                    Log.Error("Header verification failed for {FilePath}. An earlier LSN is required: {redoStartOrPreviousLastLSN}, FirstLSN: {FirstLSN}, LastLSN: {LastLSN}. NOTE: We previously found a log that was too early to apply.  Log chain might be broken, requiring manual intervention. Continuing to check log files just in case the file has a later modified date than expected.",
                                        logBackup.FilePath, redoStartOrPreviousLastLSN, header.FirstLSN, header.LastLSN);
                                    continue;
                                }
                                throw new HeaderVerificationException($"Header verification failed for {logBackup.FilePath}.  An earlier LSN is required: {redoStartOrPreviousLastLSN}, FirstLSN: {header.FirstLSN}, LastLSN: {header.LastLSN}", BackupHeader.HeaderVerificationStatus.TooRecent);

                            case LogRestoreAction.Apply:
                                Log.Information("Header verification successful for {logPath}, FILE={Position}. FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logBackup.FilePath, header.Position, header.FirstLSN, header.LastLSN);
                                break;
                        }

                        if (breakProcessingFlag)
                        {
                            break; // WaitDelay - stop processing this and subsequent files
                        }

                        var completed = ProcessRestoreCommand(sql, targetDb, file);
                        if (completed && Config.StopAt != DateTime.MinValue && header.BackupFinishDate >= Config.StopAt)
                        {
                            Log.Information("StopAt target reached for {targetDb}.  Last log: {logPath}.  Backup Finish Date: {BackupFinishDate}. StopAt: {StopAt}", targetDb, logBackup.FilePath, header.BackupFinishDate, Config.StopAt);
                            lock (locker) // Prevent future processing of this DB
                            {
                                Config.ExcludedDatabases.Add(targetDb); // Exclude this DB from future processing
                            }
                            breakProcessingFlag = true;
                            break;
                        }
                        redoStartOrPreviousLastLSN = header.LastLSN;
                        if (completed)
                        {
                            // Advance the reference as we restore logs in the current chain
                            currentChainCreationDate = header.DatabaseCreationDate;
                            currentChainBackupFinishDate = header.BackupFinishDate;
                        }
                    }
                }
                else
                {
                    ProcessRestoreCommand(sql, targetDb, file);
                }
            }

            if (brokenChainHeader != null)
            {
                var reason = brokenChainStatus == LogChainStatus.Recreated
                    ? $"source database {sourceDb} has been re-created (dropped & re-created with the same name)"
                    : $"the log chain for source database {sourceDb} has been broken (e.g. the recovery model was switched to SIMPLE and back to FULL)";
                if (Config.EnableReinitialization)
                {
                    AuditLog.Reinitialization.Warning("The log chain for {targetDb} cannot continue - {reason}.  {logPath} begins a new log chain (BeginsLogChain: {BeginsLogChain}, DatabaseCreationDate: {DatabaseCreationDate:o}, FirstLSN: {FirstLSN}).  EnableReinitialization is enabled - {targetDb} will be dropped and re-initialized.",
                        targetDb, reason, brokenChainLogPath, brokenChainHeader.BeginsLogChain, brokenChainHeader.DatabaseCreationDate, brokenChainHeader.FirstLSN, targetDb);
                    throw new ReinitializeRequiredException(
                        $"The log chain for {targetDb} cannot continue - {reason}.  {brokenChainLogPath} begins a new log chain (BeginsLogChain: {brokenChainHeader.BeginsLogChain}, DatabaseCreationDate: {brokenChainHeader.DatabaseCreationDate:o}, FirstLSN: {brokenChainHeader.FirstLSN}).  {targetDb} must be dropped and re-initialized from a current backup to resume log shipping.",
                        sourceDb, targetDb, reason);
                }
                AuditLog.Reinitialization.Error("The log chain for {targetDb} cannot continue - {reason}.  {logPath} begins a new log chain (BeginsLogChain: {BeginsLogChain}, DatabaseCreationDate: {DatabaseCreationDate:o}, FirstLSN: {FirstLSN}).  {targetDb} must be dropped and re-initialized from a current backup to resume log shipping.  Set EnableReinitialization to true to perform this automatically.",
                    targetDb, reason, brokenChainLogPath, brokenChainHeader.BeginsLogChain, brokenChainHeader.DatabaseCreationDate, brokenChainHeader.FirstLSN, targetDb);
            }

            RestoreWithStandby(targetDb);
            return Task.CompletedTask;
        }

        internal enum LogChainStatus
        {
            /// <summary>The log continues the current chain, or its position simply doesn't line up yet (not a broken chain).</summary>
            Ok,

            /// <summary>The source database has been dropped &amp; re-created with the same name (newer DatabaseCreationDate).</summary>
            Recreated,

            /// <summary>The log chain has been reset/broken at the source (e.g. recovery model switched to SIMPLE and back to FULL).</summary>
            Broken
        }

        /// <summary>
        /// The action to take for a single log backup, based on its position relative to the current redo point,
        /// the restore delay, and the chain integrity (<see cref="GetLogChainStatus"/>).  Produced by <see cref="ClassifyLog"/>
        /// so the restore loop can switch on intent rather than re-deriving the decision from several overlapping conditions.
        /// </summary>
        internal enum LogRestoreAction
        {
            /// <summary>The next log in the current chain - restore it.</summary>
            Apply,

            /// <summary>The log is the last one restored - the current tip (its LastLSN matches the redo point) - skip / re-process.</summary>
            LastRestored,

            /// <summary>The log is entirely before the redo point - too early to apply, skip and keep looking.</summary>
            TooOld,

            /// <summary>The log begins after the redo point - a gap; too recent to apply.</summary>
            TooRecent,

            /// <summary>The log is not yet eligible for restore because of the configured restore delay - stop processing.</summary>
            WaitDelay,

            /// <summary>The source database has been dropped &amp; re-created with the same name.</summary>
            SourceRecreated,

            /// <summary>The source log chain has been reset/broken (e.g. recovery model switched to SIMPLE and back to FULL).</summary>
            ChainBroken
        }

        /// <summary>
        /// Classify a single log backup into the <see cref="LogRestoreAction"/> the restore loop should take.  Precedence:
        /// (1) the restore delay (not eligible yet), then (2) the log's LSN position relative to the current redo point, with the
        /// chain integrity (<see cref="GetLogChainStatus"/>) overriding the position when the source was re-created or the chain
        /// was reset.  Pure and deterministic so it can be unit-tested without a SQL Server instance - all state is passed in.
        /// A re-created source diverts every position; a broken chain only diverts the too-old / too-recent positions (matching
        /// the original behaviour, where an exact-tip or spanning log is never treated as broken).
        /// </summary>
        internal static LogRestoreAction ClassifyLog(BackupHeader header, DateTime? currentChainCreationDate, DateTime referenceBackupFinishDate, BigInteger? redoStart, DateTime now, int restoreDelayMins)
        {
            if (restoreDelayMins > 0 && now.Subtract(header.BackupFinishDate).TotalMinutes < restoreDelayMins)
            {
                return LogRestoreAction.WaitDelay;
            }

            if (header.FirstLSN <= redoStart && header.LastLSN == redoStart)
            {
                // The last log we already restored.  Only a re-created source diverts here (an exact-tip log can't be "ahead").
                return GetLogChainStatus(header, currentChainCreationDate, referenceBackupFinishDate, redoStart) == LogChainStatus.Recreated
                    ? LogRestoreAction.SourceRecreated
                    : LogRestoreAction.LastRestored;
            }
            if (header.FirstLSN <= redoStart && header.LastLSN > redoStart)
            {
                // The next log that spans the redo point - restore it.  Only a re-created source diverts here.
                return GetLogChainStatus(header, currentChainCreationDate, referenceBackupFinishDate, redoStart) == LogChainStatus.Recreated
                    ? LogRestoreAction.SourceRecreated
                    : LogRestoreAction.Apply;
            }
            if (header.FirstLSN < redoStart)
            {
                return MapChainStatus(GetLogChainStatus(header, currentChainCreationDate, referenceBackupFinishDate, redoStart), LogRestoreAction.TooOld);
            }
            if (header.FirstLSN > redoStart)
            {
                return MapChainStatus(GetLogChainStatus(header, currentChainCreationDate, referenceBackupFinishDate, redoStart), LogRestoreAction.TooRecent);
            }

            // redoStart is null (no redo point yet) - the LSN comparisons above are all false; restore the log.
            return LogRestoreAction.Apply;

            static LogRestoreAction MapChainStatus(LogChainStatus status, LogRestoreAction whenOk) => status switch
            {
                LogChainStatus.Recreated => LogRestoreAction.SourceRecreated,
                LogChainStatus.Broken => LogRestoreAction.ChainBroken,
                _ => whenOk
            };
        }

        /// <summary>
        /// Classify a log backup that does not connect to the current redo point.  A newer DatabaseCreationDate means the source database
        /// was re-created.  BeginsLogChain means a new log chain has started that we can't bridge onto - but only treat it as broken when it
        /// is ahead of our redo point (FirstLSN > redo start) or we have no reference chain, otherwise it is just our own chain's original
        /// first log re-appearing (older finish date pulled in by a reProcess walk-back) which is safe to skip.  As a safeguard for the
        /// destructive re-initialization, a chain is only ever considered broken when the log was genuinely taken after the last log we
        /// restored (BackupFinishDate) - protects against acting on a stale or out-of-order backup file.
        /// </summary>
        internal static LogChainStatus GetLogChainStatus(BackupHeader header, DateTime? currentChainCreationDate, DateTime referenceBackupFinishDate, BigInteger? redoStart)
        {
            if (header.BackupFinishDate <= referenceBackupFinishDate)
            {
                return LogChainStatus.Ok; // Not newer than the last log we restored - don't treat as a broken chain
            }
            if (currentChainCreationDate.HasValue && header.DatabaseCreationDate > currentChainCreationDate.Value)
            {
                return LogChainStatus.Recreated;
            }
            if (header.BeginsLogChain && (redoStart == null || header.FirstLSN > redoStart || !currentChainCreationDate.HasValue))
            {
                return LogChainStatus.Broken;
            }
            return LogChainStatus.Ok;
        }

        private static void LogBrokenChainDetected(LogChainStatus status, string sourceDb, string logPath, BackupHeader header, DateTime? currentChainCreationDate, BigInteger? redoStart)
        {
            if (status == LogChainStatus.Recreated)
            {
                Log.Warning("Detected that source database {sourceDb} has been re-created (dropped & re-created with the same name).  {logPath} belongs to a newer incarnation - DatabaseCreationDate: {HeaderCreateDate} is newer than the current chain: {ChainCreateDate}.  BeginsLogChain: {BeginsLogChain}, FirstLSN: {FirstLSN}, LastLSN: {LastLSN}",
                    sourceDb, logPath, header.DatabaseCreationDate, currentChainCreationDate, header.BeginsLogChain, header.FirstLSN, header.LastLSN);
            }
            else
            {
                Log.Warning("Detected a broken log chain for source database {sourceDb} (e.g. the recovery model was switched to SIMPLE and back to FULL).  {logPath} begins a new log chain that cannot be applied - BeginsLogChain: {BeginsLogChain}, FirstLSN: {FirstLSN}, LastLSN: {LastLSN}, required LSN: {RequiredLSN}",
                    sourceDb, logPath, header.BeginsLogChain, header.FirstLSN, header.LastLSN, redoStart);
            }
        }

        private static bool ProcessRestoreCommand(string sql, string db, string file)
        {
            try
            {
                Execute(sql);
                return true;
            }
            catch (SqlException ex) when
                (ex.Number == 4326) // Log file is too early to apply, Log error and continue
            {
                Log.Warning(ex, "Log file is too early to apply. Processing will continue with next file.");
            }
            catch (SqlException ex) when
                (ex.Number == 3203) // Read error.  Damaged backup? Log error and continue processing.
            {
                Log.Error(ex, "Error reading backup file {file} - possible damaged or incomplete backup.  Processing will continue with next file.", file);
            }
            catch (SqlException ex) when
                (ex.Number == 3101) // Exclusive access could not be obtained because the database is in use.  Kill user connections and retry.
            {
                if (!KillUserConnections(db)) return false;
                Execute(sql);
                return true;
            }
            catch (SqlException ex) when (ex.Number == 4319)
            {
                Log.Warning(ex,
                    "A previous restore operation was interrupted for {targetDb}.  Attempting to fix automatically with RESTART option", db);
                sql += ",RESTART";
                try
                {
                    Execute(sql);
                    return true;
                }
                catch (Exception ex2)
                {
                    Log.Error(ex2, "Error running RESTORE with RESTART option. {sql}. Skipping file and trying next in sequence.", sql);
                }
            }

            return false;
        }

        internal static bool KillUserConnections(string db)
        {
            if (Config.KillUserConnections)
            {
                var sql = $"IF DATABASEPROPERTYEX({db.SqlSingleQuote()},'IsInStandBy')=1\n";
                sql += "BEGIN\n";
                sql += $"\tALTER DATABASE {db.SqlQuote()} SET SINGLE_USER WITH ROLLBACK AFTER {Config.KillUserConnectionsWithRollbackAfter}\n";
                sql += $"\tRESTORE DATABASE {db.SqlQuote()} WITH NORECOVERY\n";
                sql += "END\n";
                Log.Warning("User connections to {targetDb} are preventing restore operations.  Sessions will be killed after {seconds}. {sql}", db, Config.KillUserConnectionsWithRollbackAfter, sql);
                try
                {
                    Execute(sql);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error killing user connections for {targetDb}. {sql}", db, sql);
                    return false;
                }
            }
            else
            {
                Log.Error("User connections to {targetDb} are preventing restore operations. Consider enabling KillUserConnections in config");
                return false;
            }
        }

        private static void RestoreWithStandby(string db)
        {
            if (string.IsNullOrEmpty(Config.StandbyFileName)) return;
            var standby = Config.StandbyFileName.Replace(Config.DatabaseToken, db);
            var sql = $"IF DATABASEPROPERTYEX({db.SqlSingleQuote()},'IsInStandBy') = 0 RESTORE DATABASE {db.SqlQuote()} WITH STANDBY = {standby.SqlSingleQuote()}";
            try
            {
                Execute(sql);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error running {sql}", sql);
            }
        }

        private static void Execute(string sql)
        {
            DataHelper.ExecuteWithTiming(sql, Config.Destination);
        }

        public static DataTable GetDatabases()
        {
            using var cn = new SqlConnection(Config.Destination);
            using var cmd = new SqlCommand(SqlStrings.GetDatabases, cn) { CommandTimeout = 0 };
            using var da = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            da.Fill(dt);
            return dt;
        }

        private static IEnumerable<BackupFile> GetFilesForDb(string db, DateTime fromDate)
        {
            var path = Config.LogFilePath!.Replace(Config.DatabaseToken, db);
            IEnumerable<BackupFile> logFiles;

            using (var op = Operation.Begin("Get logs for {DatabaseName} after {date} (Offset:{offset}) from {path}", db,
                       fromDate, Config.OffsetMins, path))
            {
                logFiles = FileHandler.FileHandlerInstance.GetFiles(path, "*.trn", fromDate, true);
                op.Complete();
            }

            return logFiles;
        }

        // Snapshot current monitoring state
        private static (long Enqueued, long EnqueueSkipAlreadyEnqueued, long EnqueueSkipInProgress, long Dequeued, long Processed, long Errors, int InProgressCount) GetStats()
        {
            return (
                Enqueued: Interlocked.Read(ref _enqueuedCount),
                EnqueueSkipAlreadyEnqueued: Interlocked.Read(ref _enqueueSkipAlreadyEnqueuedCount),
                EnqueueSkipInProgress: Interlocked.Read(ref _enqueueSkipInProgressCount),
                Dequeued: Interlocked.Read(ref _dequeuedCount),
                Processed: Interlocked.Read(ref _processedCount),
                Errors: Interlocked.Read(ref _errorCount),
                InProgressCount: InProgress.Count
            );
        }
    }
}