using Amazon.Runtime;
using Amazon.S3.Model;
using Amazon.S3;
using Amazon;
using System.Web;
using Serilog;

namespace LogShippingService.FileHandling
{
    internal class S3FileHandler : FileHandlerBase
    {
        public override IEnumerable<BackupFile> GetFiles(string path, string pattern, DateTime maxAge, bool ascending)
        {
            var paths = path.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return GetFilesFromUrlsS3(paths, pattern, maxAge, ascending).Result;
        }

        protected override IEnumerable<string> GetDatabasesSpecific()
        {
            if (Config.FullFilePath == null) { return new List<string>(); }
            // Split the full file path on comma to get individual S3 paths
            var s3Paths = Config.FullFilePath.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            // Process each S3 path to generate dbRoot values
            var dbRootList = s3Paths.Select(s3Path =>
            {
                var s3Uri = new S3Uri(s3Path.Trim());
                var key = s3Uri.Key[..s3Uri.Key.IndexOf(HttpUtility.UrlEncode(Config.DatabaseToken), StringComparison.OrdinalIgnoreCase)];
                return $"s3://{s3Uri.Uri.Host}/{key}";
            }).ToList();

            Log.Information("Polling for new databases from S3.  Prefix: {prefix}", dbRootList);
            return ListFoldersFromS3Paths(dbRootList).Result;
        }

        public static async Task<IEnumerable<BackupFile>> GetFilesFromUrlsS3(List<string> paths, string pattern, DateTime MaxAge, bool ascending)
        {
            // Use Task.WhenAll to await all the tasks initiated for each path
            var tasks = paths.Select(path => GetFilesFromUrlS3(path, pattern, MaxAge)).ToList();
            var filesFromAllPaths = await Task.WhenAll(tasks);

            // Flatten the results and order them
            var allFiles = filesFromAllPaths.SelectMany(files => files);
            return ascending ? allFiles.OrderBy(file => file.LastModifiedUtc) : allFiles.OrderByDescending(file => file.LastModifiedUtc);
        }

        private static async Task<IEnumerable<BackupFile>> GetFilesFromUrlS3(string path, string pattern, DateTime MaxAge)
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
                var matchingFiles = response.S3Objects
                    .Where(s3Object => IsFileNameMatchingPattern(s3Object.Key, pattern) && s3Object.LastModified.ToUniversalTime() >= MaxAge)
                    .Select(s3Object => new BackupFile($"s3://{s3Uri.Uri.Host}/{s3Object.Key}", BackupHeader.DeviceTypes.Url, s3Object.LastModified.ToUniversalTime()));

                files.AddRange(matchingFiles);
                request.ContinuationToken = response.NextContinuationToken;
            } while (response.IsTruncated);
            
            return files;
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

        public static async Task<List<string>> ListFoldersFromS3Paths(List<string> paths)
        {
            // Initiate folder listing tasks for each path in parallel
            var tasks = paths.Select(ListFoldersFromS3SinglePath).ToList();
            var foldersFromAllPaths = await Task.WhenAll(tasks);

            // Flatten results and remove duplicates
            var allFolders = foldersFromAllPaths.SelectMany(folders => folders).Distinct().ToList();

            return allFolders;
        }

        public static async Task<List<string>> ListFoldersFromS3SinglePath(string path)
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
    }
}