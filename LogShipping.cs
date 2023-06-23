using System.Collections.Concurrent;
using System.Data;
using System.Numerics;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Security;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Data.SqlClient;
using Serilog;
using SerilogTimings;

namespace LogShippingService
{
    internal class LogShipping
    {

        private bool _isStopRequested;
        private bool _isShutdown;
        public static ConcurrentDictionary<string,string> InitializingDBs = new();

        private readonly LogShippingInitializer _initializer = new();

        private readonly Waiter wait = new();
     
        public void Start()
        {
            Task.Run(StartProcessing);
            Task.Run(_initializer.PollForNewDBs);
        }

        private void StartProcessing()
        {
            long i = 1;
            while (!_isStopRequested)
            {
                if (!wait.WaitUntilActiveHours())
                {
                    break;
                }
                Log.Information("Starting iteration {0}",i);
                try
                {
                    Process();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unexpected error processing log restores");
                }

                var nextIterationStart = DateTime.Now.AddMilliseconds(Config.IterationDelayMs);
                Log.Information(
                    $"Iteration {i} Completed.  Next iteration will start at {nextIterationStart}");
                while (DateTime.Now < nextIterationStart && !_isStopRequested)
                {
                    Thread.Sleep(100);
                }
                i++;
            }
            Log.Information("Finished processing LOG restores");
            _isShutdown = true;
        }

 

        public void Stop()
        {
            Log.Information("Initiating shutdown...");
            _isStopRequested=true;
            _initializer.Stop();
            wait.Stop();
            while (!_isShutdown || !_initializer.IsStopped)
            {
                Thread.Sleep(100);
            }
            Log.Information("Shutdown complete.");
            Log.CloseAndFlush();

        }

        private void Process()
        {
            DataTable dt;
            using (Operation.Time("GetDatabases"))
            {
                dt = GetDatabases();
            }

            Parallel.ForEach(dt.AsEnumerable(), new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads }, row =>
            {
                if (!_isStopRequested && Waiter.CanRestoreLogsNow)
                {
                    var db = (string)row["Name"];
                    if (InitializingDBs.ContainsKey(db.ToLower()))
                    {
                        Log.Information("Skipping log restores for {db} due to initialization",db);
                        return;
                    }
                    var fromDate = row["backup_finish_date"] as DateTime? ?? DateTime.MinValue;
                    fromDate = fromDate.AddMinutes(Config.OffSetMins);
                    ProcessDatabase(db, fromDate);
                }
            });
        }

        public static bool IsIncludedDatabase(string db)
        {
            bool isExcluded = Config.ExcludedDatabases.Count > 0 && Config.ExcludedDatabases.Any(e => e.Equals(db, StringComparison.OrdinalIgnoreCase));
            bool isIncluded = Config.IncludedDatabases.Count == 0 || Config.IncludedDatabases.Any(e => e.Equals(db, StringComparison.OrdinalIgnoreCase));

            return !isExcluded && isIncluded;
        }

        private void ProcessDatabase(string db, DateTime fromDate,int processCount=1)
        {
            if (!IsIncludedDatabase(db))
            {
                Log.Debug("Skipping {db}. Database is excluded.",db);
                return;
            }
            var logFiles = GetFilesForDb(db, fromDate);
            using (var op = Operation.Begin("Restore Logs for {DatabaseName}", db))
            {
                try
                {
                    RestoreLogs(logFiles, db);
                    op.Complete();
                }
                catch (TimeoutException ex) when (ex.Message == "Max processing time exceeded")
                {
                    Log.Warning(
                        "Max processing time exceeded. Log processing will continue for {db} on the next iteration.",
                        db);
                    op.SetException(ex);
                }
                catch (SqlException ex) when (ex.Number == 4305)
                {
                    HandleTooRecent(ex,db,fromDate,processCount);
                }
                catch (HeaderVerificationException ex) when (ex.VerificationStatus ==
                                                             BackupHeader.HeaderVerificationStatus.TooRecent)
                {
                    HandleTooRecent(ex, db, fromDate, processCount);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error restoring logs for {db}", db);
                }
            }

        }

