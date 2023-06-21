using System.Data;
using System.Numerics;

namespace LogShippingService
{
    public class BackupHeader
    {
        public enum BackupTypes
        {
            DatabaseFull = 1,
            TransactionLog = 2,
            File = 4,
            DatabaseDiff = 5,
            FileDiff = 6,
            Partial = 7,
            PartialDiff = 8
        }

        public enum DeviceTypes
        {
            Unknown = -1,
            Disk = 2,
            Diskette = 3,
            Tape = 5,
            Pipe = 6,
            Virtual = 7,
            Url = 9,
            UrlPhysical = 109,
            TapePhysical = 105,
            DiskPhysical = 102
        }

        public string? BackupName { get; set; }
        public string? BackupDescription { get; set; }
        public BackupTypes BackupType { get; set; }
        public DateTime? ExpirationDate { get; set; }
        public bool Compressed { get; set; }
        public DeviceTypes DeviceType { get; set; }

        public string UserName { get; set; } = null!;
        public string ServerName { get; set; } = null!;
        public string DatabaseName { get; set; } = null!;

        public int DatabaseVersion { get; set; }

        public DateTime DatabaseCreationDate { get; set; }

        public BigInteger FirstLSN { get; set; }
        public BigInteger LastLSN { get; set; }
        public BigInteger CheckpointLSN { get; set; }

        public BigInteger DatabaseBackupLSN { get; set; }

        public BigInteger? ForkPointLSN { get; set; }

        public BigInteger? DifferentialBaseLSN { get; set; }
        public BigInteger BackupSize { get; set; }

        public DateTime BackupStartDate { get; set; }

        public DateTime BackupFinishDate { get; set; }
        public short SortOrder { get; set; }

        public short CodePage { get; set; }

        public int UnicodeLocaleId { get; set; }

        public int UnicodeComparisonStyle { get; set; }

        public short CompatibilityLevel { get; set; }

        public int SoftwareVendorId { get; set; }

        public int SoftwareVersionMajor { get; set; }
        public int SoftwareVersionMinor { get; set; }
        public int SoftwareVersionBuild { get; set; }

        public string MachineName { get; set; } = null!;

        public int Flags { get; set; }

        public Guid BindingID { get; set; }

        public Guid RecoveryForkID { get; set; }

        public string Collation { get; set; } = null!;

        public Guid FamilyGUID { get; set; }

