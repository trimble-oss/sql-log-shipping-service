using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LogShippingTest
{
    internal static class StringExtensions
    {

        public static string SqlSingleQuote(this string str)
        {
            return "'" + str.Replace("'", "''") + "'";
        }
        public static string SqlQuote(this string str)
        {
            return "[" + str.Replace("]", "]]") + "]";
        }

        public static string RemovePrefix(this string str, string prefix)
        {
            return str.StartsWith(prefix) ? str.Substring(prefix.Length) : str;
        }

    }
}
