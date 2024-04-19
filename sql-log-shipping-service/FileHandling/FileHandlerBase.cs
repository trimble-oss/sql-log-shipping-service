using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Serilog;

namespace LogShippingService.FileHandling
{
    public abstract class FileHandlerBase
    {
        public static Config.FileHandlerTypes FileHandlerType => AppConfig.Config.FileHandlerType;

        public static Config Config => AppConfig.Config;

        public abstract IEnumerable<BackupFile> GetFiles(string path, string pattern, DateTime maxAge, bool ascending);

        public virtual IEnumerable<string> GetDatabases()
        {
            if (string.IsNullOrEmpty(Config.FullFilePath)) return new List<string>();
            if (Config.IncludedDatabases.Count > 0)
            {
                Log.Information("Polling for new databases.  Using IncludedDatabases list. {Included}", Config.IncludedDatabases);
                return Config.IncludedDatabases;
            }

            // Let derived classes handle specific behavior
            return GetDatabasesSpecific();
        }

        protected abstract IEnumerable<string> GetDatabasesSpecific();

        // Common methods or properties can be defined here
        public static bool IsFileNameMatchingPattern(string fileName, string searchPattern)
        {
            var pattern = "^" + Regex.Escape(searchPattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";

            return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
        }
    }
}