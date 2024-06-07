using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using System.Collections.Concurrent;

namespace LogShippingService.FileHandling
{
    internal class AzureBlobFileHandler : FileHandlerBase
    {
        internal static readonly char[] separator = { '/' };

        public static List<string> GetFoldersForAzBlob(List<string> prefixes)
        {
            var containerUri = new Uri(Config.ContainerUrl);
            var containerClient = new BlobContainerClient(new Uri($"{containerUri}{Config.SASToken}"));

            // Use a thread-safe collection to store folders from multiple threads
            var folders = new ConcurrentBag<string>();

            // Parallelize the processing of each prefix
            Parallel.ForEach(prefixes, prefix =>
            {
                var results = containerClient.GetBlobsByHierarchy(prefix: prefix, delimiter: "/").AsPages();

                results.SelectMany(blobPage => blobPage.Values)
                    .Where(item => item.IsPrefix)
                    .Select(item => item.Prefix.TrimEnd(separator).Split(separator).LastOrDefault())
                    .Where(folderName => !string.IsNullOrEmpty(folderName))
                    .Distinct()
                    .ToList()
                    .ForEach(folderName => folders.Add(folderName!));
            });

            return folders.Distinct().ToList();
        }

        public IEnumerable<BackupFile> GetFiles(List<string> paths, string pattern, DateTime maxAge, bool ascending)
        {
            var containerUri = new Uri(Config.ContainerUrl + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            // Temporarily store the filtered blobs from each path
            var allFilteredBlobs = new ConcurrentBag<BlobItem>();

            Parallel.ForEach(paths, path =>
            {
                var blobItems = containerClient.GetBlobs(BlobTraits.Metadata, BlobStates.None, path)
                    .Where(blobItem => IsFileNameMatchingPattern(blobItem.Name, pattern) &&
                                       blobItem.Properties.LastModified.GetValueOrDefault(DateTimeOffset.MinValue).UtcDateTime>= maxAge);

                foreach (var blobItem in blobItems)
                {
                    allFilteredBlobs.Add(blobItem);
                }
            });

            // Sort the blobs based on the ascending flag
            var sortedBlobs = ascending
                ? allFilteredBlobs.OrderBy(blobItem => blobItem.Properties.LastModified.GetValueOrDefault(DateTimeOffset.MinValue).UtcDateTime)
                : allFilteredBlobs.OrderByDescending(blobItem => blobItem.Properties.LastModified.GetValueOrDefault(DateTimeOffset.MinValue).UtcDateTime);

            // Yield return each BackupFile
            foreach (var blobItem in sortedBlobs)
            {
                yield return new BackupFile(
                    $"{Config.ContainerUrl}/{blobItem.Name}",
                    BackupHeader.DeviceTypes.Url,
                    blobItem.Properties.LastModified!.Value.UtcDateTime);
            }
        }

        public override IEnumerable<BackupFile> GetFiles(string path, string pattern, DateTime maxAge, bool ascending)
        {
            var paths = path.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return GetFiles(paths, pattern, maxAge, ascending);
        }

        protected override IEnumerable<string> GetDatabasesSpecific()
        {
            if (Config.FullFilePath == null) { return new List<string>(); }
            var paths = Config.FullFilePath.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var dbRoots = paths.Select(path =>
            {
                var tokenIndex = path.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase);
                return tokenIndex != -1 ? path[..tokenIndex] : path;
            }).ToList();

            Log.Information("Polling for new databases from Azure Blob.  Folders in path(s): {path}", dbRoots);
            return GetFoldersForAzBlob(dbRoots);
        }
    }
}