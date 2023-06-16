using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LogShippingService;
using Serilog;
using SerilogTimings;
using Topshelf;

namespace LogShippingTest
{
    internal class LogShipping
    {

        private bool isStopRequested;

        public LogShipping()
        {

        }

        public void Start()
        {
            Task.Run(StartProcessing);
        }

        private void StartProcessing()
        {
            long i = 1;
            while (!isStopRequested)
            {
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
                while (DateTime.Now < nextIterationStart && !isStopRequested)
                {
                    Thread.Sleep(100);
                }
                i++;
            }
            Log.Information("Shutdown complete.");
        }

        public void Stop()
        {
            Log.Information("Initiating shutdown...");
            isStopRequested=true;
        }

        private void Process()
        {
            DataTable dt;
            using (var op = Operation.Time("GetDatabases"))
            {
                dt = GetDatabases();
            }

            Parallel.ForEach(dt.AsEnumerable(), new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads }, row =>
            {
                if (!isStopRequested)
                {
                    var db = (string)row["Name"];
                    DateTime fromDate = row["backup_finish_date"] as DateTime? ?? DateTime.MinValue;
                    fromDate = fromDate.AddMinutes(Config.OffSetMins);
                    ProcessDatabase(db, fromDate);
                }
            });
        }

        private void ProcessDatabase(string db, DateTime fromDate,int processCount=1)
        {
            List<string> logFiles;
            var prefix = Config.LogFilePathTemplate.Replace(Config.DatabaseToken, db);
            using (var op = Operation.Begin("Query Azure Blob for {DB} after {date} (Offset:{offset}):{prefix}", db, fromDate, Config.OffSetMins, prefix))
            {
                logFiles = GetFilesForDb(prefix, fromDate);
                op.Complete("FileCount", logFiles.Count);
            }
            using (var op = Operation.Begin("Restore Logs for {DB}", db))
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
                            Log.Error(ex,"Log file too recent to apply.  Manual intervention might be required.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error restoring logs for {db}", db);
                }
            }
        }

        private void RestoreLogs(List<string> logFiles,string db)
        {
            var maxTime = DateTime.Now.AddMinutes(Config.MaxProcessingTimeMins);
            foreach (var url in logFiles)
            {
                var file = (Config.ContainerURL + "/" + url).SqlSingleQuote();
                var sql = $"RESTORE LOG {db.SqlQuote()} FROM URL = {file} WITH NORECOVERY";
                using (var op = Operation.Begin(sql))
                {
                    try
                    {
                        Execute(sql);
                        op.Complete();
                    }
                    catch (SqlException ex) when
                        (ex.Number == 4326) // Log file is too early to apply, Log error and continue
                    {
                        op.SetException(ex);
                    }
                    catch (SqlException ex) when
                        (ex.Number == 3203) // Read error.  Damaged backup? Log error and continue processing.
                    {
                        Log.Error(ex,"Error reading backup file {file} - possible damaged or incomplete backup.  Processing will continue with next file.",file);
                        op.SetException(ex);
                    }
                    catch (SqlException ex) when (ex.Number == 4319)
                    {
                        Log.Warning(ex,
                            "A previous restore operation was interrupted for {db}.  Attempting to fix automatically with RESTART option",db);
                        sql += ",RESTART";
                        Execute(sql);
                        op.Complete();
                    }
                }
                if (DateTime.Now > maxTime) 
                {
                    // Stop processing logs if max processing time is exceeded. Prevents a single DB that has fallen behind from impacting other DBs
                    throw new TimeoutException("Max processing time exceeded");
                }

                if (isStopRequested)
                {
                    Log.Information("Halt log restores for {db} due to stop request",db);
                    break;
                }
            }
        }

        private static void Execute(string sql)
        {
            using var cn = new SqlConnection(Config.ConnectionString);
            using var cmd = new SqlCommand(sql, cn) {CommandTimeout = 0};
            cn.Open();
            cmd.ExecuteNonQuery();
        }

        public DataTable GetDatabases()
        {
            using var cn = new SqlConnection(Config.ConnectionString);
            using var cmd = new SqlCommand(SqlStrings.GetDatabases,cn) {CommandTimeout = 0};
            using var da = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            da.Fill(dt);
            return dt;
        }

        private List<string> GetFilesForDb(string prefix, DateTime fromDate)
        {
            Uri containerUri = new Uri(Config.ContainerURL + Config.SASToken);
            BlobContainerClient containerClient = new BlobContainerClient(containerUri);

            List<string> filteredBlobs = containerClient
                .GetBlobs(BlobTraits.Metadata, BlobStates.None, prefix)
                .Where(blobItem => blobItem.Properties.LastModified > fromDate)
                .OrderBy(blobItem => blobItem.Properties.LastModified)
                .Select(blobItem => blobItem.Name)
                .ToList();

            return filteredBlobs;
        }


    }
}
