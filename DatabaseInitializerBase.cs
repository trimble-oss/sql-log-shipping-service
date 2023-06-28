using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace LogShippingService
{
    public abstract class DatabaseInitializerBase
    {
        protected abstract void PollForNewDBs();

        public bool IsStopRequested;
        private readonly Waiter wait = new();

        public void Stop()
        {
            wait.Stop();
            IsStopRequested = true;
        }

        public void WaitForShutdown()
        {
            if (!IsStopRequested)
            {
                throw new Exception("Stop hasn't been requested.");
            }
            while (!IsStopped)
            {
                Thread.Sleep(100);
            }
        }

        public bool IsStopped { get; private set; }

        public abstract bool IsValidated { get;}

        public void RunPollForNewDBs()
        {
            if (!IsValidated)
            {
                IsStopped = true;
                return;
            }

            while (!IsStopRequested)
            {
                if (!wait.WaitUntilActiveHours())
                {
                    break;
                }

                try
                {
                    PollForNewDBs();
                }
                catch (Exception ex)
                {
                    Log.Error(ex,"Error running poll for new DBs");
                }

                var nextIterationStart = DateTime.Now.AddMinutes(Config.PollForNewDatabasesFrequency);

                while (DateTime.Now < nextIterationStart && !IsStopRequested)
                {
                    Thread.Sleep(100);
                }
            }
            Log.Information("Poll for new DBs is shutdown");
            IsStopped = true;
        }
    }
}
