namespace LogShippingService
{
    public class BackupFile
    {
        private List<BackupFileListRow>? _backupFiles;
        private List<BackupHeader>? _headers;
        private Config Config => AppConfig.Config;

        public BackupFile(string filePath, BackupHeader.DeviceTypes deviceType, DateTime lastModUtc)
        {
            FilePath = filePath;
            DeviceType = deviceType;
            LastModifiedUtc = lastModUtc;
        }

        public BackupHeader.DeviceTypes DeviceType { get; set; }

        public string FilePath { get; set; }

        public DateTime LastModifiedUtc { get; set; }

        public BackupHeader FirstHeader => Headers[0];

        public List<BackupHeader> Headers
        {
            get
            {
                _headers ??= BackupHeader.GetHeaders(FilePath, Config.Destination, DeviceType);
                return _headers;
            }
        }

        public List<BackupFileListRow> BackupFileList
        {
            get
            {
                _backupFiles ??= BackupFileListRow.GetFileList(FilePath, Config.Destination, DeviceType);
                return _backupFiles;
            }
        }
    }
}