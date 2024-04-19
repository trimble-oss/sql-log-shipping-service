using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace LogShippingService.FileHandling
{
    internal class AzureBlobFileHandler : FileHandlerBase
    {
        internal static readonly char[] separator = { '/' };

        private static IEnumerable<string> GetFoldersForAzBlob(string prefix)
        {
            var containerUri = new Uri(Config.ContainerUrl + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            var results = containerClient.GetBlobsByHierarchy(prefix: prefix, delimiter: "/").AsPages();

            return from blobPage in results from item in blobPage.Values select item.Prefix.Split(separator, StringSplitOptions.RemoveEmptyEntries).Last();
        }

        public override IEnumerable<BackupFile> GetFiles(string path, string pattern, DateTime maxAge, bool ascending)
        {
            var containerUri = new Uri(Config.ContainerUrl + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            // Retrieve blobs filtered by path, pattern, and maxAge
            var blobItems = containerClient.GetBlobs(BlobTraits.Metadata, BlobStates.None, path)
                .Where(blobItem => IsFileNameMatchingPattern(blobItem.Name, pattern) &&
                                   blobItem.Properties.LastModified.GetValueOrDefault(DateTimeOffset.MinValue) >= maxAge);

            // Using switch expression to determine sort order
            var sortedBlobItems = ascending switch
            {
                true => blobItems.OrderBy(blobItem => blobItem.Properties.LastModified),
                false => blobItems.OrderByDescending(blobItem => blobItem.Properties.LastModified)
            };

            // Map to BackupFile objects
            return sortedBlobItems.Select(blobItem => new BackupFile(
                $"{Config.ContainerUrl}/{blobItem.Name}",
                BackupHeader.DeviceTypes.Url,
                blobItem.Properties.LastModified!.Value.UtcDateTime));
        }

        protected override IEnumerable<string> GetDatabasesSpecific()
        {
            if (Config.FullFilePath == null) { return new List<string>(); }
            var dbRoot = Config.FullFilePath[..Config.FullFilePath.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];
            Log.Information("Polling for new databases from Azure Blob.  Folders in path: {path}", dbRoot);
            return GetFoldersForAzBlob(dbRoot);
        }
    }
}