        private void HandleTooRecent(Exception ex,string db,DateTime fromDate, int processCount)
        {
            switch (processCount)
            {
                // Too recent
                case 1:
                    Log.Warning(ex, "Log file to recent to apply.  Adjusting fromDate by 60min.");
                    ProcessDatabase(db, fromDate.AddMinutes(-60), processCount + 1);
                    break;
                case 2:
                    Log.Warning(ex, "Log file to recent to apply.  Adjusting fromDate by 1 day.");
                    ProcessDatabase(db, fromDate.AddMinutes(-1440), processCount + 1);
                    break;
                default:
                    Log.Error(ex, "Log file too recent to apply.  Manual intervention might be required.");
                    break;
            }
        }

        private void RestoreLogs(List<string> logFiles, string db)
        {
            BackupHeader? header=null;
            BigInteger? redoStartOrPreviousLastLSN = null;
            if (Config.CheckHeaders)
            {
                redoStartOrPreviousLastLSN = DataHelper.GetRedoStartLSNForDB(db, Config.ConnectionString);
                Log.Debug("{db} Redo Start LSN: {RedoStartLSN}",db, redoStartOrPreviousLastLSN);
            }

            var maxTime = DateTime.Now.AddMinutes(Config.MaxProcessingTimeMins);
            foreach (var logPath in logFiles)
            {
                var file = logPath.SqlSingleQuote();
                var urlOrDisk = string.IsNullOrEmpty(Config.ContainerURL) ? "DISK" : "URL";
                var sql = $"RESTORE LOG {db.SqlQuote()} FROM {urlOrDisk} = {file} WITH NORECOVERY";

                if (Config.CheckHeaders)
                {
                    var headers = BackupHeader.GetHeaders(logPath, Config.ConnectionString,
                        string.IsNullOrEmpty(Config.ContainerURL)
                            ? BackupHeader.DeviceTypes.Disk
                            : BackupHeader.DeviceTypes.Url);
                    if (headers.Count > 1)
                    {
                        throw new Exception("File contains multiple backups");
                    }
                    header = headers[0];

                    if(header.DatabaseName != db)
                    {
                        throw new HeaderVerificationException(
                            $"Header verification failed for {logPath}.  Database: {header.DatabaseName}. Expected a backup for {db}", BackupHeader.HeaderVerificationStatus.WrongDatabase );
                    }
    
                    if (header.FirstLSN <= redoStartOrPreviousLastLSN && header.LastLSN >= redoStartOrPreviousLastLSN)
                    {
                        Log.Information("Header verification successful for {logPath}. FirstLSN: {FirstLSN}, LastLSN: {LastLSN}", logPath, header.FirstLSN, header.LastLSN);
                    }
                    else if (header.FirstLSN < redoStartOrPreviousLastLSN)
                    {
                        Log.Information("Skipping {logPath}.  A later LSN is required: {RequiredLSN}, FirstLSN: {FirstLSN}, LastLSN: {LastLSN}",logPath, redoStartOrPreviousLastLSN, header.FirstLSN,header.LastLSN);
                        continue;
                    }
                    else if (header.FirstLSN > redoStartOrPreviousLastLSN)
                    {
                        throw new HeaderVerificationException($"Header verification failed for {logPath}.  An earlier LSN is required: {redoStartOrPreviousLastLSN}, FirstLSN: {header.FirstLSN}, LastLSN: {header.LastLSN}", BackupHeader.HeaderVerificationStatus.TooRecent);
                    }
                }
          
                try
                {
                    Execute(sql);
                }
                catch (SqlException ex) when
                    (ex.Number == 4326) // Log file is too early to apply, Log error and continue
                {
                    Log.Warning(ex,"Log file is too early to apply. Processing will continue with next file.");
                }
                catch (SqlException ex) when
                    (ex.Number == 3203) // Read error.  Damaged backup? Log error and continue processing.
                {
                    Log.Error(ex,"Error reading backup file {file} - possible damaged or incomplete backup.  Processing will continue with next file.",file);
                }
                catch (SqlException ex) when
                    (ex.Number == 3101) // Exclusive access could not be obtained because the database is in use.  Kill user connections and retry.
                {
                    if (!KillUserConnections(db)) return;
                    Execute(sql);
                }
                catch (SqlException ex) when (ex.Number == 4319)
                {
                    Log.Warning(ex,
                        "A previous restore operation was interrupted for {db}.  Attempting to fix automatically with RESTART option",db);
                    sql += ",RESTART";
                    try
                    {
                        Execute(sql);
                    }
                    catch (Exception ex2)
                    {
                        Log.Error(ex2,"Error running RESTORE with RESTART option. {sql}. Skipping file and trying next in sequence.",sql);
                    }

                }
                
                if (DateTime.Now > maxTime) 
                {
                    // Stop processing logs if max processing time is exceeded. Prevents a single DatabaseName that has fallen behind from impacting other DBs
                    throw new TimeoutException("Max processing time exceeded");
                }

                if (_isStopRequested)
                {
                    Log.Information("Halt log restores for {db} due to stop request",db);
                    break;
                }

                if (!Waiter.CanRestoreLogsNow)
                {
                    Log.Information("Halt log restores for {db} due to Hours configuration", db);
                    break;
                }

                if (header!=null)
                {
                    redoStartOrPreviousLastLSN = header.LastLSN;
                }
            }
            RestoreWithStandby(db);
        }

