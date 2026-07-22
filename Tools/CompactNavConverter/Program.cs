using System;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
                {
                    CompactRainNavLoadResult load;
                    CompactRainNavDataset dataset = CompactRainNavLoader.Load(args[1], out load);
                    int samples = 0;
                    int failures = 0;
                    int samePoly = 0;
                    float maximumVerticalError = 0f;
                    for (int i = 0; i < dataset.PolyCount; i += 137)
                    {
                        CompactRainNavPolyRecord poly = dataset.GetPoly(i);
                        CompactRainPoint a = dataset.GetVertex(dataset.GetTriangleIndex(poly.TriangleStart));
                        CompactRainPoint b = dataset.GetVertex(dataset.GetTriangleIndex(poly.TriangleStart + 1));
                        CompactRainPoint c = dataset.GetVertex(dataset.GetTriangleIndex(poly.TriangleStart + 2));
                        CompactRainPoint input = new CompactRainPoint((a.X + b.X + c.X) / 3f,
                            (a.Y + b.Y + c.Y) / 3f + 0.25f, (a.Z + b.Z + c.Z) / 3f);
                        CompactRainProjection projection;
                        samples++;
                        bool projected = dataset.SpatialIndex.TryProject(input, 0.02f, 0.75f,
                            out projection);
                        if (!projected || !projection.ExactXZ)
                        {
                            if (failures < 10)
                                Console.WriteLine("projection_failure poly={0} projected={1} result_poly={2} exact={3} h={4:0.000000} v={5:0.000000} input=({6:0.000},{7:0.000},{8:0.000})",
                                    i, projected, projection.PolyIndex, projection.ExactXZ,
                                    projection.HorizontalError, projection.VerticalError,
                                    input.X, input.Y, input.Z);
                            failures++;
                            continue;
                        }
                        if (projection.PolyIndex == i) samePoly++;
                        if (projection.VerticalError > maximumVerticalError)
                            maximumVerticalError = projection.VerticalError;
                    }
                    CompactRainNavHeader header = dataset.Header;
                    CompactRainPoint outside = new CompactRainPoint(
                        header.BoundsCenterX + header.BoundsSizeX + 100f,
                        header.BoundsCenterY + header.BoundsSizeY + 100f,
                        header.BoundsCenterZ + header.BoundsSizeZ + 100f);
                    CompactRainProjection outsideProjection;
                    bool outsideRejected = !dataset.SpatialIndex.TryProject(outside, 1f, 2f,
                        out outsideProjection);
                    Console.WriteLine("selftest samples={0} failures={1} same_poly={2} max_vertical_error={3:0.000000} outside_rejected={4}",
                        samples, failures, samePoly, maximumVerticalError, outsideRejected);
                    return failures == 0 && outsideRejected ? 0 : 3;
                }
                if (args.Length == 2 && string.Equals(args[0], "--load", StringComparison.OrdinalIgnoreCase))
                {
                    CompactRainNavLoadResult load;
                    CompactRainNavDataset dataset = CompactRainNavLoader.Load(args[1], out load);
                    Console.WriteLine("loaded path={0} file_sha256={1}", load.FilePath, load.FileSha256);
                    Console.WriteLine("vertices={0} polys={1} portals={2} links={3} bvh_nodes={4}",
                        dataset.VertexCount, dataset.PolyCount, dataset.PortalCount,
                        dataset.LinkCount, load.BvhNodeCount);
                    Console.WriteLine("file_bytes={0} resident_bytes={1} elapsed_ms={2}",
                        load.FileBytes, load.ResidentDatasetBytes, load.ElapsedMilliseconds);
                    Console.WriteLine("managed_delta={0} private_delta={1}",
                        load.ManagedBytesAfter - load.ManagedBytesBefore,
                        load.PrivateBytesAfter - load.PrivateBytesBefore);
                    return 0;
                }
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
                    Console.Error.WriteLine("   or: CompactNavConverter --load <level33.aswnav>");
                    Console.Error.WriteLine("   or: CompactNavConverter --selftest <level33.aswnav>");
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
