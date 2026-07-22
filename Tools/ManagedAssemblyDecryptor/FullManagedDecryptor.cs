using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ASWDEBUG.Tools.ManagedAssemblyDecryptor
{
    internal static class FullManagedDecryptor
    {
        private const ushort ProtectedMethodFlag = 0x8000;

        public static FullDecryptResult Decrypt(
            string managedDir,
            string outputRoot,
            ProtectionProfile profile,
            string mode)
        {
            if (!Directory.Exists(managedDir))
                throw new DirectoryNotFoundException("Managed directory not found: " + managedDir);
            profile.Validate();

            string[] files = Directory.GetFiles(managedDir, "*.dll", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            var prepared = new List<PreparedAssembly>();
            var unrecognized = new List<string>();
            int protectedAssemblies = 0;
            int copiedManagedAssemblies = 0;
            int ignoredNativeAssemblies = 0;
            int protectedMethods = 0;
            int validatedMethodBodies = 0;

            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                byte[] input = File.ReadAllBytes(path);
                if (BinaryUtil.HasPrefix(input, profile.ProtectedMagic))
                {
                    PreparedAssembly assembly = PrepareProtected(path, input, profile);
                    prepared.Add(assembly);
                    protectedAssemblies++;
                    protectedMethods += assembly.ProtectedMethods;
                    validatedMethodBodies += assembly.ValidatedMethodBodies;
                    continue;
                }

                if (input.Length >= 2 && input[0] == 0x4D && input[1] == 0x5A)
                {
                    try
                    {
                        new ManagedPeImage(input);
                        prepared.Add(PreparedAssembly.CreateCopiedManaged(path, input));
                        copiedManagedAssemblies++;
                    }
                    catch (InvalidDataException)
                    {
                        ignoredNativeAssemblies++;
                    }
                    continue;
                }

                unrecognized.Add(path);
            }

            if (unrecognized.Count > 0)
                throw new InvalidDataException(
                    "Found non-MZ DLLs that do not match this protection profile: " +
                    string.Join(", ", unrecognized.ToArray()) +
                    ". Recalibrate against the current client before decrypting.");
            if (protectedAssemblies == 0)
                throw new InvalidDataException(
                    "No protected assemblies matched this profile. Recalibrate against the current client.");

            Directory.CreateDirectory(outputRoot);
            string runDirectory = CreateUniqueRunDirectory(outputRoot);
            string fullManagedDirectory = Path.Combine(runDirectory, "FULL_MANAGED");
            Directory.CreateDirectory(fullManagedDirectory);

            for (int i = 0; i < prepared.Count; i++)
            {
                string outputPath = Path.Combine(fullManagedDirectory, prepared[i].Name);
                File.WriteAllBytes(outputPath, prepared[i].OutputBytes);
                prepared[i].OutputPath = outputPath;
            }

            string profilePath = Path.Combine(runDirectory, "protection_profile.txt");
            profile.Save(profilePath);
            string manifestPath = Path.Combine(runDirectory, "full_managed_manifest.txt");
            File.WriteAllText(
                manifestPath,
                BuildManifest(
                    mode,
                    managedDir,
                    profile,
                    prepared,
                    protectedAssemblies,
                    copiedManagedAssemblies,
                    ignoredNativeAssemblies,
                    protectedMethods,
                    validatedMethodBodies),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(runDirectory, "__complete.txt"),
                "Completed=" + DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine,
                new UTF8Encoding(false));

            return new FullDecryptResult(
                runDirectory,
                fullManagedDirectory,
                profilePath,
                manifestPath,
                protectedAssemblies,
                copiedManagedAssemblies,
                protectedMethods,
                validatedMethodBodies);
        }

        private static PreparedAssembly PrepareProtected(
            string inputPath,
            byte[] encrypted,
            ProtectionProfile profile)
        {
            if (encrypted.Length <= profile.TrailerLength)
                throw new InvalidDataException("Protected assembly is shorter than its trailer: " + inputPath);

            int outputLength = encrypted.Length - profile.TrailerLength;
            var outerDecrypted = new byte[outputLength];
            for (int i = 0; i < outerDecrypted.Length; i++)
                outerDecrypted[i] = (byte)(encrypted[i] ^ profile.OuterKey[i & (profile.OuterKey.Length - 1)]);

            var image = new ManagedPeImage(outerDecrypted);
            var fullyDecrypted = new byte[outerDecrypted.Length];
            Buffer.BlockCopy(outerDecrypted, 0, fullyDecrypted, 0, outerDecrypted.Length);
            int protectedMethods = 0;
            int maxProtectedCodeSize = 0;
            int maxProtectedToken = 0;

            for (int i = 0; i < image.Methods.Count; i++)
            {
                MethodDefinitionRecord method = image.Methods[i];
                if ((method.ImplFlags & ProtectedMethodFlag) == 0) continue;
                MethodBodyInfo body = image.ReadMethodBody(method);
                if (body.CodeSize > profile.InnerStream.Length)
                    throw new InvalidDataException(
                        "Method stream is too short for " + Path.GetFileName(inputPath) + " " +
                        method.DisplayName + ": code=" + body.CodeSize +
                        " stream=" + profile.InnerStream.Length + ". Recalibrate with a longer runtime method sample.");

                for (int codeIndex = 0; codeIndex < body.CodeSize; codeIndex++)
                    fullyDecrypted[body.CodeOffset + codeIndex] ^= profile.InnerStream[codeIndex];
                WriteUInt16(
                    fullyDecrypted,
                    method.ImplFlagsOffset,
                    (ushort)(method.ImplFlags & ~ProtectedMethodFlag));
                protectedMethods++;
                if (body.CodeSize > maxProtectedCodeSize)
                {
                    maxProtectedCodeSize = body.CodeSize;
                    maxProtectedToken = method.Token;
                }
            }

            var validatedImage = new ManagedPeImage(fullyDecrypted);
            if (validatedImage.Methods.Count != image.Methods.Count)
                throw new InvalidDataException("MethodDef count changed after decryption: " + inputPath);

            int remainingProtectedFlags = 0;
            int validatedBodies = 0;
            for (int i = 0; i < validatedImage.Methods.Count; i++)
            {
                MethodDefinitionRecord method = validatedImage.Methods[i];
                if ((method.ImplFlags & ProtectedMethodFlag) != 0) remainingProtectedFlags++;
                if (method.Rva != 0) validatedBodies += validatedImage.ValidateMethodBody(method);
            }
            if (remainingProtectedFlags != 0)
                throw new InvalidDataException(
                    "Protected MethodImpl flags remain after decryption: " + inputPath);

            return new PreparedAssembly(
                Path.GetFileName(inputPath),
                "decrypted",
                encrypted,
                fullyDecrypted,
                protectedMethods,
                validatedBodies,
                maxProtectedCodeSize,
                maxProtectedToken);
        }

        private static string BuildManifest(
            string mode,
            string managedDir,
            ProtectionProfile profile,
            IList<PreparedAssembly> assemblies,
            int protectedAssemblies,
            int copiedManagedAssemblies,
            int ignoredNativeAssemblies,
            int protectedMethods,
            int validatedMethodBodies)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Format=ASWDEBUG.ManagedAssemblyDecryptor.FullManaged.v1");
            builder.AppendLine("Time=" + DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            builder.AppendLine("Mode=" + mode);
            builder.AppendLine("ManagedDir=" + managedDir);
            builder.AppendLine("ProtectedMagic=" + BinaryUtil.ToHex(profile.ProtectedMagic));
            builder.AppendLine("TrailerLength=" + profile.TrailerLength.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("OuterKeySHA256=" + profile.OuterKeySha256);
            builder.AppendLine("InnerStreamLength=" + profile.InnerStream.Length.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("InnerStreamSHA256=" + profile.InnerStreamSha256);
            builder.AppendLine("ProtectedAssemblies=" + protectedAssemblies.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("CopiedManagedAssemblies=" + copiedManagedAssemblies.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("IgnoredNativeAssemblies=" + ignoredNativeAssemblies.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("ProtectedMethods=" + protectedMethods.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("ValidatedMethodBodies=" + validatedMethodBodies.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(
                "Name|Mode|InputLength|OutputLength|ProtectedMethods|ValidatedBodies|MaxProtectedCodeSize|" +
                "MaxProtectedToken|InputSHA256|OutputSHA256|Output");
            for (int i = 0; i < assemblies.Count; i++)
            {
                PreparedAssembly item = assemblies[i];
                builder.Append(item.Name).Append('|')
                    .Append(item.Mode).Append('|')
                    .Append(item.InputLength.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.OutputBytes.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.ProtectedMethods.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.ValidatedMethodBodies.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.MaxProtectedCodeSize.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.MaxProtectedToken == 0 ? string.Empty : "0x" + item.MaxProtectedToken.ToString("X8")).Append('|')
                    .Append(item.InputSha256).Append('|')
                    .Append(item.OutputSha256).Append('|')
                    .Append(item.OutputPath).AppendLine();
            }
            return builder.ToString();
        }

        private static string CreateUniqueRunDirectory(string outputRoot)
        {
            string baseName = "OFFLINE_FULL_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string candidate = Path.Combine(outputRoot, baseName);
            int suffix = 1;
            while (Directory.Exists(candidate))
            {
                candidate = Path.Combine(
                    outputRoot,
                    baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture));
                suffix++;
            }
            Directory.CreateDirectory(candidate);
            return candidate;
        }

        private static void WriteUInt16(byte[] value, int offset, ushort data)
        {
            if (offset < 0 || offset > value.Length - 2)
                throw new InvalidDataException("MethodImpl flag offset is outside the assembly.");
            value[offset] = (byte)data;
            value[offset + 1] = (byte)(data >> 8);
        }

        private sealed class PreparedAssembly
        {
            public readonly string Name;
            public readonly string Mode;
            public readonly int InputLength;
            public readonly string InputSha256;
            public readonly byte[] OutputBytes;
            public readonly string OutputSha256;
            public readonly int ProtectedMethods;
            public readonly int ValidatedMethodBodies;
            public readonly int MaxProtectedCodeSize;
            public readonly int MaxProtectedToken;
            public string OutputPath;

            public PreparedAssembly(
                string name,
                string mode,
                byte[] input,
                byte[] output,
                int protectedMethods,
                int validatedMethodBodies,
                int maxProtectedCodeSize,
                int maxProtectedToken)
            {
                Name = name;
                Mode = mode;
                InputLength = input.Length;
                InputSha256 = BinaryUtil.ComputeSha256(input);
                OutputBytes = output;
                OutputSha256 = BinaryUtil.ComputeSha256(output);
                ProtectedMethods = protectedMethods;
                ValidatedMethodBodies = validatedMethodBodies;
                MaxProtectedCodeSize = maxProtectedCodeSize;
                MaxProtectedToken = maxProtectedToken;
            }

            public static PreparedAssembly CreateCopiedManaged(string path, byte[] input)
            {
                var output = new byte[input.Length];
                Buffer.BlockCopy(input, 0, output, 0, input.Length);
                return new PreparedAssembly(
                    Path.GetFileName(path),
                    "copied-managed",
                    input,
                    output,
                    0,
                    0,
                    0,
                    0);
            }
        }
    }

    internal sealed class FullDecryptResult
    {
        public readonly string RunDirectory;
        public readonly string FullManagedDirectory;
        public readonly string ProfilePath;
        public readonly string ManifestPath;
        public readonly int ProtectedAssemblies;
        public readonly int CopiedManagedAssemblies;
        public readonly int ProtectedMethods;
        public readonly int ValidatedMethodBodies;

        public FullDecryptResult(
            string runDirectory,
            string fullManagedDirectory,
            string profilePath,
            string manifestPath,
            int protectedAssemblies,
            int copiedManagedAssemblies,
            int protectedMethods,
            int validatedMethodBodies)
        {
            RunDirectory = runDirectory;
            FullManagedDirectory = fullManagedDirectory;
            ProfilePath = profilePath;
            ManifestPath = manifestPath;
            ProtectedAssemblies = protectedAssemblies;
            CopiedManagedAssemblies = copiedManagedAssemblies;
            ProtectedMethods = protectedMethods;
            ValidatedMethodBodies = validatedMethodBodies;
        }
    }
}
