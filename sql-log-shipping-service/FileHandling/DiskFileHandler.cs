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
        public override IEnumerable<BackupFile> GetFiles(string pathsCSV, string pattern, DateTime maxAge, bool ascending)
        {
            var paths = pathsCSV.Split(',')
                .Select(path => path.Trim())
                .Where(path => !string.IsNullOrEmpty(path)) 
                .ToList();

            return GetFiles(paths, pattern, maxAge, ascending);
        }

        public IEnumerable<BackupFile> GetFiles(List<string> paths, string pattern, DateTime maxAge, bool ascending)
        {
            if (!paths.Any(Directory.Exists))
            {
                throw new DirectoryNotFoundException($"GetFilesFromDisk: None of the provided folders exist. {string.Join(",",paths)}");
            }

            // Use EnumerateFiles for better performance with large directories
            var allFiles = paths.AsParallel() 
                .Where(Directory.Exists)
                .SelectMany(path => new DirectoryInfo(path).EnumerateFiles(pattern))
                .Where(f => f.LastWriteTimeUtc >= maxAge);

            // Apply sorting only after filtering
            var sortedFiles = ascending ? allFiles.OrderBy(f => f.LastWriteTimeUtc) : allFiles.OrderByDescending(f => f.LastWriteTimeUtc);

            // Delay materialization and map to BackupFile objects
            foreach (var file in sortedFiles)
            {
                yield return new BackupFile(file.FullName, BackupHeader.DeviceTypes.Disk, file.LastWriteTimeUtc);
            }
        }



        protected override IEnumerable<string> GetDatabasesSpecific()
        {
            if (string.IsNullOrEmpty(Config.FullFilePath)) return Enumerable.Empty<string>();

            // Config.FullFilePath might be comma-separated list of paths, split and process each
            var databases = Config.FullFilePath.Split(',')
                .Select(path => path.Trim())
                .Where(path => !string.IsNullOrEmpty(path))
                .SelectMany(path =>
                {
                    // Find where the database token is in the path, or return an empty sequence if not found
                    var dbRootIndex = path.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase);
                    if (dbRootIndex == -1) return Enumerable.Empty<string>(); 
                    // Get the root path up to the database token.  Folders within this path gives us the database names
                    var dbRoot = path[..dbRootIndex];
                    Log.Information("Polling for new databases from disk. Folders in path: {path}", dbRoot);

                    // Get names of subdirectories in the root path (database names)
                    try
                    {
                        return Directory.EnumerateDirectories(dbRoot).Select(dir => Path.GetFileName(dir)!);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to enumerate directories in path: {path}", dbRoot);
                        return Enumerable.Empty<string>();
                    }
                })
                .Distinct(); // Ensure unique database names

            return databases;
        }

    }
}