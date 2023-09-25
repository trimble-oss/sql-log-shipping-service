﻿using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Serilog;
using System.ComponentModel.DataAnnotations;

namespace LogShippingService
{
    internal static class Config
    {
        public static readonly string? ContainerURL;
        public static readonly string? SASToken;
        public static readonly string ConnectionString;
        public static readonly int MaxThreads;
        public static readonly string? LogFilePathTemplate;
        public static readonly int IterationDelayMs;
        public static readonly int OffSetMins;
        public static readonly int MaxProcessingTimeMins;
        public static readonly string DatabaseToken = "{DatabaseName}";
        private const string ConfigFile = "appsettings.json";
        public static readonly string? StandbyFileName;
        public static bool KillUserConnections;
        public static int KillUserConnectionsWithRollBackAfter;
        public static List<int> Hours;
        public static List<string> IncludedDatabases;
        public static List<string> ExcludedDatabases;
        public static string? SourceConnectionString;
        public static int PollForNewDatabasesFrequency;
        public static bool CheckHeaders;
        public static string? FullBackupPathTemplate;
        public static string? DiffBackupPathTemplate;
        public static bool InitializeSimple;
        public static int MaxBackupAgeForInitialization;
        public static string? MoveDataFolder;
        public static string? MoveLogFolder;
        public static string? MoveFileStreamFolder;
        public static string? ReadOnlyPartialBackupPathTemplate;
        public static bool RecoverPartialBackupWithoutReadOnly;

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
                PollForNewDatabasesFrequency = int.Parse(configuration["Config:PollForNewDatabasesFrequency"] ?? 1.ToString());
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
                    if (!SASToken.StartsWith("?"))
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
                MaxThreads = int.Parse(configuration["Config:MaxThreads"] ?? throw new InvalidOperationException());
                LogFilePathTemplate = configuration["Config:LogFilePath"];
                FullBackupPathTemplate = configuration["Config:FullFilePath"];
                DiffBackupPathTemplate = configuration["Config:DiffFilePath"];
                ReadOnlyPartialBackupPathTemplate = configuration["Config:ReadOnlyFilePath"];
                if (LogFilePathTemplate != null && !LogFilePathTemplate.Contains(DatabaseToken))
                {
                    throw new ValidationException("LogFilePathTemplate should contain '{DatabaseToken}'");
                }
                IterationDelayMs = int.Parse(configuration["Config:DelayBetweenIterationsMs"] ??
                                               throw new InvalidOperationException());
                OffSetMins = int.Parse(configuration["Config:OffsetMins"] ?? throw new InvalidOperationException());
                MaxProcessingTimeMins = int.Parse(configuration["Config:MaxProcessingTimeMins"] ??
                                                    throw new InvalidOperationException());
                InitializeSimple = bool.Parse(configuration["Config:InitializeSimple"] ?? false.ToString());
                MaxBackupAgeForInitialization = int.Parse(configuration["Config:MaxBackupAgeForInitialization"] ?? 14.ToString());
                MoveDataFolder = configuration["Config:MoveDataFolder"];
                MoveLogFolder = configuration["Config:MoveLogFolder"];
                MoveFileStreamFolder = configuration["Config:MoveFileStreamFolder"];
                RecoverPartialBackupWithoutReadOnly = bool.Parse(configuration["Config:RecoverPartialBackupWithoutReadOnly"] ?? false.ToString());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error reading config");
                throw;
            }
        }

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