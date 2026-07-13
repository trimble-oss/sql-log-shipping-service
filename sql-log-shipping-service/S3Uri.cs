using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.S3.Util;

namespace LogShippingService
{
    internal class S3Uri
    {
        public string Bucket { get; private set; }
        public string Key { get; private set; }
        public string RegionSystemName { get; private set; }
        public Uri Uri { get; private set; }

        public Amazon.RegionEndpoint Region => Amazon.RegionEndpoint.GetBySystemName(RegionSystemName);

        public S3Uri(string s3Uri)
        {
            Uri = new Uri(s3Uri);
            RegionSystemName = ExtractRegionFromHost(Uri.Host);
            Bucket = Uri.Host.Split('.')[0];
            // Use the unescaped path so special characters in the key (e.g. curly braces in
            // database names) are not percent-encoded.  AbsolutePath would encode '{' as %7B,
            // '}' as %7D etc., which then wouldn't match the literal S3 object keys when used
            // as a ListObjects prefix.
            Key = Uri.GetComponents(UriComponents.Path, UriFormat.Unescaped).TrimStart('/');
        }

        private string ExtractRegionFromHost(string host)
        {
            // Regular expression to extract the region from a standard S3 or a virtual-hosted style S3 URI
            var regex = new Regex(@"s3[.-](?<region>[a-z0-9-]+)\.amazonaws\.com$", RegexOptions.IgnoreCase);
            var match = regex.Match(host);
            if (match.Success)
            {
                return match.Groups["region"].Value;
            }
            throw new ArgumentException("Region not found in URI.");
        }
    }
}