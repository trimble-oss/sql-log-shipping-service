using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;


namespace LogShippingService
{
    class Program
    {

        public static readonly NamedLocker Locker = new();

        static void Main()
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

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
    }
}