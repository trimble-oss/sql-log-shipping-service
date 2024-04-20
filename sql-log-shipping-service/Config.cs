using Cronos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.ComponentModel.DataAnnotations;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using CommandLine;
using Microsoft.Extensions.Azure;
using static System.Collections.Specialized.BitVector32;
using static LogShippingService.FileHandling.FileHandler;

namespace LogShippingService
{
    [JsonObject()]
    public class Config
    {
        #region "Constants"

        private const int DelayBetweenIterationsMsDefault = 60000;
        private const int MaxThreadsDefault = 5;
        private const int MaxProcessingTimeMinsDefault = 60;
        private const int MaxBackupAgeForInitializationDefault = 14;
        private const int PollForNewDatabasesFrequencyDefault = 10;
        private const int KillUserConnectionsWithRollbackAfterDefault = 60;
        private bool _encryptionRequired = false;
        private char[] invalidPathChars => Path.GetInvalidPathChars().Concat(new[] { '"' }).ToArray();

        [JsonIgnore]
        public bool EncryptionRequired => _encryptionRequired;

        /// <summary>Database token to be used</summary>
        [JsonIgnore]
        public const string DatabaseToken = "{DatabaseName}";

        /// <summary>Config file path</summary>
        public static string ConfigFile => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        #endregion "Constants"

        #region "File Handling"

        public enum FileHandlerTypes
        {
            Disk,
            AzureBlob,
            S3
        }

        [JsonIgnore]
        public FileHandlerTypes FileHandlerType
        {
            get
            {
                if (LogFilePath != null && LogFilePath.StartsWith("s3://"))
                {
                    return FileHandlerTypes.S3;
                }
                else if (!string.IsNullOrEmpty(ContainerUrl) && !string.IsNullOrEmpty(SASToken))
                {
                    return FileHandlerTypes.AzureBlob;
                }
                else
                {
                    return FileHandlerTypes.Disk;
                }
            }
        }

        [JsonIgnore]
        public BackupHeader.DeviceTypes DeviceType => FileHandlerType == FileHandlerTypes.Disk ? BackupHeader.DeviceTypes.Disk : BackupHeader.DeviceTypes.Url;

        #endregion "File Handling"

        #region Azure

        /// <summary>Container URL to be used when restoring directly from Azure blob containers</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? ContainerUrl { get; set; }

        private string? _sasToken;

        /// <summary>SAS Token be used to allow access to Azure blob container when restoring directly from Azure blob.</summary>
        [JsonIgnore]
        public string? SASToken
        {
            get => _sasToken;
            set => SetSASToken(value);
        }

        private void SetSASToken(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                _sasToken = null;
            }
            else if (EncryptionHelper.IsEncrypted(value))
            {
                _sasToken = EncryptionHelper.DecryptWithMachineKey(value);
            }
            else
            {
                _sasToken = value.StartsWith("?") ? value : "?" + value;
                _encryptionRequired = true;
            }
        }