        private static bool KillUserConnections(string db)
        {
            if (Config.KillUserConnections)
            {
                var sql = $"ALTER DATABASE {db.SqlQuote()} SET SINGLE_USER WITH ROLLBACK AFTER {Config.KillUserConnectionsWithRollBackAfter}";
                Log.Warning("User connections to {db} are preventing restore operations.  Sessions will be killed after {seconds}. {sql}", db, Config.KillUserConnectionsWithRollBackAfter, sql);
                try
                {
                    Execute(sql);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex,"Error killing user connections for {db}. {sql}",db,sql);
                    return false;
                }
            }
            else
            {
                Log.Error("User connections to {db} are preventing restore operations. Consider enabling KillUserConnections in config");
                return false;
            }
        }

        private static void RestoreWithStandby(string db)
        {
            if (string.IsNullOrEmpty(Config.StandbyFileName)) return;
            var standby = Config.StandbyFileName.Replace(Config.DatabaseToken,db);
            var sql = $"IF DATABASEPROPERTYEX({db.SqlSingleQuote()},'IsInStandBy') = 0 RESTORE DATABASE {db.SqlQuote()} WITH STANDBY = {standby.SqlSingleQuote()}";
            try
            {
                Execute(sql);
            }
            catch (Exception ex)
            {
                Log.Error(ex,"Error running {sql}",sql);
            }
 
        }

        private static void Execute(string sql)
        {
            DataHelper.ExecuteWithTiming(sql,Config.ConnectionString);
        }

        public static DataTable GetDatabases()
        {
            using var cn = new SqlConnection(Config.ConnectionString);
            using var cmd = new SqlCommand(SqlStrings.GetDatabases,cn) {CommandTimeout = 0};
            using var da = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            da.Fill(dt);
            return dt;
        }

 
        private static List<string> GetFilesForDb(string db, DateTime fromDate)
        {
            var path = Config.LogFilePathTemplate.Replace(Config.DatabaseToken, db);
            List<string> logFiles;
            if (string.IsNullOrEmpty(Config.ContainerURL))
            {
                using (var op = Operation.Begin("Get logs for {DatabaseName} after {date} (Offset:{offset}) from {path}", db,
                           fromDate, Config.OffSetMins, path))
                {
                    logFiles = GetFilesForDbUnc(path, fromDate);
                    op.Complete("FileCount", logFiles.Count);
                }
            }
            else
            {
                using (var op = Operation.Begin("Query Azure Blob for {DatabaseName} after {date} (Offset:{offset}):{prefix}", db, fromDate, Config.OffSetMins, path))
                {
                    logFiles = GetFilesForDbAzBlob(path, fromDate);
                    op.Complete("FileCount", logFiles.Count);
                }
            }
            return logFiles;
        }

        private static List<string> GetFilesForDbUnc(string path, DateTime fromDate)
        {
            var files = new DirectoryInfo(path)
                .GetFiles("*.trn", SearchOption.AllDirectories)  
                .Where(file => file.LastWriteTime > fromDate)  
                .Select(file => file.FullName)  
                .ToList();  

            return files;
        }

        private static List<string> GetFilesForDbAzBlob(string prefix, DateTime fromDate)
        {
            var containerUri = new Uri(Config.ContainerURL + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            var filteredBlobs = containerClient
                .GetBlobs(BlobTraits.Metadata, BlobStates.None, prefix)
                .Where(blobItem => blobItem.Properties.LastModified > fromDate)
                .OrderBy(blobItem => blobItem.Properties.LastModified)
                .Select(blobItem => Config.ContainerURL + "/" + blobItem.Name)
                .ToList();

            return filteredBlobs;
        }


    }
}
