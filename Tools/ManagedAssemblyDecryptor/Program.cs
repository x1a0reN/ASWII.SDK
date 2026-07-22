using System;
using System.IO;

namespace ASWDEBUG.Tools.ManagedAssemblyDecryptor
{
    internal static class Program
    {
        private const string CalibrateAndDecryptCommand = "calibrate-and-decrypt";
        private const string CalibrateCommand = "calibrate";
        private const string DecryptCommand = "decrypt";

        private static int Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            try
            {
                string command = args[0].Trim().ToLowerInvariant();
                if (command == CalibrateAndDecryptCommand)
                {
                    RequireArgumentCount(args, 4);
                    string managedDir = Path.GetFullPath(args[1]);
                    string runtimeDumpRoot = Path.GetFullPath(args[2]);
                    string outputRoot = Path.GetFullPath(args[3]);

                    ProtectionProfile profile = ProtectionCalibrator.Calibrate(managedDir, runtimeDumpRoot);
                    FullDecryptResult result = FullManagedDecryptor.Decrypt(
                        managedDir,
                        outputRoot,
                        profile,
                        CalibrateAndDecryptCommand);
                    PrintResult(profile, result);
                    return 0;
                }

                if (command == CalibrateCommand)
                {
                    RequireArgumentCount(args, 4);
                    string managedDir = Path.GetFullPath(args[1]);
                    string runtimeDumpRoot = Path.GetFullPath(args[2]);
                    string profilePath = Path.GetFullPath(args[3]);

                    ProtectionProfile profile = ProtectionCalibrator.Calibrate(managedDir, runtimeDumpRoot);
                    profile.Save(profilePath);
                    Console.WriteLine("Profile=" + profilePath);
                    Console.WriteLine("OuterKeySHA256=" + profile.OuterKeySha256);
                    Console.WriteLine("InnerStreamLength=" + profile.InnerStream.Length);
                    Console.WriteLine("InnerStreamSHA256=" + profile.InnerStreamSha256);
                    Console.WriteLine("RuntimeImages=" + profile.RuntimeImagesUsed +
                                      " ProtectedMethodPairs=" + profile.ProtectedMethodPairs);
                    return 0;
                }

                if (command == DecryptCommand)
                {
                    RequireArgumentCount(args, 4);
                    string managedDir = Path.GetFullPath(args[1]);
                    string profilePath = Path.GetFullPath(args[2]);
                    string outputRoot = Path.GetFullPath(args[3]);

                    ProtectionProfile profile = ProtectionProfile.Load(profilePath);
                    FullDecryptResult result = FullManagedDecryptor.Decrypt(
                        managedDir,
                        outputRoot,
                        profile,
                        DecryptCommand);
                    PrintResult(profile, result);
                    return 0;
                }

                Console.Error.WriteLine("Unknown command: " + args[0]);
                PrintUsage();
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static void PrintResult(ProtectionProfile profile, FullDecryptResult result)
        {
            Console.WriteLine("Output=" + result.RunDirectory);
            Console.WriteLine("FullManaged=" + result.FullManagedDirectory);
            Console.WriteLine("Profile=" + result.ProfilePath);
            Console.WriteLine("Manifest=" + result.ManifestPath);
            Console.WriteLine("ProtectedAssemblies=" + result.ProtectedAssemblies +
                              " CopiedManagedAssemblies=" + result.CopiedManagedAssemblies);
            Console.WriteLine("ProtectedMethods=" + result.ProtectedMethods +
                              " ValidatedMethodBodies=" + result.ValidatedMethodBodies);
            Console.WriteLine("OuterKeySHA256=" + profile.OuterKeySha256);
            Console.WriteLine("InnerStreamLength=" + profile.InnerStream.Length);
            Console.WriteLine("InnerStreamSHA256=" + profile.InnerStreamSha256);
        }

        private static void RequireArgumentCount(string[] args, int expected)
        {
            if (args.Length != expected)
            {
                PrintUsage();
                throw new ArgumentException(
                    "Command '" + args[0] + "' expects " + (expected - 1) + " arguments.");
            }
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine(
                "  ManagedAssemblyDecryptor calibrate-and-decrypt <ManagedDir> <RuntimeDumpRoot> <OutputRoot>");
            Console.Error.WriteLine(
                "  ManagedAssemblyDecryptor calibrate <ManagedDir> <RuntimeDumpRoot> <ProfilePath>");
            Console.Error.WriteLine(
                "  ManagedAssemblyDecryptor decrypt <ManagedDir> <ProfilePath> <OutputRoot>");
        }
    }
}
