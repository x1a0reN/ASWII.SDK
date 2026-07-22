using System;
using System.Collections.Generic;
using System.IO;

namespace ASWDEBUG.Tools.ManagedAssemblyDecryptor
{
    internal static class ProtectionCalibrator
    {
        public const int OuterKeyLength = 4096;
        public const int MagicLength = 8;
        private const string RuntimeReadSuffix = ".runtime-read.dll";

        public static ProtectionProfile Calibrate(string managedDir, string runtimeDumpRoot)
        {
            if (!Directory.Exists(managedDir))
                throw new DirectoryNotFoundException("Managed directory not found: " + managedDir);
            if (!Directory.Exists(runtimeDumpRoot))
                throw new DirectoryNotFoundException("Runtime dump directory not found: " + runtimeDumpRoot);

            string[] runtimeImages = Directory.GetFiles(
                runtimeDumpRoot,
                "*" + RuntimeReadSuffix,
                SearchOption.AllDirectories);
            Array.Sort(runtimeImages, StringComparer.OrdinalIgnoreCase);
            if (runtimeImages.Length == 0)
                throw new InvalidDataException("No *.runtime-read.dll files were found under: " + runtimeDumpRoot);

            byte[] expectedMagic = null;
            byte[] expectedOuterKey = null;
            int expectedTrailerLength = 0;
            var contexts = new List<CalibrationContext>();
            int rejectedImages = 0;

            for (int i = 0; i < runtimeImages.Length; i++)
            {
                string runtimePath = runtimeImages[i];
                string encryptedName = GetEncryptedFileName(runtimePath);
                string encryptedPath = Path.Combine(managedDir, encryptedName);
                if (!File.Exists(encryptedPath)) continue;

                CalibrationContext context;
                string rejection;
                if (!TryCreateContext(encryptedPath, runtimePath, out context, out rejection))
                {
                    rejectedImages++;
                    Console.Error.WriteLine("Skipped runtime image: " + runtimePath + " :: " + rejection);
                    continue;
                }

                if (expectedOuterKey == null)
                {
                    expectedMagic = context.Magic;
                    expectedOuterKey = context.OuterKey;
                    expectedTrailerLength = context.TrailerLength;
                }
                else if (context.TrailerLength != expectedTrailerLength ||
                         !BytesEqual(context.Magic, expectedMagic) ||
                         !BytesEqual(context.OuterKey, expectedOuterKey))
                {
                    throw new InvalidDataException(
                        "Runtime image uses a different protection profile: " + runtimePath);
                }
                contexts.Add(context);
            }

            if (contexts.Count == 0)
                throw new InvalidDataException(
                    "No runtime image matched the currently installed encrypted assemblies. Re-run the runtime dump first.");

            byte[] innerStream = null;
            int protectedPairs = 0;
            int plainPairs = 0;
            int exceptionTables = 0;
            int runtimeImagesWithMethods = 0;

            for (int i = 0; i < contexts.Count; i++)
            {
                CalibrationContext context = contexts[i];
                string directory = Path.GetDirectoryName(context.RuntimePath);
                string[] silFiles = Directory.GetFiles(directory, "0x06*.bin", SearchOption.TopDirectoryOnly);
                Array.Sort(silFiles, StringComparer.OrdinalIgnoreCase);
                if (silFiles.Length == 0) continue;
                runtimeImagesWithMethods++;

                var image = new ManagedPeImage(context.RuntimeBytes);
                for (int fileIndex = 0; fileIndex < silFiles.Length; fileIndex++)
                {
                    Sil2Method live = Sil2Reader.Read(silFiles[fileIndex]);
                    MethodDefinitionRecord method = image.GetMethodByToken(live.Token);
                    MethodBodyInfo body = image.ReadMethodBody(method);
                    if (body.CodeSize != live.Il.Length)
                        throw new InvalidDataException(
                            "Runtime IL length does not match the method header: " + method.DisplayName);
                    if (body.MaxStack != live.MaxStack)
                        throw new InvalidDataException(
                            "Runtime MaxStack does not match the method header: " + method.DisplayName);
                    if (body.InitLocals != live.InitLocals)
                        throw new InvalidDataException(
                            "Runtime InitLocals does not match the method header: " + method.DisplayName);

                    ValidateExceptionTables(image.ReadExceptionClauses(body), live.ExceptionClauses, method.DisplayName);
                    if (live.ExceptionClauses.Count > 0) exceptionTables++;
                    IlValidator.Validate(live.Il, image, method.DisplayName);

                    byte[] storedCode = image.ReadMethodCode(body);
                    if ((method.ImplFlags & 0x8000) != 0)
                    {
                        byte[] candidateStream = Xor(storedCode, live.Il);
                        innerStream = MergeStream(innerStream, candidateStream, live.Path);
                        protectedPairs++;
                    }
                    else
                    {
                        if (!BytesEqual(storedCode, live.Il))
                            throw new InvalidDataException(
                                "An unprotected method differs from its runtime IL: " + method.DisplayName);
                        plainPairs++;
                    }
                }
            }

            if (innerStream == null || protectedPairs == 0)
                throw new InvalidDataException(
                    "No protected SIL2 method pair was available. The runtime dump must include structured method files.");

            var profile = new ProtectionProfile
            {
                CreatedUtc = DateTime.UtcNow,
                TrailerLength = expectedTrailerLength,
                ProtectedMagic = expectedMagic,
                OuterKey = expectedOuterKey,
                InnerStream = innerStream,
                RuntimeImagesUsed = contexts.Count,
                ProtectedMethodPairs = protectedPairs,
                PlainMethodPairs = plainPairs,
                ExceptionTablesValidated = exceptionTables
            };
            profile.Validate();
            Console.WriteLine(
                "Calibration RuntimeImages=" + contexts.Count +
                " RuntimeImagesWithMethods=" + runtimeImagesWithMethods +
                " RejectedImages=" + rejectedImages);
            Console.WriteLine(
                "Calibration ProtectedPairs=" + protectedPairs +
                " PlainPairs=" + plainPairs +
                " ExceptionTables=" + exceptionTables +
                " InnerStreamLength=" + innerStream.Length);
            return profile;
        }

