using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SerilogTimings;

namespace LogShippingService
{
    public static class DataHelper
    {

        public static DataTable GetDataTable(string sql,string connectionString)
        {
            using var cn = new SqlConnection(connectionString);
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
            using var da = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            da.Fill(dt);
            return dt;
        }

        public static void Execute(string sql,string connectionString)
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
                Execute(sql,connectionString);
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

        public static string GetRestoreDbScript(List<string> files, string db, BackupHeader.DeviceTypes type)
        {
            var from = GetFromDisk(files, type);
            if (string.IsNullOrEmpty(from)) { return string.Empty; }
            StringBuilder builder = new();
            builder.AppendLine($"RESTORE DATABASE {db.SqlQuote()} ");
            builder.AppendLine(from);
            builder.Append("WITH NORECOVERY");
            return builder.ToString();
        }

        private static string GetFromDisk(List<string> files, BackupHeader.DeviceTypes type)
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
    }
}
