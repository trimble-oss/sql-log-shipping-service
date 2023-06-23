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
        private bool _isStopRequested;
        public void Stop()
        {
            _isStopRequested=true;
        }

        public bool WaitUntilActiveHours()
        {
            if (CanRestoreLogsNow) return !_isStopRequested;

            Log.Information("Waiting for active hours to run {Hours}", Config.Hours);
            
            while (!CanRestoreLogsNow && !_isStopRequested)
            {
                Thread.Sleep(1000);
            }

            if (!_isStopRequested)
            {
                Log.Information("Wait for active hours is complete");
            }

            return !_isStopRequested;
        }

        public static bool CanRestoreLogsNow => Config.Hours.Contains(DateTime.Now.Hour);

    }
}
