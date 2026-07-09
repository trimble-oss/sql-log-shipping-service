namespace LogShippingService
{
    /// <summary>
    /// Thrown when a log backup is found that cannot be applied because the source log chain has been reset.
    /// This happens when the source database is dropped and re-created with the same name, or when the log chain
    /// is otherwise broken (e.g. the recovery model is switched to SIMPLE and back to FULL).
    /// The log-shipped database must be dropped and re-initialized from a current backup to resume log shipping.
    /// </summary>
    public class ReinitializeRequiredException : Exception
    {
        public string SourceDb { get; }
        public string TargetDb { get; }

        /// <summary>Human readable description of why the log chain can't be continued - the source database was re-created, or the log chain was otherwise broken.</summary>
        public string Reason { get; }

        public ReinitializeRequiredException(string message, string sourceDb, string targetDb, string reason)
            : base(message)
        {
            SourceDb = sourceDb;
            TargetDb = targetDb;
            Reason = reason;
        }
    }
}
