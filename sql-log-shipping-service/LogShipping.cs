﻿using LogShippingService.FileHandling;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Serilog;
using SerilogTimings;
using System.Collections.Concurrent;
using System.Data;
using System.Numerics;

namespace LogShippingService
{
    internal class LogShipping : BackgroundService
    {
        public static ConcurrentDictionary<string, string> InitializingDBs = new();

        private readonly DatabaseInitializerBase? _initializer;

        private static readonly object locker = new();

        private static Config Config => AppConfig.Config;

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
            var logRestoreTask = StartProcessing(stoppingToken);

            try
            {
                if (_initializer != null)
                {
                    await Task.WhenAll(logRestoreTask, _initializer.RunPollForNewDBs(stoppingToken));
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
        }

        private async Task StartProcessing(CancellationToken stoppingToken)
        {
            long i = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForNextIteration(i, stoppingToken);
                i++;
                using (Operation.Time($"Log restore iteration {i}"))
                {
                    Log.Information("Starting log restore iteration {0}", i);
                    try
                    {
                        await Process(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Unexpected error processing log restores");
                    }
                }
            }
            Log.Information("Finished processing LOG restores");
        }

        /// <summary>
        /// Wait for the required time before starting the next iteration.  Either a delay in milliseconds or a cron schedule can be used.  Also waits until active hours if configured.
        /// </summary>
        private static async Task WaitForNextIteration(long count, CancellationToken stoppingToken)
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
                await Waiter.WaitUntilTime(nextIterationStart, stoppingToken);
            }
            // If active hours are configured, wait until the next active period
            await Waiter.WaitUntilActiveHours(stoppingToken);
        }

        public void Stop()
        {
            Log.Information("Initiating shutdown...");
        }

        private Task Process(CancellationToken stoppingToken)
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

