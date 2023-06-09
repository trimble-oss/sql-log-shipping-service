using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace LogShippingService
{
    internal class SqlStrings
    {
        public static string GetSqlString(string name)
        {
            var resourcePath = "LogShippingService.SQL." + name + ".sql";
            var assembly = Assembly.GetExecutingAssembly();

            using var stream = assembly.GetManifestResourceStream(resourcePath);
            if (stream != null)
            {
                using StreamReader reader = new(stream);
                return reader.ReadToEnd();
            }
            else
            {
                throw new ArgumentException($"GetSqlString did not find {name}");
            }
        }

        public static string GetDatabases => GetSqlString("GetDatabases");

    }
}
