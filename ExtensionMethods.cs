namespace LogShippingService
{
    public static class ExtensionMethods
    {
        public static string SqlSingleQuote(this string str)
        {
            return "'" + str.Replace("'", "''") + "'";
        }

        public static string SqlQuote(this string str)
        {
            return "[" + str.Replace("]", "]]") + "]";
        }

        public static string RemovePrefix(this string str, string prefix)
        {
            return str.StartsWith(prefix) ? str[prefix.Length..] : str;
        }

        /// <summary>
        /// Returns the char associated with the backup type (consistent with msdb.dbo.backupset)
        /// </summary>
        /// <param name="backupType"></param>
        /// <returns>Char associated with the backup type in msdb.dbo.backupset</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public static char ToBackupTypeChar(this BackupHeader.BackupTypes backupType)
        {
            return backupType switch
            {
                BackupHeader.BackupTypes.DatabaseFull => 'D',
                BackupHeader.BackupTypes.DatabaseDiff => 'I',
                BackupHeader.BackupTypes.File => 'F',
                BackupHeader.BackupTypes.FileDiff => 'G',
                BackupHeader.BackupTypes.Partial => 'P',
                BackupHeader.BackupTypes.PartialDiff => 'Q',
                _ => throw new ArgumentOutOfRangeException(nameof(backupType), backupType, null)
            };
        }

        public static List<string> GetFileList(this List<BackupFile> backupFiles)
        {
            return backupFiles.Select(f => f.FilePath).ToList();
        }
    }
}