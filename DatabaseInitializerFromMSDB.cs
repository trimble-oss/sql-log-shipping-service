using Serilog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using SerilogTimings;
using System.ServiceProcess;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders.Physical;

namespace LogShippingService
{
    public class DatabaseInitializerFromMSDB: DatabaseInitializerBase
    {
        public override bool IsValidated => !string.IsNullOrEmpty(Config.SourceConnectionString);

        /// <summary>
        /// Check for new DBs in the source connection that don't exist in the destination.  
        /// </summary>
        protected override void PollForNewDBs()
        {
            List<DatabaseInfo> newDBs;
            using (Operation.Time("Polling for new databases using msdb history"))
            {
                newDBs = GetNewDatabases();
            }

            Log.Information("NewDBs:{Count}", newDBs.Count);
            Parallel.ForEach(newDBs.AsEnumerable(),
                new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads },
                newDb =>
                {
                      ProcessDB(newDb.Name);
                });
        }

        /// <summary>
        /// Get the last FULL/DIFF backup for the database from msdb history & restore
        /// </summary>
        /// <param name="db">Database name</param>
        protected override void DoProcessDB(string db)
        {
            if (Config.SourceConnectionString == null) return;
            Log.Information("Initializing new database: {db}", db);
            var lastFull = new LastBackup(db, Config.SourceConnectionString, BackupHeader.BackupTypes.DatabaseFull );
            var lastDiff = new LastBackup(db, Config.SourceConnectionString, BackupHeader.BackupTypes.DatabaseDiff );
            ReplacePaths(ref lastFull); // Replace paths if necessary.  e.g. Convert local path to UNC path
            ReplacePaths(ref lastDiff); // Replace paths if necessary.  e.g. Convert local path to UNC path
            if (lastFull.FileList.Count == 0)
            {
                Log.Error("No backups available to initialize {db}", db);
                return;
            }

            Log.Debug("Last full for {db}: {lastFull}",db,lastFull.BackupFinishDate);
            Log.Debug("Last diff for {db}: {lastDiff}", db, lastDiff.BackupFinishDate);

            var fullHeader = lastFull.GetHeader(Config.ConnectionString);

            lastFull.Restore();

            // Check if diff backup should be applied
            if (lastDiff.BackupFinishDate <= lastFull.BackupFinishDate) return;

            var diffHeader = lastDiff.GetHeader(Config.ConnectionString);
            if (IsDiffApplicable(fullHeader,diffHeader))
            {
                lastDiff.Restore();
            }
        }


        /// <summary>
        /// Replace paths if path replacement is configured.  e.g. Convert local path to UNC path
        /// </summary>
        /// <param name="backup"></param>
        private static void ReplacePaths(ref LastBackup backup)
        {
            if(Config.MSDBPathFind==null || Config.MSDBPathReplace==null) return;
            for (var i=0; i<backup.FileList.Count; i++)
            {
                backup.FileList[i] = backup.FileList[i].Replace(Config.MSDBPathFind, Config.MSDBPathReplace);
            }
        }

        
       /// <summary>
       /// Get a list of databases that exist in the source connection that don't exist in the destination.   Only include ONLINE databases with FULL or BULK LOGGED recovery model
       /// </summary>
       /// <returns></returns>
       private List<DatabaseInfo> GetNewDatabases()
        {
            if (Config.SourceConnectionString == null) return new List<DatabaseInfo>();
            if(DestinationDBs==null) return new List<DatabaseInfo>();

            var sourceDBs = DatabaseInfo.GetDatabaseInfo(Config.SourceConnectionString);

            sourceDBs = sourceDBs.Where(db => (db.RecoveryModel is 1 or 2 || Config.InitializeSimple) && db.State == 0).ToList();

            var newDBs = sourceDBs.Where(db =>
                !DestinationDBs.Any(destDb => destDb.Name.Equals(db.Name, StringComparison.OrdinalIgnoreCase))).ToList();

            return newDBs;
        }
    }
}
