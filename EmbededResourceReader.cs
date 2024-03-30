using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace LogShippingService
{

    public class EmbeddedResourceReader
    {
        public static string? ReadResourceText(string resourceFileName)
        {
            // Get the current assembly that contains the embedded resource
            var assembly = Assembly.GetExecutingAssembly();
            // Create the resource path
            var resourcePath = assembly.GetName().Name + "." + resourceFileName.Replace(" ", "_").Replace("\\", ".").Replace("/", ".");

            // Use a stream to read the embedded resource
            using var stream = assembly.GetManifestResourceStream(resourcePath);
            if (stream == null) return null; // Resource not found

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd(); // Read the content as string
        }
    }

}
