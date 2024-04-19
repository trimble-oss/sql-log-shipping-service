using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LogShippingService.FileHandling
{
    internal class FileHandler
    {
        private static FileHandlerBase? _fileHandler;

        public static FileHandlerBase FileHandlerInstance => _fileHandler ??= GetFileHandler();

        public static FileHandlerBase GetFileHandler()
        {
            return AppConfig.Config.FileHandlerType switch
            {
                Config.FileHandlerTypes.Disk => new DiskFileHandler(),
                Config.FileHandlerTypes.AzureBlob => new AzureBlobFileHandler(),
                Config.FileHandlerTypes.S3 => new S3FileHandler(),
                _ => throw new NotImplementedException($"FileHandlerType '{AppConfig.Config.FileHandlerType}' not implemented.")
            };
        }
    }
}