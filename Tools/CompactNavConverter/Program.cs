using System;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && string.Equals(args[0], "--verify", StringComparison.OrdinalIgnoreCase))
                {
                    CompactRainNavHeader header = CompactRainNavConverter.VerifyFile(args[1], null, null);
                    Console.WriteLine("verified path={0} payload={1} graph={2} components={3} safe={4}",
                        args[1], header.PayloadLength, header.RawGraphSize,
                        header.ComponentCount, header.SafeSpawnCount);
                    return 0;
                }
                if (args.Length != 3)
                {
                    Console.Error.WriteLine("usage: CompactNavConverter <level33.rainnav> <level33.rainmeta> <level33.aswnav>");
                    Console.Error.WriteLine("   or: CompactNavConverter --verify <level33.aswnav>");
                    return 2;
                }

                CompactRainNavConversionResult result;
                string status;
                if (!CompactRainNavConverter.TryConvert(args[0], args[1], args[2], out result, out status))
                {
                    Console.Error.WriteLine(status);
                    return 1;
                }
                Console.WriteLine(status);
                Console.WriteLine("output={0}", result.OutputPath);
                Console.WriteLine("sha256={0}", result.OutputSha256);
                Console.WriteLine("payload_sha256={0}", result.PayloadSha256);
                Console.WriteLine("source_nav_sha256={0}", result.SourceNavSha256);
                Console.WriteLine("source_meta_sha256={0}", result.SourceMetaSha256);
                Console.WriteLine("vertices={0} polys={1} portals={2} links={3} boundaries={4} surfaces={5} components={6} safe={7}",
                    result.VertexCount, result.PolyCount, result.PortalCount, result.LinkCount,
                    result.BoundaryCount, result.SurfaceCount, result.ComponentCount, result.SafeSpawnCount);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("fatal={0}:{1}", ex.GetType().Name, ex.Message);
                return 1;
            }
        }
    }
}
