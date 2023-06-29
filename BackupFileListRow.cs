using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;

namespace LogShippingService
{
    internal class BackupFileListRow
    {
        public string LogicalName { get; set; }
        public string PhysicalName { get; set; }

        public string FileName => Path.GetFileName(PhysicalName);

        public char Type { get; set; }

        public string? FileGroupName { get; set; }

        public BigInteger Size { get; set; }

        public BigInteger MaxSize { get; set; }

        public long FileID { get; set; }
        public BigInteger CreateLSN { get; set; }

        public BigInteger? DropLSN { get; set; }

        public Guid UniqueId { get; set; }

        public BigInteger? ReadOnlyLSN { get; set; }

        public BigInteger? ReadWriteLSN { get; set; }

        public long BackupSizeInBytes { get; set; }

        public int SourceBlockSize { get; set; }

        public int FileGroupID { get; set; }

        public Guid? LogGroupGUID { get; set; }

        public BigInteger? DifferentialBaseLSN { get; set; }

        public Guid? DifferentialBaseGUID { get; set; }

        public bool IsReadOnly { get; set; }

        public bool IsPresent { get; set; }

        public byte[]? TDEThumbprint { get; set; }

        public string? SnapshotURL { get; set; }

        public BackupFileListRow(DataRow row)
        {
            LogicalName = (string)row["LogicalName"];
            PhysicalName = (string)row["PhysicalName"];
            Type = Convert.ToChar(row["Type"]);
            FileGroupName = row["FileGroupName"] as string;
            Size = BackupHeader.GetBigInteger(row, "Size");
            MaxSize = BackupHeader.GetBigInteger(row, "MaxSize");
            FileID = (long)row["FileID"];
            CreateLSN = BackupHeader.GetBigInteger(row, "CreateLSN");
            DropLSN = BackupHeader.GetNullableBigInteger(row, "DropLSN");
            UniqueId = (Guid)row["UniqueId"];
            ReadOnlyLSN = BackupHeader.GetNullableBigInteger(row, "ReadOnlyLSN");
            ReadWriteLSN = BackupHeader.GetNullableBigInteger(row, "ReadWriteLSN");
            BackupSizeInBytes = (long)row["BackupSizeInBytes"];
            SourceBlockSize = (int)row["SourceBlockSize"];
            FileGroupID = (int)row["FileGroupID"];
            LogGroupGUID = row["LogGroupGUID"] == DBNull.Value ? null : (Guid)row["LogGroupGUID"];
            DifferentialBaseLSN = BackupHeader.GetNullableBigInteger(row, "DifferentialBaseLSN");
            DifferentialBaseGUID = row["DifferentialBaseGUID"] == DBNull.Value ? null : (Guid)row["DifferentialBaseGUID"];
            IsReadOnly = (bool)row["IsReadOnly"];
            IsPresent = (bool)row["IsPresent"];
            if (row.Table.Columns.Contains("TDEThumbprint"))
            {
                TDEThumbprint = row["TDEThumbprint"] == DBNull.Value ? null : (byte[])row["TDEThumbprint"];
            }
            if (row.Table.Columns.Contains("SnapshotURL"))
            {
                SnapshotURL = row["SnapshotURL"] as string;
            }
        }

        public static List<BackupFileListRow> GetFileList(List<string> backupFiles, string connectionString, BackupHeader.DeviceTypes deviceType)
        {
            List<BackupFileListRow> fileList = new();
            var sql = DataHelper.GetFileListOnlyScript(backupFiles, deviceType);
            var dt = DataHelper.GetDataTable(sql,connectionString);
            foreach (DataRow row in dt.Rows)
            {
                fileList.Add(new BackupFileListRow(row));
            }
            return fileList;
        }

    }
}
