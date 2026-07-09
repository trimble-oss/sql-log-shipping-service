using Serilog;

namespace LogShippingService
{
    /// <summary>
    /// Provides a logger tagged so that database re-initialization events - a database was re-initialized, or requires manual
    /// re-initialization - are written to a dedicated audit log in addition to the main log.  These events are rare but significant
    /// (they involve dropping a database), so an easy to find, separate record is kept.
    /// </summary>
    internal static class AuditLog
    {
        /// <summary>Log property used to tag re-initialization events so a dedicated sink can filter on them.</summary>
        public const string ReinitializationProperty = "Reinitialization";

        /// <summary>
        /// Logger for re-initialization events.  Events are written to the main log and, via the tagged property, to the dedicated
        /// re-initialization log.  Resolved from the current Log.Logger on each access so it always uses the configured logger.
        /// </summary>
        public static ILogger Reinitialization => Log.ForContext(ReinitializationProperty, true);
    }
}
