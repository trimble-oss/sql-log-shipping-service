namespace LogShippingService
{
    internal class QueueItem : IEquatable<QueueItem>
    {
        /// <summary>
        /// Name of the database on the source server.  Usually the same as TargetDb, but may have a prefix or suffix.
        /// </summary>
        public string SourceDb { get; }

        /// <summary>
        /// Name of the database where logs are restored.
        /// </summary>
        public string TargetDb { get; }

        /// <summary>
        /// Backup finish date of the last restored log backup.  Log shipping will start from the next log backup after this date.
        /// </summary>
        public DateTime FromDate { get; }

        public QueueItem(string sourceDb, string targetDb, DateTime fromDate)
        {
            SourceDb = sourceDb;
            TargetDb = targetDb;
            FromDate = fromDate;
        }

        /// <summary>
        /// Equality based on TargetDb only. We want to avoid duplicates in the queue for the same target database.
        /// SourceDb is usually the same as TargetDb, but a prefix or suffix may have been added.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool Equals(QueueItem? other) =>
            other != null && string.Equals(TargetDb, other.TargetDb, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => Equals(obj as QueueItem);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(TargetDb);
    }
}