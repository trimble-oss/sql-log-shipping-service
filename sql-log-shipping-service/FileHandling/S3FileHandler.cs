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
            return GetFilesFromUrlS3(path, pattern, maxAge, ascending).Result;
        }

        protected override IEnumerable<string> GetDatabasesSpecific()
        {
            if (Config.FullFilePath == null) { return new List<string>(); }
            var s3Uri = new S3Uri(Config.FullFilePath);
            var key = s3Uri.Key[..s3Uri.Key.IndexOf(HttpUtility.UrlEncode(Config.DatabaseToken), StringComparison.OrdinalIgnoreCase)];
            var dbRoot = $"s3://{s3Uri.Uri.Host}/{key}";

            Log.Information("Polling for new databases from S3.  Prefix: {prefix}", dbRoot);
            return ListFoldersFromS3(dbRoot).Result;
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
    }
}