        public bool HasBulkLoggedData { get; set; }
        public bool IsSnapshot { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsSingleUser { get; set; }

        public bool HasBackupChecksums { get; set; }

        public bool IsDamaged { get; set; }
        public bool BeginsLogChain { get; set; }
        public bool HasIncompleteMetaData { get; set; }

        public bool IsForceOffline { get; set; }
        public bool IsCopyOnly { get; set; }

        public Guid FirstRecoveryForkID { get; set; }

        public string RecoveryModel { get; set; } = null!;
        public Guid? DifferentialBaseGUID { get; set; }

        public string BackupTypeDescription { get; set; } = null!;

        public Guid BackupSetGUID { get; set; }

        public long CompressedBackupSize { get; set; }

        public short? Containment { get; set; }

        public string? KeyAlgorithm { get; set; }

        public string? EncryptorThumbprint { get; set; }

        public string? EncryptorType { get; set; }

        public DateTime? LastValidRestoreTime { get; set; }

        public string? TimeZone { get; set; }

        public string? CompressionAlgorithm { get; set; }

        public BackupHeader(DataRow row)
        {
            SetFromRow(row);
        }

        private void SetFromRow(DataRow row)
        {
            BackupName = row["BackupName"] as string;
            BackupDescription = row["BackupDescription"] as string;
            BackupType = (BackupTypes)Convert.ToInt32(row["BackupType"]);
            ExpirationDate = row["ExpirationDate"] == DBNull.Value ? null : Convert.ToDateTime(row["ExpirationDate"]);
            Compressed = Convert.ToBoolean(row["Compressed"]);
            DeviceType = (DeviceTypes)Convert.ToInt32(row["DeviceType"]);
            UserName = (string)row["UserName"];
            ServerName = (string)row["ServerName"];
            DatabaseName = (string)row["DatabaseName"];
            DatabaseVersion = (int)row["DatabaseVersion"];
            DatabaseCreationDate = (DateTime)row["DatabaseCreationDate"];
            BackupSize = GetBigInteger(row, "BackupSize");
            FirstLSN = GetBigInteger(row, "FirstLSN");
            LastLSN = GetBigInteger(row, "LastLSN");
            CheckpointLSN = GetBigInteger(row, "CheckpointLSN");
            DatabaseBackupLSN = GetBigInteger(row, "DatabaseBackupLSN");
            BackupStartDate = (DateTime)row["BackupStartDate"];
            BackupFinishDate = (DateTime)row["BackupFinishDate"];
            SortOrder = Convert.ToInt16(row["SortOrder"]);
            CodePage = Convert.ToInt16(row["CodePage"]);
            UnicodeLocaleId = Convert.ToInt32(row["UnicodeLocaleId"]);
            UnicodeComparisonStyle = Convert.ToInt32(row["UnicodeComparisonStyle"]);
            CompatibilityLevel = Convert.ToInt16(row["CompatibilityLevel"]);
            SoftwareVendorId = Convert.ToInt32(row["SoftwareVendorId"]);
            SoftwareVersionMajor = Convert.ToInt32(row["SoftwareVersionMajor"]);
            SoftwareVersionMinor = Convert.ToInt32(row["SoftwareVersionMinor"]);
            SoftwareVersionBuild = Convert.ToInt32(row["SoftwareVersionBuild"]);
            MachineName = (string)row["MachineName"];
            Flags = Convert.ToInt32(row["Flags"]);
            BindingID = (Guid)row["BindingID"];
            RecoveryForkID = (Guid)row["RecoveryForkID"];
            Collation = (string)row["Collation"];
            FamilyGUID = (Guid)row["FamilyGUID"];
            HasBulkLoggedData = (bool)row["HasBulkLoggedData"];
            IsSnapshot = (bool)row["IsSnapshot"];
            IsReadOnly = (bool)row["IsReadOnly"];
            IsSingleUser = (bool)row["IsSingleUser"];
            HasBackupChecksums = (bool)row["HasBackupChecksums"];
            IsDamaged = (bool)row["IsDamaged"];
            BeginsLogChain = (bool)row["BeginsLogChain"];
            HasIncompleteMetaData = (bool)row["HasIncompleteMetaData"];
            IsForceOffline = (bool)row["IsForceOffline"];
            IsCopyOnly = (bool)row["IsCopyOnly"];
            FirstRecoveryForkID = (Guid)row["FirstRecoveryForkID"];
            ForkPointLSN = GetNullableBigInteger(row, "ForkPointLSN");
            RecoveryModel = (string)row["RecoveryModel"];
            DifferentialBaseLSN = GetNullableBigInteger(row, "DifferentialBaseLSN");
            DifferentialBaseGUID = row["DifferentialBaseGUID"] == DBNull.Value ? null : (Guid)row["DifferentialBaseGUID"];
            BackupTypeDescription = (string)row["BackupTypeDescription"];
            BackupSetGUID = (Guid)row["BackupSetGUID"];
            CompressedBackupSize = (long)row["CompressedBackupSize"];

            if (row.Table.Columns.Contains("containment"))
            {
                Containment = Convert.ToInt16(row["containment"]);
            }
            if (row.Table.Columns.Contains("KeyAlgorithm"))
            {
                KeyAlgorithm = row["KeyAlgorithm"] as string;
            }
            if (row.Table.Columns.Contains("EncryptorThumbprint"))
            {
                EncryptorThumbprint = row["EncryptorThumbprint"] as string;
            }
            if (row.Table.Columns.Contains("EncryptorType"))
            {
                EncryptorType = row["EncryptorType"] as string;
            }
            if (row.Table.Columns.Contains("LastValidRestoreTime"))
            {
                LastValidRestoreTime = row["LastValidRestoreTime"] == DBNull.Value ? null : (DateTime)row["LastValidRestoreTime"];
            }
            if (row.Table.Columns.Contains("TimeZone"))
            {
                TimeZone = row["TimeZone"] as string;
            }
            if (row.Table.Columns.Contains("CompressionAlgorithm"))
            {
                CompressionAlgorithm = row["CompressionAlgorithm"] as string;
            }
        }

        public static List<BackupHeader> GetHeaders(List<string> backupFiles, string connectionString, DeviceTypes deviceType)
        {
            if (backupFiles == null || backupFiles.Count == 0)
            {
                throw new Exception("No backup files");
            }
            List<BackupHeader> headers = new();
            var headerSQL = DataHelper.GetHeaderOnlyScript(backupFiles, deviceType);
            var dt = DataHelper.GetDataTable(headerSQL, connectionString);
            foreach (DataRow row in dt.Rows)
            {
                headers.Add(new BackupHeader(row));
            }
            return headers;
        }

        public static BigInteger? GetNullableBigInteger(DataRow row, string columnName)
        {
            return row[columnName] == DBNull.Value ? null : BigInteger.Parse(row[columnName].ToString()!);
        }

        public static BigInteger GetBigInteger(DataRow row, string columnName)
        {
            return BigInteger.Parse(row[columnName].ToString()!);
        }
    }
}