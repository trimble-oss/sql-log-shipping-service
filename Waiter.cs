using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LogShippingService
{
    internal class Waiter
    {

        public static async Task WaitUntilActiveHours(CancellationToken stoppingToken)
        {
            if (CanRestoreLogsNow) return;

            Log.Information("Waiting for active hours to run {Hours}", Config.Hours);
            
            while (!CanRestoreLogsNow && !stoppingToken.IsCancellationRequested)
            {
               await Task.Delay(1000, stoppingToken);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                Log.Information("Wait for active hours is complete");
            }

        }

        public static bool CanRestoreLogsNow => Config.Hours.Contains(DateTime.Now.Hour);

    }
}
