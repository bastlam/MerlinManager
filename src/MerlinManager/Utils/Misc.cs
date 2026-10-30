using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerlinManager.Utils
{
    public static class Misc
    {
        public static Guid NewGuidStartingWith(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("Le préfixe ne peut pas être vide.", nameof(prefix));

            if (prefix.Length > 8)
                throw new ArgumentException(
                    "Le préfixe ne peut pas dépasser 8 caractères hexadécimaux.",
                    nameof(prefix));

            if (!prefix.All(Uri.IsHexDigit))
                throw new ArgumentException(
                    "Le préfixe doit contenir uniquement des caractères hexadécimaux (0-9, A-F).",
                    nameof(prefix));

            string guid = Guid.NewGuid().ToString("N");

            string result = prefix.ToLowerInvariant() + guid[prefix.Length..];

            return Guid.ParseExact(result, "N");
        }
    }
}
