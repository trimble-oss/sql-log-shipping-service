using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

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
        private static readonly string configFile = "appsettings.json";

        static Config()
        {
            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile(configFile)
                    .Build();

                // Read values from the configuration
                ContainerURL = configuration["Config:ContainerUrl"];
                SASToken = configuration["Config:SASToken"];
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
                    throw new ArgumentException(message,"SASToken");
                }

                ConnectionString = configuration["Config:Destination"] ?? throw new InvalidOperationException();
                MaxThreads = Int32.Parse(configuration["Config:MaxThreads"] ?? throw new InvalidOperationException());
                LogFilePathTemplate = configuration["Config:LogFilePath"] ?? throw new InvalidOperationException();
                if (!LogFilePathTemplate.Contains(DatabaseToken))
                {
                    throw new ValidationException($"LogFilePathTemplate should contain '{DatabaseToken}'");
                }
                IterationDelayMs = Int32.Parse(configuration["Config:DelayBetweenIterationsMs"] ??
                                               throw new InvalidOperationException());
                OffSetMins = Int32.Parse(configuration["Config:OffsetMins"] ?? throw new InvalidOperationException());
                MaxProcessingTimeMins = Int32.Parse(configuration["Config:MaxProcessingTimeMins"] ??
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
            
            var json = File.ReadAllText(configFile);
            dynamic jsonObj = JsonConvert.DeserializeObject(json) ?? throw new InvalidOperationException();

            jsonObj[section][key] = value;

            string output = JsonConvert.SerializeObject(jsonObj, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(configFile, output);
        }
    }
}
