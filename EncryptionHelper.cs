using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LogShippingTest;

namespace LogShippingService
{
    internal class EncryptionHelper
    {
        private static readonly string EncryptionPrefix = "encrypted:";

        public static string EncryptWithMachineKey(string value)
        {
            byte[] valueBytes = Encoding.UTF8.GetBytes(value);
            byte[] encryptedBytes = ProtectedData.Protect(valueBytes, null, DataProtectionScope.LocalMachine);
            return EncryptionPrefix + Convert.ToBase64String(encryptedBytes);
        }

        public static string DecryptWithMachineKey(string encryptedValue)
        {
            encryptedValue = encryptedValue.RemovePrefix(EncryptionPrefix);
            byte[] encryptedBytes = Convert.FromBase64String(encryptedValue);
            byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decryptedBytes);
        }

        public static bool IsEncrypted(string value)
        {
            return value.StartsWith(EncryptionPrefix);
        }
    }
}
