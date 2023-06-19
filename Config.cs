using Microsoft.Extensions.Configuration;
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
        public static readonly string LogFilePathTemplate;
        public static readonly int IterationDelayMs;
        public static readonly int OffSetMins;
        public static readonly int MaxProcessingTimeMins;
        public static readonly string DatabaseToken="{DatabaseName}";
        private const string ConfigFile = "appsettings.json";
        public static readonly string? StandbyFileName;
        public static bool KillUserConnections;
        public static int KillUserConnectionsWithRollBackAfter;

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
                if (!string.IsNullOrEmpty(SASToken) && !EncryptionHelper.IsEncrypted(SASToken))
                {
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
                if(!string.IsNullOrEmpty(StandbyFileName) && (!Config.StandbyFileName.Contains(DatabaseToken)))
                {
                    Log.Error("Missing {DatabaseToken} from StandbyFileName",DatabaseToken);
                    throw new ArgumentException($"Missing {DatabaseToken} from StandbyFileName");
                }

                ConnectionString = configuration["Config:Destination"] ?? throw new InvalidOperationException();
                MaxThreads = int.Parse(configuration["Config:MaxThreads"] ?? throw new InvalidOperationException());
                LogFilePathTemplate = configuration["Config:LogFilePath"] ?? throw new InvalidOperationException();
                if (!LogFilePathTemplate.Contains(DatabaseToken))
                {
                    throw new ValidationException($"LogFilePathTemplate should contain '{DatabaseToken}'");
                }
                IterationDelayMs = int.Parse(configuration["Config:DelayBetweenIterationsMs"] ??
                                               throw new InvalidOperationException());
                OffSetMins = int.Parse(configuration["Config:OffsetMins"] ?? throw new InvalidOperationException());
                MaxProcessingTimeMins = int.Parse(configuration["Config:MaxProcessingTimeMins"] ??
                                                    throw new InvalidOperationException());
            }
            catch (Exception ex)
            {
                Log.Error(ex,"Error reading config");
                throw;
            }
        }


        private static void Update(string section,string key, string value)
        {
            
            var json = File.ReadAllText(ConfigFile);
            dynamic jsonObj = JsonConvert.DeserializeObject(json) ?? throw new InvalidOperationException();

            jsonObj[section][key] = value;

            string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
            File.WriteAllText(ConfigFile, output);
        }
    }
}
