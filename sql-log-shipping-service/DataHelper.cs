using Microsoft.Data.SqlClient;
using SerilogTimings;
using System.Data;
using System.Numerics;
using System.Text;

namespace LogShippingService
{
    public class DataHelper
    {
        private static Config Config => AppConfig.Config;

        public static DataTable GetDataTable(string sql, string connectionString)
        {
            using var cn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
            using var da = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            da.Fill(dt);
            return dt;
        }

        public static void Execute(string sql, string connectionString)
        {
            using var cn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
            cn.Open();
            cmd.ExecuteNonQuery();
        }

        public static void ExecuteWithTiming(string sql, string connectionString)
        {
            using (var op = Operation.Begin(sql))
            {
                Execute(sql, connectionString);
                op.Complete();
            }
        }

        public static string GetHeaderOnlyScript(List<string> files, BackupHeader.DeviceTypes type)
        {
            var from = GetFromDisk(files, type);
            if (string.IsNullOrEmpty(from)) { return string.Empty; }
            StringBuilder builder = new();
            builder.AppendLine($"RESTORE HEADERONLY ");
            builder.AppendLine(from);
            return builder.ToString();
        }

        public static string GetFileListOnlyScript(List<string> files, BackupHeader.DeviceTypes type)
        {
            var from = GetFromDisk(files, type);
            if (string.IsNullOrEmpty(from)) { return string.Empty; }
            StringBuilder builder = new();
            builder.AppendLine($"RESTORE FILELISTONLY ");
            builder.AppendLine(from);
            return builder.ToString();
        }

        public static string GetRestoreDbScript(List<string> files, string db, BackupHeader.DeviceTypes type,
            bool withThrowErrorIfExists, Dictionary<string, string>? fileMoves = null)
        {
            var from = GetFromDisk(files, type);
            if (string.IsNullOrEmpty(from)) { return string.Empty; }
            StringBuilder builder = new();
            if (withThrowErrorIfExists)
            {
                builder.AppendLine($"IF DB_ID(" + db.SqlSingleQuote() + ") IS NOT NULL");
                builder.AppendLine("BEGIN");
                builder.AppendLine("\tRAISERROR('Database already exists',11,1)");
                builder.AppendLine("\tRETURN");
                builder.AppendLine("END");
                builder.AppendLine();
            }
            builder.AppendLine($"RESTORE DATABASE {db.SqlQuote()} ");
            builder.AppendLine(from);
            builder.AppendLine("WITH NORECOVERY");
            if (fileMoves is { Count: > 0 })
            {
                foreach (var fileMove in fileMoves)
                {
                    builder.AppendLine(",MOVE " + fileMove.Key.SqlSingleQuote() + " TO " + fileMove.Value.SqlSingleQuote());
                }
            }
            return builder.ToString();
        }

        public static Dictionary<string, string> GetFileMoves(List<string> files, BackupHeader.DeviceTypes type, string connectionString, string? dataFolder, string? logFolder, string? fileStreamFolder, string sourceDb, string targetDb)
        {
            Dictionary<string, string> fileMoves = new();
            // Check if we need to move the files
            if (string.IsNullOrEmpty(dataFolder) && string.IsNullOrEmpty(logFolder) && string.IsNullOrEmpty(fileStreamFolder) && string.Equals(sourceDb, targetDb, StringComparison.OrdinalIgnoreCase)) { return fileMoves; }
            var list = BackupFileListRow.GetFileList(files, connectionString, type);
            foreach (var file in list)
            {
                var fileName = file.FileName;
                var movePath = file.Type switch
                {
                    'L' => logFolder,
                    'S' => fileStreamFolder,
                    _ => dataFolder
                };
                // Create a new filename if the target database is different from the source database name.  This should avoid filename conflicts
                if (!string.Equals(sourceDb, targetDb, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = (targetDb + "_" + file.LogicalName + Path.GetExtension(file.PhysicalName)).RemoveInvalidFileNameChars();
                }
                // Set movePath to the source folder if we don't have a location specified
                if (string.IsNullOrEmpty(movePath))
                {
                    movePath = Path.GetDirectoryName(file.PhysicalName) ?? throw new InvalidOperationException();
                }
                fileMoves.Add(file.LogicalName, Path.Combine(movePath, fileName));
            }
            return fileMoves;
        }

        public static Dictionary<string, string> GetFileMoves(List<string> files, BackupHeader.DeviceTypes type, string sourceDb, string targetDb)
        {
            return GetFileMoves(files, type, Config.Destination, Config.MoveDataFolder, Config.MoveLogFolder,
                Config.MoveFileStreamFolder, sourceDb, targetDb);
        }

        public static string GetFromDisk(List<string> files, BackupHeader.DeviceTypes type)
        {
            StringBuilder builder = new();
            var i = 0;
            builder.AppendLine("FROM");
            foreach (var file in files)
            {
                if (i > 0)
                {
                    builder.AppendLine(",");
                }
                switch (type)
                {
                    case BackupHeader.DeviceTypes.Disk:
                        builder.Append($"DISK = N{file.SqlSingleQuote()}");
                        break;

                    case BackupHeader.DeviceTypes.Url:
                        builder.Append($"URL = N{file.SqlSingleQuote()}");
                        break;

                    default:
                        throw new ArgumentException("Invalid DeviceType");
                }

                i++;
            }
            return builder.ToString();
        }

        public static BigInteger GetRedoStartLSNForDB(string db, string connectionString)
        {
            using var cn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(SqlStrings.GetRedoStartLSN, cn) { CommandTimeout = 0 };
            cmd.Parameters.AddWithValue("@db", db);
            cn.Open();
            return BigInteger.Parse(cmd.ExecuteScalar().ToString() ?? throw new InvalidOperationException());
        }
    }
}