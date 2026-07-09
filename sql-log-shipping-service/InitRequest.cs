namespace LogShippingService
{
    /// <summary>
    /// Why a database is being (re)initialized from a FULL backup.
    /// </summary>
    internal enum InitReason
    {
        /// <summary>A new source database was discovered that does not yet exist on the destination.</summary>
        NewDatabase,

        /// <summary>An existing log-shipped database must be dropped and re-initialized because its log chain can no longer be continued.</summary>
        Reinitialize
    }

    /// <summary>
    /// A unit of work for the shared initialization/re-initialization queue.  Both reasons perform a FULL restore, so they
    /// share a single bounded consumer pool (see <see cref="DatabaseInitializerBase"/>).
    /// </summary>
    /// <param name="SourceDb">Source database name.</param>
    /// <param name="TargetDb">Destination database name (the log-restore gate key).</param>
    /// <param name="Reason">Whether this is a first-time initialization or a re-initialization.</param>
    /// <param name="Detail">Optional human readable detail (e.g. the reason a re-initialization is required).</param>
    /// <param name="Attempt">1-based attempt counter, incremented when a re-initialization is requeued after a failure.</param>
    internal sealed record InitRequest(string SourceDb, string TargetDb, InitReason Reason, string? Detail, int Attempt = 1);
}