            Parallel.ForEach(dt.AsEnumerable(), new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads }, row =>
            {
                if (stoppingToken.IsCancellationRequested || !Waiter.CanRestoreLogsNow) return;
                var targetDb = (string)row["Name"];
                var sourceDb = DatabaseInitializerBase.GetSourceDatabaseName(targetDb);
                if (InitializingDBs.ContainsKey(targetDb.ToLower()))
                {
                    Log.Information("Skipping log restores for {targetDb} due to initialization", targetDb);
                    return;
                }
                var fromDate = row["backup_finish_date"] as DateTime? ?? DateTime.MinValue;
                fromDate = fromDate.AddMinutes(Config.OffsetMins);
                try
                {
                    ProcessDatabase(sourceDb, targetDb, fromDate, stoppingToken);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error processing database {targetDb}", targetDb);
                }
            });
            return Task.CompletedTask;
        }

        public static bool IsIncludedDatabase(string db)
        {
            var isExcluded = Config.ExcludedDatabases.Count > 0 && Config.ExcludedDatabases.Any(e => e.Equals(db, StringComparison.OrdinalIgnoreCase));
            var isIncluded = Config.IncludedDatabases.Count == 0 || Config.IncludedDatabases.Any(e => e.Equals(db, StringComparison.OrdinalIgnoreCase));

            return !isExcluded && isIncluded;
        }

        private void ProcessDatabase(string sourceDb, string targetDb, DateTime fromDate, CancellationToken stoppingToken, int processCount = 1, bool reProcess = false)
        {
            var expectedTarget = DatabaseInitializerBase.GetDestinationDatabaseName(sourceDb);
            if (!string.Equals(expectedTarget, targetDb, StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("Skipping {targetDb}. Expected target to be {expectedName}", targetDb, expectedTarget);
                return;
            }
            if (!IsIncludedDatabase(targetDb) && !IsIncludedDatabase(sourceDb))
            {
                Log.Debug("Skipping {targetDb}. Database is excluded.", targetDb);
                return;
            }
            var logFiles = GetFilesForDb(sourceDb, fromDate);
            using (var op = Operation.Begin("Restore Logs for {DatabaseName}", targetDb))
            {
                try
                {
                    RestoreLogs(logFiles, sourceDb, targetDb, reProcess, stoppingToken);
                    op.Complete();
                }
                catch (TimeoutException ex) when (ex.Message == "Max processing time exceeded")
                {
                    Log.Warning(
                        "Max processing time exceeded. Log processing will continue for {targetDb} on the next iteration.",
                        targetDb);
                    op.SetException(ex);
                }
                catch (SqlException ex) when (ex.Number == 4305)
                {
                    HandleTooRecent(ex, sourceDb, targetDb, fromDate, processCount, stoppingToken);
                }
                catch (HeaderVerificationException ex) when (ex.VerificationStatus ==
                                                             BackupHeader.HeaderVerificationStatus.TooRecent)
                {
                    HandleTooRecent(ex, sourceDb, targetDb, fromDate, processCount, stoppingToken);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error restoring logs for {targetDb}", targetDb);
                }
            }
        }

        private void HandleTooRecent(Exception ex, string sourceDb, string targetDb, DateTime fromDate, int processCount, CancellationToken stoppingToken)
        {
            switch (processCount)
            {
                // Too recent
                case 1:
                    Log.Warning(ex, "Log file to recent to apply.  Adjusting fromDate by 60min.");
                    ProcessDatabase(sourceDb, targetDb, fromDate.AddMinutes(-60), stoppingToken, processCount + 1, true);
                    break;

                case 2:
                    Log.Warning(ex, "Log file to recent to apply.  Adjusting fromDate by 1 day.");
                    ProcessDatabase(sourceDb, targetDb, fromDate.AddMinutes(-1440), stoppingToken, processCount + 1, true);
                    break;

                default:
                    Log.Error(ex, "Log file too recent to apply.  Manual intervention might be required.");
                    break;
            }
        }

        private static Task RestoreLogs(IEnumerable<BackupFile> logFiles, string sourceDb, string targetDb, bool reProcess, CancellationToken stoppingToken)
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

                        if (Config.RestoreDelayMins > 0 && DateTime.Now.Subtract(header.BackupFinishDate).TotalMinutes < Config.RestoreDelayMins)
                        {
                            Log.Information("Waiting to restore {logPath} & subsequent files.  Backup Finish Date: {BackupFinishDate}. Eligible for restore after {RestoreAfter}, RestoreDelayMins:{RestoreDelay}", logBackup.FilePath, header.BackupFinishDate, header.BackupFinishDate.AddMinutes(Config.RestoreDelayMins), Config.RestoreDelayMins);
                            breakProcessingFlag = true;
                            break;
                        }
                        else if (header.FirstLSN <= redoStartOrPreviousLastLSN && header.LastLSN == redoStartOrPreviousLastLSN)
                        {
                            if (reProcess) // Reprocess previous file if we got a too recent error, otherwise skip it
                            {
                                Log.Information("Re-processing {logPath}, FILE={Position}. FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logBackup.FilePath, header.Position, header.FirstLSN, header.LastLSN);
                                continue;
                            }
                            else
                            {
                                Log.Information("Skipping {logPath}, FILE={Position}. Found last log file restored.  FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logBackup.FilePath, header.Position, header.FirstLSN, header.LastLSN);
                                continue;
                            }
                        }
                        else if (header.FirstLSN <= redoStartOrPreviousLastLSN && header.LastLSN > redoStartOrPreviousLastLSN)
                        {
                            Log.Information("Header verification successful for {logPath}, FILE={Position}. FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logBackup.FilePath, header.Position, header.FirstLSN, header.LastLSN);
                        }
                        else if (header.FirstLSN < redoStartOrPreviousLastLSN)
                        {
                            earlierLogFound = true;
                            Log.Information("Skipping {logPath}.  A later LSN is required: {RequiredLSN}, FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logBackup.FilePath, redoStartOrPreviousLastLSN, header.FirstLSN, header.LastLSN);
                            continue;
                        }
                        else if (header.FirstLSN > redoStartOrPreviousLastLSN)
                        {
                            if (earlierLogFound && reProcess) 
                            {
                                // The current log is too recent.  We previously adjusted the search date looking for an earlier log.  Now we have found log files that are too early to apply, then this log that is too recent
                                // The log chain appears to be broken, but log an error and continue processing in case the file has a later modified date than expected.
                                Log.Error("Header verification failed for {FilePath}. An earlier LSN is required: {redoStartOrPreviousLastLSN}, FirstLSN: {FirstLSN}, LastLSN: {LastLSN}. NOTE: We previously found a log that was too early to apply.  Log chain might be broken, requiring manual intervention. Continuing to check log files just in case the file has a later modified date than expected.",
                                    logBackup.FilePath,redoStartOrPreviousLastLSN,header.FirstLSN,header.LastLSN);
                                continue;
                            }
                            throw new HeaderVerificationException($"Header verification failed for {logBackup.FilePath}.  An earlier LSN is required: {redoStartOrPreviousLastLSN}, FirstLSN: {header.FirstLSN}, LastLSN: {header.LastLSN}", BackupHeader.HeaderVerificationStatus.TooRecent);
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
                    }
                }
                else
                {
                    ProcessRestoreCommand(sql, targetDb, file);
                }
            }
            RestoreWithStandby(targetDb);
            return Task.CompletedTask;
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

        private static bool KillUserConnections(string db)
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
    }
}