        private static bool TryCreateContext(
            string encryptedPath,
            string runtimePath,
            out CalibrationContext context,
            out string rejection)
        {
            context = null;
            rejection = string.Empty;
            try
            {
                byte[] encrypted = File.ReadAllBytes(encryptedPath);
                byte[] runtime = File.ReadAllBytes(runtimePath);
                int trailerLength = encrypted.Length - runtime.Length;
                if (runtime.Length < OuterKeyLength || trailerLength <= 0)
                {
                    rejection = "lengths do not match the protected format";
                    return false;
                }

                var key = new byte[OuterKeyLength];
                for (int i = 0; i < key.Length; i++) key[i] = (byte)(encrypted[i] ^ runtime[i]);
                for (int i = 0; i < runtime.Length; i++)
                {
                    if ((byte)(encrypted[i] ^ key[i & (OuterKeyLength - 1)]) != runtime[i])
                    {
                        rejection = "outer XOR stream validation failed";
                        return false;
                    }
                }

                var magic = new byte[MagicLength];
                Buffer.BlockCopy(encrypted, 0, magic, 0, magic.Length);
                new ManagedPeImage(runtime);
                context = new CalibrationContext(
                    encryptedPath,
                    runtimePath,
                    runtime,
                    trailerLength,
                    magic,
                    key);
                return true;
            }
            catch (Exception ex)
            {
                rejection = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string GetEncryptedFileName(string runtimePath)
        {
            string name = Path.GetFileName(runtimePath);
            if (!name.EndsWith(RuntimeReadSuffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected runtime image name: " + name);
            return name.Substring(0, name.Length - RuntimeReadSuffix.Length) + ".dll";
        }

        private static byte[] Xor(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) throw new InvalidDataException("XOR inputs have different lengths.");
            var result = new byte[left.Length];
            for (int i = 0; i < result.Length; i++) result[i] = (byte)(left[i] ^ right[i]);
            return result;
        }

        private static byte[] MergeStream(byte[] current, byte[] candidate, string source)
        {
            if (current == null) return candidate;
            int sharedLength = Math.Min(current.Length, candidate.Length);
            for (int i = 0; i < sharedLength; i++)
            {
                if (current[i] != candidate[i])
                    throw new InvalidDataException(
                        "Method XOR streams disagree at offset " + i + ": " + source);
            }
            return candidate.Length > current.Length ? candidate : current;
        }

        private static void ValidateExceptionTables(
            IList<ExceptionClauseRecord> stored,
            IList<Sil2ExceptionClause> live,
            string methodName)
        {
            if (stored.Count != live.Count)
                throw new InvalidDataException("Exception table count differs for " + methodName + ".");
            for (int i = 0; i < stored.Count; i++)
            {
                ExceptionClauseRecord left = stored[i];
                Sil2ExceptionClause right = live[i];
                if (left.Flags != right.Flags ||
                    left.TryOffset != right.TryOffset ||
                    left.TryLength != right.TryLength ||
                    left.HandlerOffset != right.HandlerOffset ||
                    left.HandlerLength != right.HandlerLength ||
                    ((left.Flags & 1) != 0 && left.ClassTokenOrFilterOffset != (uint)right.FilterOffset))
                    throw new InvalidDataException("Exception table differs for " + methodName + ".");
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private sealed class CalibrationContext
        {
            public readonly string EncryptedPath;
            public readonly string RuntimePath;
            public readonly byte[] RuntimeBytes;
            public readonly int TrailerLength;
            public readonly byte[] Magic;
            public readonly byte[] OuterKey;

            public CalibrationContext(
                string encryptedPath,
                string runtimePath,
                byte[] runtimeBytes,
                int trailerLength,
                byte[] magic,
                byte[] outerKey)
            {
                EncryptedPath = encryptedPath;
                RuntimePath = runtimePath;
                RuntimeBytes = runtimeBytes;
                TrailerLength = trailerLength;
                Magic = magic;
                OuterKey = outerKey;
            }
        }
    }
}
