using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LogShippingService
{
    internal class ReadOnlyBackupSet
    {
        public List<BackupFile> BackupFiles=new();
        public List<BackupFileListRow> ToRestore = null!;
    }
}
