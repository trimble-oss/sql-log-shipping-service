using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CommandLine;

namespace LogShippingService
{
    internal class CommandLineOptions
    {
        [Option("Destination", Required = false, HelpText = "Target server connection string.  SQL Instance to restore transaction logs to.")]
        public string? Destination { get; set; }

        [Option("LogFilePath", Required = false, HelpText = @"Path for transaction logs.  Most include {DatabaseName} token.  Don't include trailing '\'.  e.g. \\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\FULL")]
        public string? LogFilePath { get; set; }

        [Option("SASToken", Required = false, HelpText = "SASToken for Azure blob.  Allows app to query for files in blob container.")]
        public string? SASToken { get; set; }

        [Option("ContainerUrl", Required = false, HelpText = "Azure blob container Url")]
        public string? ContainerUrl { get; set; }

        [Option("SourceConnectionString", Required = false, HelpText = "Source server connection string for database initialization (using msdb backup history).  Or use FullFilePath/DiffFilePath")]
        public string? SourceConnectionString { get; set; }

        [Option("MSDBPathFind", Required = false, HelpText = "Use MSDBPathFind/MSDBPathReplace to do a find/replace on the backup paths returned from msdb history.  e.g. Convert a local path to a UNC path ")]
        public string? MSDBPathFind { get; set; }

        [Option("MSDBPathReplace", Required = false, HelpText = "Use MSDBPathFind/MSDBPathReplace to do a find/replace on the backup paths returned from msdb history.  e.g. Convert a local path to a UNC path ")]
        public string? MSDBPathReplace { get; set; }

        [Option("FullFilePath", Required = false, HelpText = @"Full backup file path.  Used to initialize new databases.  Include {DatabaseName} token in the path. Don't include trailing '\'. e.g. \\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\FULL")]
        public string? FullFilePath { get; set; }

        [Option("DiffFilePath", Required = false, HelpText = @"Diff backup file path.  Use with FullFilePath to initialize new databases. Include {DatabaseName} token in the path. Don't include trailing '\'. e.g. \\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\DIFF")]
        public string? DiffFilePath { get; set; }

        [Option("ReadOnlyFilePath", Required = false, HelpText = @"Read only backup file path.  Used to initialize new databases for databases with readonly filegroups & partial backups.  Include {DatabaseName} token in the path. e.g. \\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\READONLY")]
        public string? ReadOnlyFilePath { get; set; }

        [Option("RecoverPartialBackupWithoutReadOnly", Required = false, HelpText = @"Restore operation using partial backups will continue without readonly filegroups with this set to true.")]
        public bool? RecoverPartialBackupWithoutReadOnly { get; set; }

        [Option("PollForNewDatabasesFrequency", Required = false, HelpText = "Frequency in minutes to poll for new databases.")]
        public int? PollForNewDatabasesFrequency { get; set; }

        [Option("PollForNewDatabasesCron", Required = false, HelpText = "Cron expression.  Frequency to poll for new databases.")]
        public string? PollForNewDatabasesCron { get; set; }

        [Option("MaxBackupAgeForInitialization", Required = false, HelpText = "Max age (days) of backups to use for database initialization")]
        public int? MaxBackupAgeForInitialization { get; set; }

        [Option("MoveDataFolder", Required = false, HelpText = @"Option to move data files to a new location.  e.g. D:\Data")]
        public string? MoveDataFolder { get; set; }

        [Option("MoveLogFolder", Required = false, HelpText = @"Option to move log files to a new location. e.g. L:\Log")]
        public string? MoveLogFolder { get; set; }

        [Option("MoveFileStreamFolder", Required = false, HelpText = @"Option to move FILESTREAM to a new location. e.g. F:\FileStream")]
        public string? MoveFileStreamFolder { get; set; }

        [Option("InitializeSimple", Required = false, HelpText = @"Databases in SIMPLE recovery model are excluded by default.")]
        public bool? InitializeSimple { get; set; }