        [JsonProperty("SASToken", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? SASTokenEncrypted
        {
            get => !string.IsNullOrEmpty(SASToken) ? EncryptionHelper.EncryptWithMachineKey(SASToken) : null;
            set => SetSASToken(value);
        }

        #endregion Azure

        #region S3

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? AccessKey { get; set; }

        [JsonIgnore]
        public string? SecretKey
        {
            get => _secretKey;
            set => SetSecretKey(value);
        }

        private string? _secretKey;

        [JsonProperty("SecretKey", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? SecretKeyEncrypted
        {
            get => !string.IsNullOrEmpty(SecretKey) ? EncryptionHelper.EncryptWithMachineKey(SecretKey) : null;
            set => SetSecretKey(value);
        }

        private void SetSecretKey(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                _secretKey = null;
            }
            else if (EncryptionHelper.IsEncrypted(value))
            {
                _secretKey = EncryptionHelper.DecryptWithMachineKey(value);
            }
            else
            {
                _secretKey = value;
                _encryptionRequired = true;
            }
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? BucketName { get; set; }

        #endregion S3

        #region BasicConfig

        private string _logFilePath = string.Empty;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string Destination { get; set; } = string.Empty;

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? LogFilePath
        {
            get => _logFilePath;
            set
            {
                if (!string.IsNullOrEmpty(value) && !value.Contains(DatabaseToken))
                {
                    throw new ArgumentException($"Missing {DatabaseToken} token from LogFilePath");
                }

                if (value != null && value.IndexOfAny(invalidPathChars) >= 0)
                {
                    throw new ArgumentException($"LogFilePath contains invalid characters: {value}");
                }
                _logFilePath = value ?? string.Empty;
            }
        }

        #endregion BasicConfig

        #region Schedule

        /// <summary>Delay between processing log restores in milliseconds</summary>
        public int DelayBetweenIterationsMs { get; set; } = DelayBetweenIterationsMsDefault;

        /// <summary>Cron schedule for log restores.  Overrides DelayBetweenIterationsMs if specified </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? LogRestoreScheduleCron
        {
            get => LogRestoreCron?.ToString();
            set => LogRestoreCron = value != null ? CronExpression.Parse(value) : null;
        }

        /// <summary>Return if cron schedule should be used for log restores</summary>
        [JsonIgnore]
        public bool UseLogRestoreScheduleCron => LogRestoreCron != null;

        /// <summary>Cron expression for generating next log restore time</summary>
        [JsonIgnore]
        public CronExpression? LogRestoreCron;

        /// <summary>Timezone offset to handle timezone differences if needed</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int OffsetMins { get; set; }

        /// <summary>Maximum amount of time to spend processing log restores for a single database in minutes.</summary>
        public int MaxProcessingTimeMins { get; set; } = MaxProcessingTimeMinsDefault;

        private HashSet<int> _hours = new();

        /// <summary> Hours where log restores will run.  Default is all hours. 0..23 </summary>
        public HashSet<int> Hours
        {
            get => _hours;
            set
            {
                if (value is { Count: > 24 })
                {
                    throw new ArgumentException("Too many arguments specified for Hours");
                }
                else if (value is { Count: 1 } && value.First() == -1)
                {
                    _hours = DefaultHours;
                }
                else if (value != null && value.Any(h => h is < 0 or > 23))
                {
                    throw new ArgumentException("Hours should have the values 0-23");
                }
                else
                {
                    _hours = value ?? DefaultHours;
                }
            }
        }

        public static readonly HashSet<int> DefaultHours = new()
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
            14, 15, 16, 17, 18, 19, 20, 21, 22, 23
        };

        public int PollForNewDatabasesFrequency { get; set; } = PollForNewDatabasesFrequencyDefault;

        /// <summary>Cron schedule for initializing new databases.  Overrides PollForNewDatabasesFrequency if specified</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? PollForNewDatabasesCron
        {
            get => PollForNewDatabasesCronExpression?.ToString();
            set => PollForNewDatabasesCronExpression = value != null ? CronExpression.Parse(value) : null;
        }

        /// <summary>Cron expression for generating next database initialization time</summary>
        [JsonIgnore]
        public CronExpression? PollForNewDatabasesCronExpression;

        /// <summary>Return if cron schedule should be used for database initialization</summary>
        [JsonIgnore]
        public bool UsePollForNewDatabasesCron => PollForNewDatabasesCronExpression != null;

        #endregion Schedule

        #region Standby

        private string? _standbyFileName;

        /// <summary>Path to standby file which should contain {DatabaseName} token to be replaced with database name.  If null, standby will not be used.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? StandbyFileName
        {
            get => _standbyFileName;
            set
            {
                if (!string.IsNullOrEmpty(value) && !value.Contains(DatabaseToken))
                {
                    throw new ArgumentException($"Missing {DatabaseToken} token from StandbyFileName");
                }
                _standbyFileName = value;
            }
        }

        /// <summary>Kill user connections to the databases to allow restores to proceed</summary>
        public bool KillUserConnections { get; set; } = true;

        /// <summary>Killed user connections will be rolled back after the specified number of seconds.  Defaults to 60 seconds.</summary>
        public int KillUserConnectionsWithRollbackAfter { get; set; } = KillUserConnectionsWithRollbackAfterDefault;

        #endregion Standby

        #region Initialization

        private string? _fullFilePath;
        private string? _diffFilePath;
        private string? _readOnlyFilePath;

        /// <summary>Full backup path for initialization of new databases.  If null, initialization from disk will not be performed. e.g. \BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\FULL</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? FullFilePath
        {
            get => _fullFilePath;
            set
            {
                if (!string.IsNullOrEmpty(value) && !value.Contains(DatabaseToken))
                {
                    throw new ArgumentException($"Missing {DatabaseToken} token from FullFilePath");
                }
                // Check if path contains invalid characters
                if (value != null && value.IndexOfAny(invalidPathChars) >= 0)
                {
                    throw new ArgumentException($"FullFilePath contains invalid characters: {value}");
                }
                _fullFilePath = value;
            }
        }

        /// <summary>Diff backup path for initialization of new databases.  If null, initialization will not use diff backups. e.g. \BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\DIFF</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? DiffFilePath
        {
            get => _diffFilePath;
            set
            {
                if (!string.IsNullOrEmpty(value) && !value.Contains(DatabaseToken))
                {
                    throw new ArgumentException($"Missing {DatabaseToken} token from DiffFilePath");
                }
                if (value != null && value.IndexOfAny(invalidPathChars) >= 0)
                {
                    throw new ArgumentException($"DiffFilePath contains invalid characters: {value}");
                }
                _diffFilePath = value;
            }
        }

        /// <summary>List of databases to include in log shipping.  If empty, all databases will be included.</summary>
        public HashSet<string> IncludedDatabases { get; set; } = new();

        /// <summary>List of databases to exclude from log shipping.  If empty, all databases will be included.</summary>
        public HashSet<string> ExcludedDatabases { get; set; } = new();

        /// <summary>Source connection string for initialization of new databases from msdb.  Overrides FullFilePath and DiffFilePath if specified.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? SourceConnectionString { get; set; }

        /// <summary>Option to initialize databases using simple recovery model.  These databases can't be used for log shipping but we might want to restore in case of disaster recovery.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool InitializeSimple { get; set; }

        /// <summary>Max age of backups to use for initialization in days.  Defaults to 14 days. Prevents old backups been used to initialize. </summary>
        public int MaxBackupAgeForInitialization { get; set; } = MaxBackupAgeForInitializationDefault;

        /// <summary>Path to move data files to after initialization.  If null, files will be restored to their original location</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? MoveDataFolder { get; set; }

        /// <summary>Path to move log files to after initialization.  If null, files will be restored to their original location</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? MoveLogFolder { get; set; }

        /// <summary>Path to move filestream folders to after initialization.  If null, folders will be restored to their original location</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? MoveFileStreamFolder { get; set; }

        /// <summary>ReadOnly partial backup path for initialization of new databases. </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? ReadOnlyFilePath
        {
            get => _readOnlyFilePath;
            set
            {
                if (!string.IsNullOrEmpty(value) && !value.Contains(DatabaseToken))
                {
                    throw new ArgumentException($"Missing {DatabaseToken} from ReadOnlyFilePath");
                }
                _readOnlyFilePath = value;
            }
        }

        /// <summary>Option to recover partial backups without readonly</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool RecoverPartialBackupWithoutReadOnly { get; set; }

        /// <summary>Find part of find/replace for backup paths from msdb history.  e.g. Convert local paths to UNC paths</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? MSDBPathFind { get; set; }

        /// <summary>Replace part of find/replace for backup paths from msdb history.  e.g. Convert local paths to UNC paths</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string? MSDBPathReplace { get; set; }

        #endregion Initialization

        #region OtherOptions

        /// <summary>Option to check headers.  Defaults to true</summary>
        public bool CheckHeaders { get; set; } = true;

        /// <summary>Max number of threads to use for log restores and database initialization (each can use up to MaxThreads)</summary>
        public int MaxThreads { get; set; } = MaxThreadsDefault;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int RestoreDelayMins { get; set; }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime StopAt { get; set; }

        #endregion OtherOptions

        #region Serialization

        public bool ShouldSerializeDelayBetweenIterationsMs()
        {
            return DelayBetweenIterationsMs != DelayBetweenIterationsMsDefault;
        }

        public bool ShouldSerializeCheckHeaders()
        {
            return CheckHeaders == false;
        }

        public bool ShouldSerializeIncludedDatabases()
        {
            return IncludedDatabases.Count != 0;
        }

        public bool ShouldSerializeExcludedDatabases()
        {
            return ExcludedDatabases.Count != 0;
        }

        public bool ShouldSerializeKillUserConnections()
        {
            return KillUserConnections == false;
        }

        public bool ShouldSerializeHours()
        {
            return Hours != DefaultHours && Hours.Count > 0;
        }

        public bool ShouldSerializeMaxThreads()
        {
            return MaxThreads != MaxThreadsDefault;
        }

        public bool ShouldSerializeMaxProcessingTimeMins()
        {
            return MaxProcessingTimeMins != MaxProcessingTimeMinsDefault;
        }

        public bool ShouldSerializeMaxBackupAgeForInitialization()
        {
            return MaxBackupAgeForInitialization != MaxBackupAgeForInitializationDefault;
        }

        public bool ShouldSerializePollForNewDatabasesFrequency()
        {
            return PollForNewDatabasesFrequency != PollForNewDatabasesFrequencyDefault;
        }

        public bool ShouldSerializeKillUserConnectionsWithRollbackAfter()
        {
            return KillUserConnectionsWithRollbackAfter != KillUserConnectionsWithRollbackAfterDefault;
        }

        #endregion Serialization

        public void ValidateConfig()
        {
            if (!string.IsNullOrEmpty(ContainerUrl) && string.IsNullOrEmpty(SASToken) && ContainerUrl.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase))
            {
                var message = "SASToken is required with ContainerUrl";
                Log.Error(message);
                throw new ArgumentException(message);
            }
            if (string.IsNullOrEmpty(Destination))
            {
                throw new ValidationException("Destination connection string should be configured");
            }

            if (string.IsNullOrEmpty(LogFilePath))
            {
                throw new ValidationException("LogFilePath should be configured");
            }
            if (AppConfig.Config.EncryptionRequired)
            {
                Log.Information("Saving config with encryption.");
                AppConfig.Config.Save();
            }
            if (_hours.Count == 0)
            {
                _hours = DefaultHours;
            }
        }

        #region CommandLine

        public bool ApplyCommandLineOptions(string[] args)
        {
            var serviceArgs = new[] { "-displayname", "-servicename" };
            if (args.Length == 0) return false;
            // We might have -displayname or -servicename as arguments when running as a service.  In this case we don't want to process the command line arguments
            if (args.Any(arg => serviceArgs.Contains(arg, StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }
            var cfg = AppConfig.Config;

            var errorCount = 0;
            var result = Parser.Default.ParseArguments<CommandLineOptions>(args)
                .WithParsed<CommandLineOptions>(opts =>
                    {
                        try
                        {
                            if (opts.Destination != null)
                            {
                                Destination = opts.Destination;
                            }

                            if (opts.LogFilePath != null)
                            {
                                LogFilePath = opts.LogFilePath;
                            }

                            if (opts.SASToken != null)
                            {
                                SASToken = opts.SASToken;
                            }

                            if (opts.SourceConnectionString != null)
                            {
                                SourceConnectionString = opts.SourceConnectionString;
                            }

                            if (opts.MSDBPathFind != null)
                            {
                                MSDBPathFind = opts.MSDBPathFind;
                            }

                            if (opts.MSDBPathReplace != null)
                            {
                                MSDBPathReplace = opts.MSDBPathReplace;
                            }

                            if (opts.FullFilePath != null)
                            {
                                FullFilePath = opts.FullFilePath;
                            }

                            if (opts.DiffFilePath != null)
                            {
                                DiffFilePath = opts.DiffFilePath;
                            }

                            if (opts.ReadOnlyFilePath != null)
                            {
                                ReadOnlyFilePath = opts.ReadOnlyFilePath;
                            }

                            if (opts.RecoverPartialBackupWithoutReadOnly != null)
                            {
                                RecoverPartialBackupWithoutReadOnly = (bool)opts.RecoverPartialBackupWithoutReadOnly;
                            }

                            if (opts.PollForNewDatabasesFrequency != null)
                            {
                                PollForNewDatabasesFrequency = (int)opts.PollForNewDatabasesFrequency;
                            }

                            if (opts.MaxBackupAgeForInitialization != null)
                            {
                                MaxBackupAgeForInitialization = (int)opts.MaxBackupAgeForInitialization;
                            }

                            if (opts.MoveDataFolder != null)
                            {
                                MoveDataFolder = opts.MoveDataFolder;
                            }

                            if (opts.MoveLogFolder != null)
                            {
                                MoveLogFolder = opts.MoveLogFolder;
                            }

                            if (opts.MoveFileStreamFolder != null)
                            {
                                MoveFileStreamFolder = opts.MoveFileStreamFolder;
                            }

                            if (opts.InitializeSimple != null)
                            {
                                InitializeSimple = (bool)opts.InitializeSimple;
                            }

                            if (opts.IncludedDatabases != null && args.Contains("--IncludedDatabases"))
                            {
                                IncludedDatabases = opts.IncludedDatabases.Where(dbs => !string.IsNullOrEmpty(dbs))
                                    .ToHashSet();
                            }

                            if (opts.ExcludedDatabases != null && args.Contains("--ExcludedDatabases"))
                            {
                                ExcludedDatabases = opts.ExcludedDatabases.Where(dbs => !string.IsNullOrEmpty(dbs))
                                    .ToHashSet();
                            }

                            if (opts.OffsetMins != null)
                            {
                                OffsetMins = (int)opts.OffsetMins;
                            }

                            if (opts.CheckHeaders != null)
                            {
                                CheckHeaders = (bool)opts.CheckHeaders;
                            }

                            if (opts.RestoreDelayMins != null)
                            {
                                RestoreDelayMins = (int)opts.RestoreDelayMins;
                            }

                            if (opts.StopAt != null)
                            {
                                StopAt = (DateTime)opts.StopAt;
                            }

                            if (opts.LogRestoreScheduleCron != null)
                            {
                                try
                                {
                                    LogRestoreScheduleCron = opts.LogRestoreScheduleCron;
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "Error parsing LogRestoreScheduleCron");
                                    errorCount++;
                                }

                                DelayBetweenIterationsMs = DelayBetweenIterationsMsDefault;
                            }

                            if (opts.DelayBetweenIterationsMs != null)
                            {
                                DelayBetweenIterationsMs = (int)opts.DelayBetweenIterationsMs;
                                LogRestoreScheduleCron = null;
                            }

                            if (opts.MaxThreads != null)
                            {
                                MaxThreads = (int)opts.MaxThreads;
                            }

                            if (opts.ContainerUrl != null)
                            {
                                ContainerUrl = opts.ContainerUrl;
                            }

                            if (opts.PollForNewDatabasesCron != null)
                            {
                                try
                                {
                                    PollForNewDatabasesCron = opts.PollForNewDatabasesCron;
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "Error parsing PollForNewDatabasesCron");
                                    errorCount++;
                                }
                            }

                            if (opts.IncludeDatabase != null)
                            {
                                IncludedDatabases.Add(opts.IncludeDatabase);
                            }

                            if (opts.ExcludeDatabase != null)
                            {
                                ExcludedDatabases.Add(opts.ExcludeDatabase);
                            }

                            if (opts.Hours != null && opts.Hours.Any())
                            {
                                Hours = opts.Hours.ToHashSet();
                            }

                            if (opts.StandbyFileName != null)
                            {
                                StandbyFileName = opts.StandbyFileName;
                            }

                            if (opts.KillUserConnections != null)
                            {
                                KillUserConnections = (bool)opts.KillUserConnections;
                            }

                            if (opts.KillUserConnectionsWithRollbackAfter != null)
                            {
                                KillUserConnectionsWithRollbackAfter = (int)opts.KillUserConnectionsWithRollbackAfter;
                            }

                            if (opts.MaxProcessingTimeMins != null)
                            {
                                MaxProcessingTimeMins = (int)opts.MaxProcessingTimeMins;
                            }

                            if (opts.AccessKey != null)
                            {
                                AccessKey = opts.AccessKey;
                            }

                            if (opts.SecretKey != null)
                            {
                                SecretKey = opts.SecretKey;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error parsing command line options");
                            errorCount++;
                        }
                    }
                );

            if (errorCount == 0 && result.Tag == ParserResultType.Parsed)
            {
                Save();
                Console.WriteLine("Configuration updated:");
                Console.WriteLine(File.ReadAllText(ConfigFile));
                Log.Information("Configuration updated.  Restart the service.");
                Environment.Exit(0);
            }
            else if (result.Errors.Any(ex => ex is HelpRequestedError))
            {
                Console.WriteLine("Current Config:");
                Console.WriteLine(File.ReadAllText(ConfigFile));
                Environment.Exit(0);
            }
            else if (result.Errors.Any(ex => ex is VersionRequestedError))
            {
                Environment.Exit(0);
            }
            else
            {
                Log.Error("Configuration not updated.  Please check the command line options.{args}", args);
                Environment.Exit(1);
            }

            return true;
        }

        #endregion CommandLine

        public void Save()
        {
            // Read the existing appsettings.json content
            var configFileContent = File.ReadAllText(ConfigFile);
            var configJson = JObject.Parse(configFileContent);

            // Serialize the current instance of Config to JSON
            var serializedConfig = JsonConvert.SerializeObject(this, Formatting.Indented);

            // Parse the serialized config as a JObject
            var updatedConfigSection = JObject.Parse(serializedConfig);

            // Directly set the "Config" section to the new configuration
            // This replaces the entire "Config" section with your updated configuration
            configJson["Config"] = updatedConfigSection;

            // Write the updated JSON back to the appsettings.json file
            File.WriteAllText(ConfigFile, configJson.ToString(Formatting.Indented));
        }

        public override string ToString()
        {
            var properties = GetType().GetProperties().Where(p => !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute))).OrderBy(p => p.Name);
            var stringBuilder = new StringBuilder();
            foreach (var property in properties)
            {
                var value = property.GetValue(this);
                if (value is HashSet<int> hashSet)
                {
                    stringBuilder.AppendLine($"{property.Name}: {string.Join(", ", hashSet)}");
                }
                else if (value is HashSet<string> stringHashSet)
                {
                    stringBuilder.AppendLine($"{property.Name}: {string.Join(", ", stringHashSet)}");
                }
                else
                {
                    stringBuilder.AppendLine($"{property.Name}: {value}");
                }
            }
            return stringBuilder.ToString();
        }
    }
}