using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Amazon.S3.Model;
using Serilog;
using Amazon.S3;
using System.Drawing;
using Amazon.Runtime.Internal.Util;
using Amazon.S3.Util;
using System.IO;
using System.Text.Encodings.Web;
using System.Web;
using Amazon;
using Amazon.Runtime;

namespace LogShippingService
{
    internal class FileHandler
    {
        public static Config.FileHandlerTypes FileHandlerType => AppConfig.Config.FileHandlerType;

        private static Config Config => AppConfig.Config;

        public static IEnumerable<BackupFile> GetFiles(string path, string pattern, DateTime MaxAge, bool ascending)
        {
            if (FileHandlerType == Config.FileHandlerTypes.S3)
            {
                return GetFilesFromUrlS3(path, pattern, MaxAge, ascending).Result;
            }
            else if (FileHandlerType == Config.FileHandlerTypes.AzureBlob)
            {
                return GetFilesFromUrl(path, pattern, MaxAge, new Uri(Config.ContainerUrl + Config.SASToken), ascending);
            }
            else
            {
                return GetFilesFromDisk(path, pattern, MaxAge, ascending);
            }
        }

        public static IEnumerable<string> GetDatabases()
        {
            if (string.IsNullOrEmpty(Config.FullFilePath)) return new List<string>();
            if (Config.IncludedDatabases.Count > 0)
            {
                Log.Information("Polling for new databases.  Using IncludedDatabases list. {Included}", Config.IncludedDatabases);
                return Config.IncludedDatabases;
            }
            else switch (FileHandlerType)
                {
                    case Config.FileHandlerTypes.Disk:
                        {
                            var dbRoot = Config.FullFilePath[
                                ..Config.FullFilePath.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];
                            Log.Information("Polling for new databases from disk.  Folders in path: {path}", dbRoot);
                            return System.IO.Directory.EnumerateDirectories(dbRoot).Select(Path.GetFileName)!;
                        }
                    case Config.FileHandlerTypes.S3:
                        {
                            var s3Uri = new S3Uri(Config.FullFilePath);
                            var key = s3Uri.Key[..s3Uri.Key.IndexOf(HttpUtility.UrlEncode(Config.DatabaseToken), StringComparison.OrdinalIgnoreCase)];
                            var dbRoot = $"s3://{s3Uri.Uri.Host}/{key}";

                            Log.Information("Polling for new databases from S3.  Prefix: {prefix}", dbRoot);
                            return ListFoldersFromS3(dbRoot).Result;
                        }
                    case Config.FileHandlerTypes.AzureBlob:
                        {
                            var dbRoot = Config.FullFilePath[..Config.FullFilePath.IndexOf(Config.DatabaseToken, StringComparison.OrdinalIgnoreCase)];
                            Log.Information("Polling for new databases from Azure Blob.  Folders in path: {path}", dbRoot);
                            return GetFoldersForAzBlob(dbRoot);
                        }
                    default:
                        throw new Exception("Unknown FileHandlerType");
                }
        }

        private static IEnumerable<string> GetFoldersForAzBlob(string prefix)
        {
            var containerUri = new Uri(Config.ContainerUrl + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            var results = containerClient.GetBlobsByHierarchy(prefix: prefix, delimiter: "/").AsPages();

            return (from blobPage in results from item in blobPage.Values select item.Prefix.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Last());
        }

        public static IEnumerable<BackupFile> GetFilesFromDisk(string path, string pattern, DateTime maxAge, bool ascending)
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

        public static IEnumerable<BackupFile> GetFilesFromUrl(string path, string pattern, DateTime maxAge, Uri containerUri, bool ascending)
        {
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

        public static async Task<IEnumerable<BackupFile>> GetFilesFromUrlS3(string path, string pattern, DateTime MaxAge, bool ascending)
        {
            var s3Uri = new S3Uri(path);
            var request = new ListObjectsV2Request
            {
                BucketName = s3Uri.Bucket,
                Prefix = s3Uri.Key
            };
            var s3Client = GetS3Client(s3Uri.Region);
            var files = new List<BackupFile>();
            ListObjectsV2Response response;

            do
            {
                response = await s3Client.ListObjectsV2Async(request);
                files.AddRange(from s3Object in response.S3Objects
                               where IsFileNameMatchingPattern(s3Object.Key, pattern) && s3Object.LastModified >= MaxAge
                               let url = $"s3://{s3Uri.Uri.Host}/{s3Object.Key}"
                               select new BackupFile(url, BackupHeader.DeviceTypes.Url, s3Object.LastModified));
                request.ContinuationToken = response.NextContinuationToken;
            } while (response.IsTruncated);

            return ascending ? files.OrderBy(file => file.LastModifiedUtc) : files.OrderByDescending(file => file.LastModifiedUtc);
        }

        private static AmazonS3Client GetS3Client(RegionEndpoint region)
        {
            AWSCredentials cred;
            if (Config.AccessKey == null || Config.SecretKey == null)
            {
                cred = new InstanceProfileAWSCredentials();
            }
            else
            {
                cred = new BasicAWSCredentials(Config.AccessKey, Config.SecretKey);
            }

            var config = new AmazonS3Config
            {
                RegionEndpoint = region
            };

            return new AmazonS3Client(cred, config);
        }

        public static async Task<List<string>> ListFoldersFromS3(string path)
        {
            var s3Uri = new S3Uri(path);
            var s3Client = GetS3Client(s3Uri.Region);
            var request = new ListObjectsV2Request
            {
                BucketName = s3Uri.Bucket,
                Prefix = s3Uri.Key,
                Delimiter = "/" // Using slash as delimiter to simulate folders
            };

            var folders = new List<string>();
            ListObjectsV2Response response;

            do
            {
                response = await s3Client.ListObjectsV2Async(request);
                // Add all common prefixes (folders) to the list
                folders.AddRange(response.CommonPrefixes.Select(f => Path.GetFileName(f.TrimEnd('/'))));
                request.ContinuationToken = response.NextContinuationToken;
            } while (response.IsTruncated);

            return folders;
        }

        public static bool IsFileNameMatchingPattern(string fileName, string searchPattern)
        {
            var pattern = "^" + Regex.Escape(searchPattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";

            return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
        }

        private static List<string> GetFilesForDbUnc(string path, DateTime fromDate)
        {
            var files = new DirectoryInfo(path)
                .GetFiles("*.trn", SearchOption.AllDirectories)
                .Where(file => file.LastWriteTime > fromDate)
                .OrderBy(f => f.LastWriteTime)
                .Select(file => file.FullName)
                .ToList();

            return files;
        }

        public static List<string> GetFilesForDbAzBlob(string prefix, DateTime fromDate)
        {
            var containerUri = new Uri(Config.ContainerUrl + Config.SASToken);
            var containerClient = new BlobContainerClient(containerUri);

            var filteredBlobs = containerClient
                .GetBlobs(BlobTraits.Metadata, BlobStates.None, prefix)
                .Where(blobItem => blobItem.Properties.LastModified > fromDate)
                .OrderBy(blobItem => blobItem.Properties.LastModified)
                .Select(blobItem => Config.ContainerUrl + "/" + blobItem.Name)
                .ToList();

            return filteredBlobs;
        }
    }
}