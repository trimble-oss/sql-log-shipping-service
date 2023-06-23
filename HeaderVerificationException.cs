using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LogShippingService
{
    public class HeaderVerificationException : Exception
    {

        public BackupHeader.HeaderVerificationStatus VerificationStatus { get; }

        public HeaderVerificationException(string message, BackupHeader.HeaderVerificationStatus status)
            : base(message)
        {
            VerificationStatus = status;
        }
    }
}
