using System;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using LogShippingTest;
using Microsoft.Extensions.Configuration;
using Serilog;
using SerilogTimings;
using Topshelf;

namespace ListFilesFromAzureBlob
{
    class Program
    {
        
        static void Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            var rc = HostFactory.Run(x =>
            {
                x.Service<LogShipping>(s =>
                {
                    s.ConstructUsing(name => new LogShipping());
                    s.WhenStarted(tc => tc.Start());
                    s.WhenStopped(tc => tc.Stop());
                });
                x.StartAutomaticallyDelayed();
                x.EnableServiceRecovery(r =>
                {
                    r.RestartService(1);
                });

                x.SetDescription("Log Shipping Service for SQL Server");
                x.SetDisplayName("Log Shipping Service");
                x.SetServiceName("LogShippingService");
            });

        }
    }
}