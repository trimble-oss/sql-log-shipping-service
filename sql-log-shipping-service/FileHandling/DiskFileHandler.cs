using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace LogShippingService.FileHandling
{
    internal class DiskFileHandler : FileHandlerBase
    {
        public override IEnumerable<BackupFile> GetFiles(string path, string pattern, DateTime maxAge, bool ascending)
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"GetFilesFromDisk: Folder '{path}' does not exist.");

            var directory = new DirectoryInfo(path);

            // Retrieve files filtered by pattern and maxAge
            var files = directory.GetFiles(pattern)
                .Where(f => f.LastWriteTimeUtc >= maxAge);

            // Using switch expression to determine sort order
            var sortedFiles = ascending switch
            {
                true => files.OrderBy(f => f.LastWriteTimeUtc),
                false => files.OrderByDescending(f => f.LastWriteTimeUtc)
            };

            // Map to BackupFile objects
            return sortedFiles.Select(f => new BackupFile(
                f.FullName,
                BackupHeader.DeviceTypes.Disk,
                f.LastWriteTimeUtc));
        }

        protected override IEnumerable<string> GetDatabasesSpecific()
        {
            if (Config.FullFilePath == null) { return new List<string>(); }
            var dbRoot = Config.FullFilePath[
                ..Config.FullFilePath.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];
            Log.Information("Polling for new databases from disk.  Folders in path: {path}", dbRoot);
            return Directory.EnumerateDirectories(dbRoot).Select(Path.GetFileName)!;
        }
    }
}