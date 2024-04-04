﻿using System.Runtime.CompilerServices;
using CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using static System.Collections.Specialized.BitVector32;
using System.Windows.Forms;
using Microsoft.Data.SqlClient;
using System.Linq;

namespace LogShippingService
{
    internal class Program
    {
        public const string ConfigFile = "appsettings.json";
        public static readonly NamedLocker Locker = new();

        private static void Main(string[] args)
        {
            if (!File.Exists(ConfigFile))
            {
                File.WriteAllText(ConfigFile, "{}");
            }
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateLogger();

            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            SetupLogging(configuration);
            AppConfig.Config = configuration.GetSection("Config").Get<Config>() ?? new Config();

            var cliApply = AppConfig.Config.ApplyCommandLineOptions(args);

            Log.Information("Configuration:\n" + AppConfig.Config.ToString());
            try
            {
                AppConfig.Config.ValidateConfig();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error validating config.");
                return;
            }
            if (cliApply)
            {
                return;
            }

            var builder = Host.CreateApplicationBuilder();

            // Configure the ShutdownTimeout to infinite
            builder.Services.Configure<HostOptions>(options =>
                options.ShutdownTimeout = Timeout.InfiniteTimeSpan);
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "LogShippingService";
            });
            builder.Services.AddHostedService<LogShipping>();

            var host = builder.Build();
            host.Run();
        }

        private static void SetupLogging(IConfigurationRoot? configuration)
        {
            // Check if the Serilog section exists and has content
            var serilogSection = configuration?.GetSection("Serilog");
            if (configuration != null && serilogSection.Exists() && serilogSection.GetChildren().Any())
            {
                // Configure Serilog from the configuration file
                Log.Logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration)
                    .CreateLogger();
            }
            else
            {
                // Configure Serilog with default settings programmatically
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} <{ThreadId}>{NewLine}{Exception}")
                    .WriteTo.File(path: "Logs/log-.txt",
                        rollingInterval: RollingInterval.Hour,
                        retainedFileCountLimit: 24,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} <{ThreadId}>{NewLine}{Exception}")
                    .Enrich.FromLogContext()
                    .Enrich.WithMachineName()
                    .Enrich.WithThreadId()
                    .Enrich.WithProperty("Application", "LogShippingService")
                    .CreateLogger();
            }
        }
    }
}