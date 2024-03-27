using Cronos;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Serilog;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace LogShippingService
{
    internal static class Config
    {
        #region Azure
        /// <summary>Container URL to be used when restoring directly from Azure blob containers</summary>
        public static readonly string? ContainerURL;

        /// <summary>SAS Token be used to allow access to Azure blob container when restoring directly from Azure blob.</summary>
        public static readonly string? SASToken;

        #endregion

        #region BasicConfig

        public static readonly string ConnectionString;
        public static readonly string? LogFilePathTemplate;

        #endregion

        #region Schedule

        /// <summary>Delay between processing log restores in milliseconds</summary>
        public static readonly int IterationDelayMs;

        /// <summary>Cron schedule for log restores.  Overrides IterationDelayMs if specified </summary>
        public static string? LogRestoreScheduleCron; 

        /// <summary>Return if cron schedule should be used for log restores</summary>
        public static bool UseLogRestoreScheduleCron => !string.IsNullOrEmpty(LogRestoreScheduleCron) && LogRestoreCron!=null;

        /// <summary>Cron expression for generating next log restore time</summary>
        public static CronExpression? LogRestoreCron;

        /// <summary>Timezone offset to handle timezone differences if needed</summary>
        public static readonly int OffSetMins;

        /// <summary>Maximum amount of time to spend processing log restores for a single database in minutes.</summary>
        public static readonly int MaxProcessingTimeMins;

        /// <summary> Hours where log restores will run.  Default is all hours. 0..23 </summary>
        public static List<int> Hours;

        /// <summary>How often to poll for new databases in minutes</summary>
        public static int PollForNewDatabasesFrequency;

        /// <summary>Cron schedule for initializing new databases.  Overrides PollForNewDatabasesFrequency if specified</summary>
        public static string? PollForNewDatabasesCron;

        /// <summary>Cron expression for generating next database initialization time</summary>
        public static CronExpression? PollForNewDatabasesCronExpression;

        /// <summary>Return if cron schedule should be used for database initialization</summary>
        public static bool UsePollForNewDatabasesCron => !string.IsNullOrEmpty(PollForNewDatabasesCron) && PollForNewDatabasesCronExpression!=null;

        #endregion

        #region Standby

        /// <summary>Path to standby file which should contain {DatabaseName} token to be replaced with database name.  If null, standby will not be used.</summary>
        public static readonly string? StandbyFileName;

        /// <summary>Kill user connections to the databases to allow restores to proceed</summary>
        public static bool KillUserConnections;

        /// <summary>Killed user connections will be rolled back after the specified number of seconds.  Defaults to 60 seconds.</summary>
        public static int KillUserConnectionsWithRollBackAfter;

        #endregion

        #region Initialization

        /// <summary>Full backup path for initialization of new databases.  If null, initialization from disk will not be performed. e.g. \BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\FULL</summary>
        public static string? FullBackupPathTemplate;
        
        /// <summary>Diff backup path for initialization of new databases.  If null, initialization will not use diff backups. e.g. \BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\DIFF</summary>
        public static string? DiffBackupPathTemplate;
        
        /// <summary>List of databases to include in log shipping.  If empty, all databases will be included.</summary>
        public static List<string> IncludedDatabases;
        
        /// <summary>List of databases to exclude from log shipping.  If empty, all databases will be included.</summary>
        public static List<string> ExcludedDatabases;
        
        /// <summary>Source connection string for initialization of new databases from msdb.  Overrides FullBackupPathTemplate and DiffBackupPathTemplate if specified.</summary>
        public static string? SourceConnectionString;
        
        /// <summary>Option to initialize databases using simple recovery model.  These databases can't be used for log shipping but we might want to restore in case of disaster recovery.</summary>
        public static bool InitializeSimple;
        
        /// <summary>Max age of backups to use for initialization in days.  Defaults to 14 days. Prevents old backups been used to initialize. </summary>
        public static int MaxBackupAgeForInitialization;
        
        /// <summary>Path to move data files to after initialization.  If null, files will be restored to their original location</summary>
        public static string? MoveDataFolder;
        
        /// <summary>Path to move log files to after initialization.  If null, files will be restored to their original location</summary>
        public static string? MoveLogFolder;
        
        /// <summary>Path to move filestream folders to after initialization.  If null, folders will be restored to their original location</summary>
        public static string? MoveFileStreamFolder;
        
        /// <summary>ReadOnly partial backup path for initialization of new databases. </summary>
        public static string? ReadOnlyPartialBackupPathTemplate;
        
        /// <summary>Option to recover partial backups without readonly</summary>
        public static bool RecoverPartialBackupWithoutReadOnly;
        
        /// <summary>Find part of find/replace for backup paths from msdb history.  e.g. Convert local paths to UNC paths</summary>
        public static string? MSDBPathFind;
        
        /// <summary>Replace part of find/replace for backup paths from msdb history.  e.g. Convert local paths to UNC paths</summary>
        public static string? MSDBPathReplace;

        #endregion

        #region OtherOptions

        /// <summary>Database token to be used</summary>
        public static readonly string DatabaseToken = "{DatabaseName}";
        /// <summary>Config file name</summary>
        private const string ConfigFile = "appsettings.json";
        /// <summary>Option to check headers.  Defaults to true</summary>
        public static bool CheckHeaders;
        /// <summary>Max number of threads to use for log restores and database initialization (each can use up to MaxThreads)</summary>
        public static readonly int MaxThreads;

        public static int RestoreDelayMins;

        public static DateTime StopAt;

        #endregion

        /// <summary>
        /// Read the json configuration file and set values
        /// </summary>
        static Config()
        {
            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile(ConfigFile)
                    .Build();

                // Read values from the configuration
                ContainerURL = configuration["Config:ContainerUrl"];
                SASToken = configuration["Config:SASToken"];
                StandbyFileName = configuration["Config:StandbyFileName"];
                KillUserConnections = bool.Parse(configuration["Config:KillUserConnections"] ?? true.ToString());
                KillUserConnectionsWithRollBackAfter = int.Parse(configuration["Config:KillUserConnectionsWithRollbackAfter"] ?? 60.ToString());
                IncludedDatabases = configuration.GetSection("Config:IncludedDatabases").Get<List<string>>() ?? new List<string>();
                ExcludedDatabases = configuration.GetSection("Config:ExcludedDatabases").Get<List<string>>() ?? new List<string>();
                SourceConnectionString = configuration["Config:SourceConnectionString"];
                PollForNewDatabasesFrequency = int.Parse(configuration["Config:PollForNewDatabasesFrequency"] ?? 10.ToString());
                CheckHeaders = bool.Parse(configuration["Config:CheckHeaders"] ?? true.ToString());
                Log.Information("Included {IncludedDBs}", IncludedDatabases);
                Log.Information("Excluded {ExcludedDBs}", ExcludedDatabases);
                Hours = configuration.GetSection("Config:Hours").Get<List<int>>() ?? new List<int>
                {
                    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
                    14, 15, 16, 17, 18, 19, 20, 21, 22, 23
                };
                if (Hours.Count != 24)
                {
                    Log.Information("Log Restores will run in these hours {hours}", Hours);
                }
                if (!string.IsNullOrEmpty(SASToken) && !EncryptionHelper.IsEncrypted(SASToken))
                {
                    if (!SASToken.StartsWith('?'))
                    {
                        Log.Information("Adding ? to SAS Token");
                        SASToken = "?" + SASToken;
                    }
                    Log.Information("Encrypting SAS Token");
                    Config.Update("Config", "SASToken", EncryptionHelper.EncryptWithMachineKey(SASToken));
                }
                else
                {
                    SASToken = EncryptionHelper.DecryptWithMachineKey(SASToken);
                }

                if (!string.IsNullOrEmpty(ConnectionString) && string.IsNullOrEmpty(SASToken))
                {
                    var message = "SASToken is required with ContainerUrl";
                    Log.Error(message);
                    throw new ArgumentException(message);
                }
                if (!string.IsNullOrEmpty(StandbyFileName) && (!Config.StandbyFileName.Contains(DatabaseToken)))
                {
                    Log.Error("Missing {DatabaseToken} from StandbyFileName", DatabaseToken);
                    throw new ArgumentException($"Missing {DatabaseToken} from StandbyFileName");
                }

                ConnectionString = configuration["Config:Destination"] ?? throw new InvalidOperationException();
                MaxThreads = int.Parse(configuration["Config:MaxThreads"] ?? 5.ToString());
                LogFilePathTemplate = configuration["Config:LogFilePath"];
                FullBackupPathTemplate = configuration["Config:FullFilePath"];
                DiffBackupPathTemplate = configuration["Config:DiffFilePath"];
                ReadOnlyPartialBackupPathTemplate = configuration["Config:ReadOnlyFilePath"];
                if (LogFilePathTemplate != null && !LogFilePathTemplate.Contains(DatabaseToken))
                {
                    throw new ValidationException("LogFilePathTemplate should contain '{DatabaseToken}'");
                }
                IterationDelayMs = int.Parse(configuration["Config:DelayBetweenIterationsMs"] ??
                                               60000.ToString());
                OffSetMins = int.Parse(configuration["Config:OffsetMins"] ?? 0.ToString());
                MaxProcessingTimeMins = int.Parse(configuration["Config:MaxProcessingTimeMins"] ??
                                                    60.ToString());
                InitializeSimple = bool.Parse(configuration["Config:InitializeSimple"] ?? false.ToString());
                MaxBackupAgeForInitialization = int.Parse(configuration["Config:MaxBackupAgeForInitialization"] ?? 14.ToString());
                MoveDataFolder = configuration["Config:MoveDataFolder"];
                MoveLogFolder = configuration["Config:MoveLogFolder"];
                MoveFileStreamFolder = configuration["Config:MoveFileStreamFolder"];
                RecoverPartialBackupWithoutReadOnly = bool.Parse(configuration["Config:RecoverPartialBackupWithoutReadOnly"] ?? false.ToString());
                MSDBPathFind = configuration["Config:MSDBPathFind"];
                MSDBPathReplace = configuration["Config:MSDBPathReplace"];
                LogRestoreScheduleCron = configuration["Config:LogRestoreScheduleCron"];
                RestoreDelayMins = int.Parse(configuration["Config:RestoreDelayMins"] ?? 0.ToString());
                StopAt = configuration["Config:StopAt"] ==null? DateTime.MaxValue : DateTime.Parse(configuration["Config:StopAt"]!);
                if (!string.IsNullOrEmpty(LogRestoreScheduleCron))
                {
                    try
                    {
                        LogRestoreCron = CronExpression.Parse(LogRestoreScheduleCron);
                        Log.Information("Using log restore Cron schedule: {cron}", LogRestoreScheduleCron);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error parsing LogRestoreScheduleCron");
                        throw;
                    }
                }
                PollForNewDatabasesCron = configuration["Config:PollForNewDatabasesCron"];
                if (!string.IsNullOrEmpty(PollForNewDatabasesCron))
                {
                    try
                    {
                        PollForNewDatabasesCronExpression= CronExpression.Parse(PollForNewDatabasesCron);
                        Log.Information("Initializing new databases on Cron schedule: {cron}", PollForNewDatabasesCron);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error parsing PollForNewDatabasesCron");
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error reading config");
                throw;
            }
        }

        /// <summary>
        /// Update the value of a key in the config file.  e.g. Replace SASToken with encrypted value
        /// </summary>
        /// <param name="section">Section.  e.g. Config</param>
        /// <param name="key">Key.  e.g. SASToken</param>
        /// <param name="value">New value for key</param>
        /// <exception cref="InvalidOperationException"></exception>
        private static void Update(string section, string key, string value)
        {
            var json = File.ReadAllText(ConfigFile);
            dynamic jsonObj = JsonConvert.DeserializeObject(json) ?? throw new InvalidOperationException();

            jsonObj[section][key] = value;

            string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
            File.WriteAllText(ConfigFile, output);
        }
    }
}