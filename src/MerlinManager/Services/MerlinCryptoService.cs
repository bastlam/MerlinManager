using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MerlinManager.Services
{
    /// <summary>
    /// Chiffrement des playlists Merlin v3 (fichier "pl_enc.bin"), reconstitué depuis l'app Merlin 1.30.0.
    ///
    /// Schéma :
    ///   clé    = SHA-256( 6 octets de l'adresse MAC SoftAP du boîtier )
    ///   fichier = IV(12) ‖ ciphertext ‖ tag GCM(16)   (AES-256-GCM, tag 128 bits, sans AAD)
    ///
    /// Le boîtier dérive sa MAC SoftAP depuis sa MAC WiFi station (STA) en ajoutant 1
    /// (dérivation de type ESP). L'utilisateur saisit la MAC STA ; on applique +1 pour obtenir la clé.
    /// </summary>
    public static class MerlinCryptoService
    {
        private const int IvLength = 12;
        private const int TagLength = 16;

        /// <summary>Marqueur (4 octets de poids fort de la forme stockée du GUID) identifiant les items créés par MerlinManager sur la carte.</summary>
        private static readonly byte[] CustomMarker = { 0x4D, 0x4D, 0x4D, 0x4D }; // "MMMM"

        private static readonly Regex MacSeparators = new("[:\\-\\s]", RegexOptions.Compiled);

        /// <summary>Normalise une MAC "AA:BB:CC:DD:EE:FF" (ou sans séparateurs) en 6 octets.</summary>
        public static byte[] ParseMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac))
            {
                throw new ArgumentException("Adresse MAC vide.", nameof(mac));
            }

            var hex = MacSeparators.Replace(mac.Trim(), string.Empty);
            if (hex.Length != 12 || !Regex.IsMatch(hex, "^[0-9a-fA-F]{12}$"))
            {
                throw new ArgumentException($"Adresse MAC invalide : '{mac}'. Format attendu : AA:BB:CC:DD:EE:FF.", nameof(mac));
            }

            var bytes = new byte[6];
            for (var i = 0; i < 6; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        /// <summary>Dérive la MAC SoftAP (clé) depuis la MAC STA saisie : +1 sur l'ensemble des 48 bits (propagation de retenue).</summary>
        public static byte[] DeriveSoftApMac(byte[] staMac)
        {
            var mac = (byte[])staMac.Clone();
            for (var i = mac.Length - 1; i >= 0; i--)
            {
                if (++mac[i] != 0)
                {
                    break; // pas de retenue
                }
            }
            return mac;
        }

        /// <summary>Clé AES-256 = SHA-256 des 6 octets de MAC fournis.</summary>
        public static byte[] DeriveKey(byte[] mac) => SHA256.HashData(mac);

        /// <summary>Construit la clé à partir de la MAC STA saisie par l'utilisateur (applique la dérivation SoftAP +1).</summary>
        public static byte[] KeyFromStationMac(string stationMac)
            => DeriveKey(DeriveSoftApMac(ParseMac(stationMac)));

        public static byte[] Decrypt(byte[] file, byte[] key)
        {
            if (file.Length < IvLength + TagLength)
            {
                throw new ArgumentException("Fichier chiffré trop court.");
            }

            var iv = file[..IvLength];
            var tag = file[^TagLength..];
            var cipher = file[IvLength..^TagLength];
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(iv, cipher, tag, plain);
            return plain;
        }

        public static byte[] Encrypt(byte[] plain, byte[] key)
        {
            var iv = RandomNumberGenerator.GetBytes(IvLength);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagLength];

            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(iv, plain, cipher, tag);

            var result = new byte[IvLength + cipher.Length + TagLength];
            Buffer.BlockCopy(iv, 0, result, 0, IvLength);
            Buffer.BlockCopy(cipher, 0, result, IvLength, cipher.Length);
            Buffer.BlockCopy(tag, 0, result, IvLength + cipher.Length, TagLength);
            return result;
        }

        // --- Encodage des GUID (16 octets, chaque champ en little-endian) ---
        // Guid.ToByteArray() fournit déjà D1/D2/D3 en little-endian ; Merlin stocke en plus
        // le clock_seq (2 o) et le node (6 o) en little-endian. On inverse donc ces deux blocs.

        public static byte[] GuidToMerlinBytes(Guid guid)
        {
            var b = guid.ToByteArray();
            Array.Reverse(b, 8, 2);
            Array.Reverse(b, 10, 6);
            return b;
        }

        public static Guid MerlinBytesToGuid(ReadOnlySpan<byte> bytes)
        {
            var b = bytes.Slice(0, 16).ToArray();
            Array.Reverse(b, 8, 2);
            Array.Reverse(b, 10, 6);
            return new Guid(b);
        }

        /// <summary>Un GUID de carte est-il un item créé par MerlinManager (porte le marqueur) ?</summary>
        public static bool IsCustomGuid(Guid guid)
        {
            var b = GuidToMerlinBytes(guid);
            return b[0] == CustomMarker[0] && b[1] == CustomMarker[1]
                && b[2] == CustomMarker[2] && b[3] == CustomMarker[3];
        }

        /// <summary>
        /// Dérive de façon déterministe le GUID « carte » marqué pour un item MerlinManager,
        /// à partir de son GUID interne (celui qui suit le préfixe "MM-"). Les 4 premiers octets
        /// de la forme stockée reçoivent le marqueur ; le reste est conservé.
        /// </summary>
        public static Guid ToMarkedCardGuid(Guid internalGuid)
        {
            var b = GuidToMerlinBytes(internalGuid);
            Array.Copy(CustomMarker, 0, b, 0, 4);
            return MerlinBytesToGuid(b);
        }
    }
}
