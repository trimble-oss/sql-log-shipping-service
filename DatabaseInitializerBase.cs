using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace LogShippingService
{
    public abstract class DatabaseInitializerBase
    {
        protected abstract void PollForNewDBs();

        protected List<DatabaseInfo>? DestinationDBs;

        public bool IsStopRequested;
        private readonly Waiter wait = new();

        public void Stop()
        {
            wait.Stop();
            IsStopRequested = true;
        }

        public void WaitForShutdown()
        {
            if (!IsStopRequested)
            {
                throw new Exception("Stop hasn't been requested.");
            }
            while (!IsStopped)
            {
                Thread.Sleep(100);
            }
        }

        public bool IsStopped { get; private set; }

        public abstract bool IsValidated { get;}


        public bool IsValidForInitialization(string db)
        {
            if (DestinationDBs == null || DestinationDBs.Exists(d => string.Equals(d.Name, db, StringComparison.CurrentCultureIgnoreCase))) return false;
            var systemDbs = new[] { "master", "model", "msdb" };
            if (systemDbs.Any(s => s.Equals(db, StringComparison.OrdinalIgnoreCase))) return false;
            return LogShipping.IsIncludedDatabase(db);
        }

        public void RunPollForNewDBs()
        {
            if (!IsValidated)
            {
                IsStopped = true;
                return;
            }

            while (!IsStopRequested)
            {
                if (!wait.WaitUntilActiveHours())
                {
                    break;
                }
                try
                {
                    DestinationDBs = DatabaseInfo.GetDatabaseInfo(Config.ConnectionString);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting destination databases.");
                    break;
                }
                try
                {
                    PollForNewDBs();
                }
                catch (Exception ex)
                {
                    Log.Error(ex,"Error running poll for new DBs");
                }

                var nextIterationStart = DateTime.Now.AddMinutes(Config.PollForNewDatabasesFrequency);

                while (DateTime.Now < nextIterationStart && !IsStopRequested)
                {
                    Thread.Sleep(100);
                }
            }
            Log.Information("Poll for new DBs is shutdown");
            IsStopped = true;
        }

        protected static void ProcessRestore(string db, List<string> fullFiles, List<string> diffFiles, BackupHeader.DeviceTypes deviceType)
        {

            var fullHeader = BackupHeader.GetHeaders(fullFiles, Config.ConnectionString, deviceType);
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
            else if (!string.Equals(fullHeader[0].DatabaseName, db, StringComparison.CurrentCultureIgnoreCase))
            {
                Log.Error("Backup is for {db}.  Expected {expectedDB}. {fullFiles}", fullHeader[0].DatabaseName, db, fullFiles);
                return;
            }
            else if (fullHeader[0].RecoveryModel == "SIMPLE" && !Config.InitializeSimple)
            {
                Log.Warning("Skipping initialization of {db} due to SIMPLE recovery model. InitializeSimple can be set to alter this behaviour for disaster recovery purposes.", db);
                return;
            }

            var moves = DataHelper.GetFileMoves(fullFiles, deviceType, Config.ConnectionString, Config.MoveDataFolder, Config.MoveLogFolder,
                Config.MoveFileStreamFolder);
            var restoreScript = DataHelper.GetRestoreDbScript(fullFiles, db, deviceType,true,moves);
            // Restore FULL
            DataHelper.ExecuteWithTiming(restoreScript, Config.ConnectionString);

            if (diffFiles.Count <= 0) return;

            // Check header for DIFF
            var diffHeader =
                BackupHeader.GetHeaders(diffFiles, Config.ConnectionString, deviceType);

            if (diffHeader.Count>0 && diffHeader[0].DatabaseName == db &&
                diffHeader[0].DifferentialBaseLSN == fullHeader[0].DifferentialBaseLSN)
            {
                // Restore DIFF is applicable
                restoreScript = DataHelper.GetRestoreDbScript(diffFiles, db, deviceType,false);
                DataHelper.ExecuteWithTiming(restoreScript, Config.ConnectionString);
            }
        }
    }
}
