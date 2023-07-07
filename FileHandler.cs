using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Serilog;

namespace LogShippingService
{
    internal class FileHandler
    {

        public static BackupHeader.DeviceTypes DeviceType =>
            !string.IsNullOrEmpty(Config.ContainerURL) && !string.IsNullOrEmpty(Config.SASToken)
                ? BackupHeader.DeviceTypes.Url
                : BackupHeader.DeviceTypes.Disk;

        public static IEnumerable<BackupFile> GetFiles(string path, string pattern, DateTime MaxAge)
        {
            if (DeviceType== BackupHeader.DeviceTypes.Url)
            {
                return GetFilesFromUrl(path, pattern, MaxAge, new Uri(Config.ContainerURL + Config.SASToken));
            }
            else
            {
                return GetFilesFromDisk(path, pattern, MaxAge);
            }
        }

        public static  IEnumerable<string> GetDatabases()
        {
            if (string.IsNullOrEmpty(Config.FullBackupPathTemplate)) return new List<string>();
            if (Config.IncludedDatabases.Count > 0)
            {
                Log.Information("Polling for new databases.  Using IncludedDatabases list. {Included}",Config.IncludedDatabases);
                return Config.IncludedDatabases;
            }
            else if(DeviceType  == BackupHeader.DeviceTypes.Disk) 
            {
                var dbRoot = Config.FullBackupPathTemplate[
                    ..Config.FullBackupPathTemplate.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];
                Log.Information("Polling for new databases from disk.  Folders in path: {path}",dbRoot);
                return System.IO.Directory.EnumerateDirectories(dbRoot);
            }
            else
            {
                var dbRoot = Config.FullBackupPathTemplate[..Config.FullBackupPathTemplate.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];
                Log.Information("Polling for new databases from Azure Blob.  Folders in path: {path}", dbRoot);
                return GetFoldersForAzBlob(dbRoot);
            }
        }

        private static IEnumerable<string> GetFoldersForAzBlob(string prefix)
        {
            var containerUri = new Uri(Config.ContainerURL + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            var results = containerClient.GetBlobsByHierarchy(prefix: prefix, delimiter: "/").AsPages();

            return (from blobPage in results from item in blobPage.Values select item.Prefix.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Last());
        }



        public static IEnumerable<BackupFile> GetFilesFromDisk(string path,string pattern, DateTime MaxAge)
        {
            if (!Directory.Exists(path)) throw new Exception($"GetFilesFromDisk Folder '{path}' does not exist.");
            List<string> fileList = new();
            var directory = new DirectoryInfo(path);

            return directory.GetFiles(pattern)
                .Where(f => f.LastWriteTimeUtc >= MaxAge)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f=> new BackupFile(f.FullName, BackupHeader.DeviceTypes.Disk,f.LastWriteTimeUtc));
        }

        public static IEnumerable<BackupFile> GetFilesFromUrl(string path, string pattern, DateTime MaxAge,Uri containerUri)
        {
            var files = new List<string>();
            var containerClient = new BlobContainerClient(containerUri);

           return containerClient
                .GetBlobs(BlobTraits.Metadata, BlobStates.None, path)
                .Where(blobItem => IsFileNameMatchingPattern(blobItem.Name, pattern) &&
                                   blobItem.Properties.LastModified >= MaxAge)
                .OrderByDescending(blobItem => blobItem.Properties.LastModified)
                .Select(blobItem =>
                    new BackupFile(Config.ContainerURL + "/" + blobItem.Name, BackupHeader.DeviceTypes.Url, blobItem.Properties.LastModified!.Value.UtcDateTime));
        }

       public static bool IsFileNameMatchingPattern(string fileName, string searchPattern)
        {
            var pattern = "^" + Regex.Escape(searchPattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";

            return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
        }

    }
}
