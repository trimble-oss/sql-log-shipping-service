using Microsoft.Data.SqlClient;
using System.Data;

namespace LogShippingService
{
    /// <summary>
    /// Get info for the last backup for a specified database from msdb
    /// </summary>
    public class LastBackup
    {
        public bool HasBackup => FileList.Count > 0;
        public List<string> FileList = new();
        public BackupHeader.DeviceTypes DeviceType = BackupHeader.DeviceTypes.Unknown;
        public string DatabaseName;
        public DateTime BackupFinishDate = DateTime.MinValue;

        public LastBackup(string databaseName, string connectionString, BackupHeader.BackupTypes type)
        {
            DatabaseName = databaseName;
            var lastBackup = GetFilesForLastBackup(databaseName, type.ToBackupTypeChar(), connectionString);
            foreach (DataRow row in lastBackup.Rows)
            {
                if (row["device_type"] is not DBNull && DeviceType == BackupHeader.DeviceTypes.Unknown)
                {
                    var deviceTypeInt = Convert.ToInt32(row["device_type"]);
                    BackupFinishDate = (DateTime)row["backup_finish_date"];
                    if (Enum.IsDefined(typeof(BackupHeader.DeviceTypes), deviceTypeInt))
                    {
                        DeviceType = (BackupHeader.DeviceTypes)deviceTypeInt;
                    }
                }
                FileList.Add((string)row["physical_device_name"]);
            }
        }

        public void Restore(string connectionString)
        {
            var sql = GetRestoreDbScript();
            DataHelper.ExecuteWithTiming(sql, connectionString);
        }

        /// <summary>
        /// Returns the backup header.  If backup file has multiple backups and exception will be thrown.  Use GetHeaders to return all the header info to support multiple backups in same file
        /// </summary>
        /// <param name="connectionString">Database connection string</param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public BackupHeader GetHeader(string connectionString)
        {
            var headers = GetHeaders(connectionString);
            return headers.Count switch
            {
                1 => headers[0],
                > 1 => throw new Exception(
                    "RESTORE HEADERONLY returned multiple rows.  Multiple backups have been written to the same file."),
                _ => throw new Exception("RESTORE HEADERONLY returned no rows")
            };
        }

        /// <summary>
        /// Return the backup headers associated with the backup
        /// </summary>
        /// <param name="connectionString">Database connection string</param>
        /// <returns></returns>
        public List<BackupHeader> GetHeaders(string connectionString)
        {
            return BackupHeader.GetHeaders(FileList, connectionString, DeviceType);
        }

        public string GetHeaderOnlyScript() => DataHelper.GetHeaderOnlyScript(FileList, DeviceType);

        public string GetFileListOnlyScript() => DataHelper.GetFileListOnlyScript(FileList, DeviceType);

        public string GetRestoreDbScript() => DataHelper.GetRestoreDbScript(FileList, DatabaseName, DeviceType);

        internal static DataTable GetFilesForLastBackup(string db, char backupType, string connectionString)
        {
            using var cn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(SqlStrings.GetFilesForLastBackup, cn) { CommandTimeout = 0 };
            using var da = new SqlDataAdapter(cmd);
            cmd.Parameters.AddWithValue("@db", db);
            cmd.Parameters.AddWithValue("@backup_type", backupType);
            cmd.Parameters.AddWithValue("@MaxBackupAgeForInitialization", Config.MaxBackupAgeForInitialization);
            var dt = new DataTable();
            da.Fill(dt);
            return dt;
        }

        internal static DataTable GetFilesForLastFullBackup(string db, string connectionString) =>
            GetFilesForLastBackup(db, 'D', connectionString);

        internal static DataTable GetFilesForLastDiffBackup(string db, string connectionString) =>
            GetFilesForLastBackup(db, 'I', connectionString);
    }
}