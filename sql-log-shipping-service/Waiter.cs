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
        private static Config Config => AppConfig.Config;

        public static async Task WaitUntilActiveHoursAsync(CancellationToken stoppingToken)
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

        public static async Task WaitUntilTimeAsync(DateTime waitUntil, CancellationToken stoppingToken)
        {
            int delayMilliseconds;
            do
            {
                // Calculate how long to wait based on when we want the next iteration to start. If we need to wait longer than int.MaxValue (24.8 days), the process will loop.
                delayMilliseconds =
                    (int)Math.Min((waitUntil - DateTime.Now).TotalMilliseconds, int.MaxValue);
                if (delayMilliseconds <= 0) break;
                await Task.Delay(delayMilliseconds, stoppingToken);
            } while (delayMilliseconds == int.MaxValue && waitUntil > DateTime.Now && !stoppingToken.IsCancellationRequested); // Not expected to loop - only if we overflowed the int.MaxValue (24.8 days)
        }

        public static bool CanRestoreLogsNow => Config.Hours.Contains(DateTime.Now.Hour);
    }
}