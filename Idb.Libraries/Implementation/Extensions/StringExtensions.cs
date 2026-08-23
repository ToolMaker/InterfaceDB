using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Idb.Libraries.Implementation.Extensions
{
    public static class StringExtensions
    {
        public static string NormalizeExtension(this string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".bin";
            }

            return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
        }
    }
}
