using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ASWDEBUG.Tools.ManagedAssemblyDecryptor
{
    internal sealed class ProtectionProfile
    {
        private const string ProfileFormat = "ASWDEBUG.ManagedAssemblyDecryptor.Profile.v1";

        public DateTime CreatedUtc;
        public int TrailerLength;
        public byte[] ProtectedMagic;
        public byte[] OuterKey;
        public byte[] InnerStream;
        public int RuntimeImagesUsed;
        public int ProtectedMethodPairs;
        public int PlainMethodPairs;
        public int ExceptionTablesValidated;

        public string OuterKeySha256
        {
            get { return BinaryUtil.ComputeSha256(OuterKey); }
        }

        public string InnerStreamSha256
        {
            get { return BinaryUtil.ComputeSha256(InnerStream); }
        }

        public void Validate()
        {
            if (TrailerLength <= 0 || TrailerLength > 1024 * 1024)
                throw new InvalidDataException("Invalid protected trailer length: " + TrailerLength);
            if (ProtectedMagic == null || ProtectedMagic.Length != ProtectionCalibrator.MagicLength)
                throw new InvalidDataException("The protection profile must contain an 8-byte magic value.");
            if (OuterKey == null || OuterKey.Length != ProtectionCalibrator.OuterKeyLength)
                throw new InvalidDataException("The protection profile must contain a 4096-byte outer key.");
            if (InnerStream == null || InnerStream.Length == 0)
                throw new InvalidDataException("The protection profile does not contain a method stream.");
        }

        public string Serialize()
        {
            Validate();
            var builder = new StringBuilder(OuterKey.Length * 2 + InnerStream.Length * 2);
            builder.AppendLine("Format=" + ProfileFormat);
            builder.AppendLine("CreatedUtc=" + CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
            builder.AppendLine("TrailerLength=" + TrailerLength.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("ProtectedMagic=" + BinaryUtil.ToHex(ProtectedMagic));
            builder.AppendLine("OuterKeyBase64=" + Convert.ToBase64String(OuterKey));
            builder.AppendLine("OuterKeySHA256=" + OuterKeySha256);
            builder.AppendLine("InnerStreamBase64=" + Convert.ToBase64String(InnerStream));
            builder.AppendLine("InnerStreamLength=" + InnerStream.Length.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("InnerStreamSHA256=" + InnerStreamSha256);
            builder.AppendLine("RuntimeImagesUsed=" + RuntimeImagesUsed.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("ProtectedMethodPairs=" + ProtectedMethodPairs.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("PlainMethodPairs=" + PlainMethodPairs.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("ExceptionTablesValidated=" + ExceptionTablesValidated.ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        public void Save(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, Serialize(), new UTF8Encoding(false));
        }

        public static ProtectionProfile Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Protection profile not found.", path);

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) throw new InvalidDataException("Invalid profile line " + (i + 1) + ".");
                string key = line.Substring(0, separator);
                string value = line.Substring(separator + 1);
                if (values.ContainsKey(key)) throw new InvalidDataException("Duplicate profile key: " + key);
                values.Add(key, value);
            }

            if (Require(values, "Format") != ProfileFormat)
                throw new InvalidDataException("Unsupported protection profile format.");

            var profile = new ProtectionProfile();
            DateTime created;
            if (!DateTime.TryParse(
                    Require(values, "CreatedUtc"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out created))
                throw new InvalidDataException("Invalid CreatedUtc value in protection profile.");
            profile.CreatedUtc = created;
            profile.TrailerLength = ParseInt(values, "TrailerLength");
            profile.ProtectedMagic = BinaryUtil.FromHex(Require(values, "ProtectedMagic"));
            profile.OuterKey = Convert.FromBase64String(Require(values, "OuterKeyBase64"));
            profile.InnerStream = Convert.FromBase64String(Require(values, "InnerStreamBase64"));
            profile.RuntimeImagesUsed = ParseOptionalInt(values, "RuntimeImagesUsed");
            profile.ProtectedMethodPairs = ParseOptionalInt(values, "ProtectedMethodPairs");
            profile.PlainMethodPairs = ParseOptionalInt(values, "PlainMethodPairs");
            profile.ExceptionTablesValidated = ParseOptionalInt(values, "ExceptionTablesValidated");
            profile.Validate();

            if (!string.Equals(Require(values, "OuterKeySHA256"), profile.OuterKeySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Outer key SHA-256 does not match the profile contents.");
            if (!string.Equals(Require(values, "InnerStreamSHA256"), profile.InnerStreamSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Inner stream SHA-256 does not match the profile contents.");
            if (ParseInt(values, "InnerStreamLength") != profile.InnerStream.Length)
                throw new InvalidDataException("Inner stream length does not match the profile contents.");
            return profile;
        }

        private static string Require(IDictionary<string, string> values, string key)
        {
            string value;
            if (!values.TryGetValue(key, out value) || string.IsNullOrEmpty(value))
                throw new InvalidDataException("Missing profile value: " + key);
            return value;
        }

        private static int ParseInt(IDictionary<string, string> values, string key)
        {
            int value;
            if (!int.TryParse(Require(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException("Invalid integer profile value: " + key);
            return value;
        }

        private static int ParseOptionalInt(IDictionary<string, string> values, string key)
        {
            string text;
            if (!values.TryGetValue(key, out text)) return 0;
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException("Invalid integer profile value: " + key);
            return value;
        }
    }

    internal static class BinaryUtil
    {
        public static bool HasPrefix(byte[] value, byte[] prefix)
        {
            if (value == null || prefix == null || value.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (value[i] != prefix[i]) return false;
            return true;
        }

        public static string ComputeSha256(byte[] value)
        {
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(value ?? new byte[0]));
        }

        public static string ToHex(byte[] value)
        {
            if (value == null) return string.Empty;
            var builder = new StringBuilder(value.Length * 2);
            for (int i = 0; i < value.Length; i++)
                builder.Append(value[i].ToString("X2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        public static byte[] FromHex(string value)
        {
            if (value == null || (value.Length & 1) != 0)
                throw new InvalidDataException("Invalid hexadecimal value.");
            var bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }

        public static int Align4(int value)
        {
            if (value > int.MaxValue - 3) throw new InvalidDataException("Alignment overflow.");
            return (value + 3) & ~3;
        }

        public static byte[] ReadExact(BinaryReader reader, int count)
        {
            if (count < 0) throw new InvalidDataException("Negative binary field length.");
            byte[] value = reader.ReadBytes(count);
            if (value.Length != count) throw new EndOfStreamException("Unexpected end of binary data.");
            return value;
        }
    }
}
