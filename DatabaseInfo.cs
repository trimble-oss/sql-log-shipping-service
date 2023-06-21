using Microsoft.Data.SqlClient;

namespace LogShippingService
{
    internal class DatabaseInfo
    {
        public string Name { get; set; } = null!;
        public short RecoveryModel { get; set; }
        public short State { get; set; }
        public bool IsInStandby { get; set; }

        public static List<DatabaseInfo> GetDatabaseInfo(string connectionString)
        {
            List<DatabaseInfo> databaseInfos = new();

            using var cn = new SqlConnection(connectionString);

            cn.Open();

            using SqlCommand command = new(SqlStrings.GetUserDatabases, cn);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                DatabaseInfo info = new()
                {
                    Name = reader.GetString(0),
                    RecoveryModel = reader.GetByte(1),
                    State = reader.GetByte(2),
                    IsInStandby = reader.GetBoolean(3)
                };

                // Add the object to the list.
                databaseInfos.Add(info);
            }

            // Return the list.
            return databaseInfos;
        }
    }
}