        [Option("IncludedDatabases", Required = false, HelpText = @"List of databases to include.  All other databases are excluded. e.g. --IncludedDatabases ""DB1"" ""DB2""")]
        public IEnumerable<string>? IncludedDatabases { get; set; }

        [Option("IncludeDatabase", Required = false, HelpText = @"Add a database to the list of included databases. e.g. --IncludeDatabase ""DB1""")]
        public string? IncludeDatabase { get; set; }

        [Option("ExcludedDatabases", Required = false, HelpText = @"List of databases to exclude. e.g. --ExcludedDatabases ""DB1"" ""DB2""")]
        public IEnumerable<string>? ExcludedDatabases { get; set; }

        [Option("ExcludeDatabase", Required = false, HelpText = @"Add a database to the list of excluded databases. e.g. --ExcludeDatabase ""DB1""")]
        public string? ExcludeDatabase { get; set; }

        [Option("OffsetMins", Required = false, HelpText = @"Offset to deal with timezone differences.")]
        public int? OffsetMins { get; set; }

        [Option("CheckHeaders", Required = false, HelpText = @"Check headers of backup files before restore.")]
        public bool? CheckHeaders { get; set; }

        [Option("RestoreDelayMins", Required = false, HelpText = @"Minimum age of backup file before it is restored.  A delay can be useful for recovering from application/user errors.")]
        public int? RestoreDelayMins { get; set; }

        [Option("StopAt", Required = false, HelpText = @"Point in time restore to the specified date/time. ")]
        public DateTime? StopAt { get; set; }

        [Option("DelayBetweenIterationsMs", Required = false, HelpText = @"Log restore operations will repeat on this schedule.  Or use LogRestoreScheduleCron")]
        public int? DelayBetweenIterationsMs { get; set; }

        [Option("LogRestoreScheduleCron", Required = false, HelpText = @"Cron expression. Log restore operations will repeat on this schedule. Or use DelayBetweenIterationsMs.")]
        public string? LogRestoreScheduleCron { get; set; }

        [Option("MaxThreads", Required = false, HelpText = @"Max number of threads to use for restore operations.")]
        public int? MaxThreads { get; set; }

        [Option("Hours", Required = false, HelpText = @"Hours that log restores are allowed to run. Set to -1 to include ALL hours (default).  e.g. --Hours 0 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23")]
        public IEnumerable<int>? Hours { get; set; }

        [Option("StandbyFileName", Required = false, HelpText = @"Option to bring database online in STANDBY mode.  Set path to file including {DatabaseName} token. e.g. --StandbyFileName ""D:\Standby\{DatabaseName}_Standby.BAK""")]
        public string? StandbyFileName { get; set; }

        [Option("KillUserConnections", Required = false, HelpText = @"Kill user connections before restore.  For use with STANDBY so open connections don't prevent restore operations.  Default: true.")]
        public bool? KillUserConnections { get; set; }

        [Option("KillUserConnectionsWithRollbackAfter", Required = false, HelpText = @"'WITH ROLLBACK AFTER' option for killing user connections.  Default 60 seconds.")]
        public int? KillUserConnectionsWithRollbackAfter { get; set; }

        [Option("MaxProcessingTimeMins", Required = false, HelpText = @"Max time in minutes to spend processing an individual database each iteration.")]
        public int? MaxProcessingTimeMins { get; set; }

        [Option("AccessKey", Required = false, HelpText = @"S3 Access Key - use when log shipping from a S3 bucket or leave blank to use instance profile credentials")]
        public string? AccessKey { get; set; }

        [Option("SecretKey", Required = false, HelpText = @"S3 Secret Key - use when log shipping from a S3 bucket or leave blank to use instance profile credentials")]
        public string? SecretKey { get; set; }

        [Option("Run", Required = false, HelpText = "Run without saving changes to the config")]
        public bool Run { get; set; }
    }
}