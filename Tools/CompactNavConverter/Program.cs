using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && string.Equals(args[0], "--stress", StringComparison.OrdinalIgnoreCase))
                    return RunStress(args[1], 1000);
                if (args.Length == 2 && string.Equals(args[0], "--pathtest", StringComparison.OrdinalIgnoreCase))
                    return RunPathTest(args[1]);
                if (args.Length == 2 && string.Equals(args[0], "--safetytest", StringComparison.OrdinalIgnoreCase))
                    return RunSafetyTest(args[1]);
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
                    Console.Error.WriteLine("   or: CompactNavConverter --pathtest <level33.aswnav>");
                    Console.Error.WriteLine("   or: CompactNavConverter --safetytest <level33.aswnav>");
                    Console.Error.WriteLine("   or: CompactNavConverter --stress <level33.aswnav>");
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

        private static int RunPathTest(string path)
        {
            CompactRainNavLoadResult load;
            CompactRainNavDataset dataset = CompactRainNavLoader.Load(path, out load);
            CompactRainPathCapabilities capabilities = new CompactRainPathCapabilities();
            capabilities.AllowJump = true;
            capabilities.JumpHeight = 2.4f;
            capabilities.JumpVelocity = 8.0f;
            capabilities.RunSpeed = 8.5f;
            capabilities.MaximumDrop = 8.0f;
            CompactRainQuery query = new CompactRainQuery(dataset);

            int[] componentSizes = new int[dataset.ComponentCount];
            for (int i = 0; i < dataset.PolyCount; i++) componentSizes[dataset.GetPoly(i).Component]++;
            int largestComponent = 0;
            for (int i = 1; i < componentSizes.Length; i++)
                if (componentSizes[i] > componentSizes[largestComponent]) largestComponent = i;
            int startPoly = -1;
            for (int i = 0; i < dataset.PolyCount; i++)
                if (dataset.GetPoly(i).Component == largestComponent) { startPoly = i; break; }
            if (startPoly < 0) throw new InvalidOperationException("pathtest_no_start_poly");
            CompactRainPoint start = TriangleCentroid(dataset, startPoly);
            int goalPoly = -1;
            float bestDistanceScore = float.MaxValue;
            for (int i = startPoly + 1; i < dataset.PolyCount; i++)
            {
                if (dataset.GetPoly(i).Component != largestComponent) continue;
                CompactRainPoint candidate = TriangleCentroid(dataset, i);
                float distance = CompactRainPoint.DistanceXZ(start, candidate);
                if (distance < 80f || distance > 180f) continue;
                float score = Math.Abs(distance - 120f);
                if (score >= bestDistanceScore) continue;
                bestDistanceScore = score;
                goalPoly = i;
            }
            if (goalPoly < 0) throw new InvalidOperationException("pathtest_no_goal_poly");
            CompactRainPoint goal = TriangleCentroid(dataset, goalPoly);
            CompactRainPathResult first;
            string firstDetail;
            bool firstOk = query.TryFindPath(start, goal, capabilities, 4096, 1000000,
                out first, out firstDetail);
            CompactRainPathResult second;
            string secondDetail;
            bool secondOk = query.TryFindPath(start, goal, capabilities, 4096, 1000000,
                out second, out secondDetail);
            bool deterministic = firstOk && secondOk && PathsEqual(first, second);
            Console.WriteLine("normal_path ok={0}/{1} deterministic={2} component={3} component_polys={4}",
                firstOk, secondOk, deterministic, largestComponent, componentSizes[largestComponent]);
            Console.WriteLine("normal_detail={0} cost={1:0.000} portals={2} waypoints={3} actions={4}",
                firstDetail, first == null ? -1f : first.Cost,
                first == null ? 0 : first.PortalPath.Length,
                first == null ? 0 : first.Waypoints.Length,
                first == null ? 0 : first.ActionCount);

            bool offMeshOk = false;
            string offMeshDetail = "no_candidate";
            CompactRainPathResult offMeshResult = null;
            for (int i = 0; i < dataset.LinkCount && !offMeshOk; i++)
            {
                CompactRainNavLinkRecord link = dataset.GetLink(i);
                if (link.RequiredJumpHeight > capabilities.JumpHeight + 0.05f ||
                    link.RequiredRunSpeed > capabilities.RunSpeed + 0.05f) continue;
                CompactRainNavPortalRecord from = dataset.GetPortal(link.FromPortal);
                CompactRainNavPortalRecord to = dataset.GetPortal(link.ToPortal);
                if (from.PolyCount <= 0 || to.PolyCount <= 0) continue;
                int fromPoly = dataset.GetPortalPolyIndex(from.PolyStart);
                int toPoly = dataset.GetPortalPolyIndex(to.PolyStart);
                CompactRainPathResult candidateResult;
                string candidateDetail;
                bool candidateOk = query.TryFindPath(TriangleCentroid(dataset, fromPoly),
                    TriangleCentroid(dataset, toPoly), capabilities, 4096, 1000000,
                    out candidateResult, out candidateDetail);
                if (!candidateOk || candidateResult == null || candidateResult.ActionCount <= 0) continue;
                offMeshOk = true;
                offMeshDetail = "link=" + i + " " + candidateDetail;
                offMeshResult = candidateResult;
            }
            Console.WriteLine("offmesh_path ok={0} detail={1} portals={2} waypoints={3} actions={4}",
                offMeshOk, offMeshDetail, offMeshResult == null ? 0 : offMeshResult.PortalPath.Length,
                offMeshResult == null ? 0 : offMeshResult.Waypoints.Length,
                offMeshResult == null ? 0 : offMeshResult.ActionCount);
            Console.WriteLine("path_workspace_bytes={0}", query.WorkspaceBytes);
            return deterministic && offMeshOk ? 0 : 4;
        }

        private static CompactRainPoint TriangleCentroid(CompactRainNavDataset dataset, int polyIndex)
        {
            CompactRainNavPolyRecord poly = dataset.GetPoly(polyIndex);
            CompactRainPoint a = dataset.GetVertex(dataset.GetTriangleIndex(poly.TriangleStart));
            CompactRainPoint b = dataset.GetVertex(dataset.GetTriangleIndex(poly.TriangleStart + 1));
            CompactRainPoint c = dataset.GetVertex(dataset.GetTriangleIndex(poly.TriangleStart + 2));
            return new CompactRainPoint((a.X + b.X + c.X) / 3f,
                (a.Y + b.Y + c.Y) / 3f, (a.Z + b.Z + c.Z) / 3f);
        }

        private static bool PathsEqual(CompactRainPathResult left, CompactRainPathResult right)
        {
            if (left == null || right == null || left.Cost != right.Cost ||
                left.Waypoints.Length != right.Waypoints.Length ||
                left.Actions.Length != right.Actions.Length ||
                left.PortalPath.Length != right.PortalPath.Length ||
                left.IncomingLinks.Length != right.IncomingLinks.Length) return false;
            for (int i = 0; i < left.Waypoints.Length; i++)
                if (left.Waypoints[i].X != right.Waypoints[i].X ||
                    left.Waypoints[i].Y != right.Waypoints[i].Y ||
                    left.Waypoints[i].Z != right.Waypoints[i].Z ||
                    left.Actions[i] != right.Actions[i]) return false;
            for (int i = 0; i < left.PortalPath.Length; i++)
                if (left.PortalPath[i] != right.PortalPath[i] ||
                    left.IncomingLinks[i] != right.IncomingLinks[i]) return false;
            return true;
        }

        private static int RunSafetyTest(string path)
        {
            bool cornerRegression = RunSyntheticCornerRegression();
            bool shortCornerRegression = RunSyntheticShortCornerRegression();
            bool gapRegression = RunSyntheticGapRegression();
            bool cliffMarginRegression = RunSyntheticCliffMarginRegression();
            bool topologyRegression = RunSyntheticTopologyRegression();

            CompactRainNavLoadResult load;
            CompactRainNavDataset dataset = CompactRainNavLoader.Load(path, out load);
            CompactRainQuery query = new CompactRainQuery(dataset);
            int invalidTopologyReferences = 0;
            int affectedTopologyPolys = 0;
            for (int polyIndex = 0; polyIndex < dataset.PolyCount; polyIndex++)
            {
                CompactRainNavPolyRecord poly = dataset.GetPoly(polyIndex);
                bool affected = false;
                for (int portalOffset = 0; portalOffset < poly.PortalCount; portalOffset++)
                {
                    int portalIndex = dataset.GetPolyPortalIndex(poly.PortalStart + portalOffset);
                    if (dataset.IsPortalOnPolyBoundary(portalIndex, polyIndex)) continue;
                    invalidTopologyReferences++;
                    affected = true;
                }
                if (affected) affectedTopologyPolys++;
            }
            CompactRainPathCapabilities capabilities = new CompactRainPathCapabilities(true,
                2.4f, 8.0f, 8.5f, 8.0f);
            int[] componentSizes = new int[dataset.ComponentCount];
            for (int i = 0; i < dataset.PolyCount; i++)
                componentSizes[dataset.GetPoly(i).Component]++;
            int component = 0;
            for (int i = 1; i < componentSizes.Length; i++)
                if (componentSizes[i] > componentSizes[component]) component = i;
            List<int> componentPolys = new List<int>(componentSizes[component]);
            for (int i = 0; i < dataset.PolyCount; i++)
                if (dataset.GetPoly(i).Component == component) componentPolys.Add(i);

            int completed = 0;
            int failed = 0;
            int unreachable = 0;
            int unsafeSegments = 0;
            int checkedSegments = 0;
            int attempted = 0;
            const int corpusCount = 64;
            const int maximumAttempts = 128;
            for (int corpus = 0; corpus < maximumAttempts && completed < corpusCount; corpus++)
            {
                attempted++;
                int startAt = (corpus * 7919) % componentPolys.Count;
                int goalAt = (componentPolys.Count / 2 + corpus * 15401) % componentPolys.Count;
                CompactRainPoint start = TriangleCentroid(dataset, componentPolys[startAt]);
                CompactRainPoint goal = TriangleCentroid(dataset, componentPolys[goalAt]);
                for (int seek = 0; seek < 64; seek++)
                {
                    float distance = CompactRainPoint.DistanceXZ(start, goal);
                    if (distance >= 18f && distance <= 180f) break;
                    goalAt = (goalAt + 997) % componentPolys.Count;
                    goal = TriangleCentroid(dataset, componentPolys[goalAt]);
                }

                CompactRainPathResult result;
                string detail;
                if (!query.TryFindPath(start, goal, capabilities, 4096, 1000000,
                    out result, out detail) || result == null)
                {
                    if (detail.StartsWith("no_route", StringComparison.Ordinal) ||
                        detail.StartsWith("start_poly_has_no_portals", StringComparison.Ordinal))
                    {
                        unreachable++;
                        continue;
                    }
                    Console.WriteLine("safety_path_failed corpus={0} detail={1}", corpus, detail);
                    failed++;
                    continue;
                }
                completed++;
                string unsafeDetail;
                int segmentCount;
                if (!ValidateWalkSegments(query, result, out segmentCount, out unsafeDetail))
                {
                    if (unsafeSegments < 6) Console.WriteLine(
                        "safety_segment_failed corpus={0} detail={1}", corpus, unsafeDetail);
                    unsafeSegments++;
                }
                checkedSegments += segmentCount;
            }

            Console.WriteLine("safety_synthetic corner={0} short_corner={1} gap={2} cliff_margin={3} topology={4}",
                cornerRegression, shortCornerRegression, gapRegression,
                cliffMarginRegression, topologyRegression);
            Console.WriteLine("safety_topology invalid_poly_portal_refs={0} affected_polys={1}",
                invalidTopologyReferences, affectedTopologyPolys);
            Console.WriteLine("safety_corpus paths={0}/{1} attempts={2} unreachable={3} failed={4} segments={5} unsafe={6} component={7}",
                completed, corpusCount, attempted, unreachable, failed, checkedSegments,
                unsafeSegments, component);
            return cornerRegression && shortCornerRegression && gapRegression &&
                cliffMarginRegression && topologyRegression && completed == corpusCount &&
                failed == 0 && unsafeSegments == 0 ? 0 : 6;
        }

        private static bool RunSyntheticCornerRegression()
        {
            float[] vertices =
            {
                0f, 0f, 0f,
                3f, 0f, 0f,
                3f, 0f, 1f,
                1f, 0f, 1f,
                1f, 0f, 3f,
                0f, 0f, 3f
            };
            int[] triangles =
            {
                0, 1, 2,
                0, 2, 3,
                0, 3, 5,
                3, 4, 5
            };
            CompactRainNavDataset dataset = CreateSyntheticDataset(vertices, triangles,
                new CompactRainPoint(0.5f, 0f, 0.5f), 1.5f, 1.5f, 3f, 3f);
            CompactRainQuery query = new CompactRainQuery(dataset);
            CompactRainPoint start = new CompactRainPoint(0.5f, 0f, 2.5f);
            CompactRainPoint goal = new CompactRainPoint(2.5f, 0f, 0.5f);
            string directDetail;
            bool directUnsafe = !query.TryValidateWalkSegment(start, goal, out directDetail);
            CompactRainPathResult result;
            string pathDetail;
            bool found = query.TryFindPath(start, goal, new CompactRainPathCapabilities(),
                128, 4096, out result, out pathDetail);
            int segments;
            string segmentDetail;
            bool safe = found && result != null && result.Waypoints.Length >= 3 &&
                ValidateWalkSegments(query, result, out segments, out segmentDetail);
            Console.WriteLine("synthetic_corner direct_unsafe={0} repaired={1} waypoints={2} detail={3}",
                directUnsafe, safe, result == null ? 0 : result.Waypoints.Length, pathDetail);
            return directUnsafe && safe;
        }

        private static bool RunSyntheticGapRegression()
        {
            float[] vertices =
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                1f, 0f, 1f,
                0f, 0f, 1f,
                2f, 0f, 0f,
                3f, 0f, 0f,
                3f, 0f, 1f,
                2f, 0f, 1f
            };
            int[] triangles =
            {
                0, 1, 2,
                0, 2, 3,
                4, 5, 6,
                4, 6, 7
            };
            CompactRainNavDataset dataset = CreateSyntheticDataset(vertices, triangles,
                new CompactRainPoint(0.5f, 0f, 0.5f), 1.5f, 0.5f, 3f, 1f);
            CompactRainQuery query = new CompactRainQuery(dataset);
            CompactRainPoint start = new CompactRainPoint(0.5f, 0f, 0.5f);
            CompactRainPoint goal = new CompactRainPoint(2.5f, 0f, 0.5f);
            string directDetail;
            bool directUnsafe = !query.TryValidateWalkSegment(start, goal, out directDetail);
            CompactRainPathResult result;
            string pathDetail;
            bool rejected = !query.TryFindPath(start, goal, new CompactRainPathCapabilities(),
                128, 4096, out result, out pathDetail);
            Console.WriteLine("synthetic_gap direct_unsafe={0} rejected={1} detail={2}",
                directUnsafe, rejected, pathDetail);
            return directUnsafe && rejected;
        }

        private static bool RunSyntheticShortCornerRegression()
        {
            float[] vertices =
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                1f, 0f, 0.3f,
                0.3f, 0f, 0.3f,
                0.3f, 0f, 1f,
                0f, 0f, 1f
            };
            int[] triangles =
            {
                0, 1, 2,
                0, 2, 3,
                0, 3, 5,
                3, 4, 5
            };
            CompactRainNavDataset dataset = CreateSyntheticDataset(vertices, triangles,
                new CompactRainPoint(0.15f, 0f, 0.15f), 0.5f, 0.5f, 1f, 1f);
            CompactRainQuery query = new CompactRainQuery(dataset);
            CompactRainPoint start = new CompactRainPoint(0.15f, 0f, 0.80f);
            CompactRainPoint goal = new CompactRainPoint(0.80f, 0f, 0.15f);
            string directDetail;
            bool directUnsafe = !query.TryValidateWalkSegment(start, goal, out directDetail);
            CompactRainPathResult result;
            string pathDetail;
            bool found = query.TryFindPath(start, goal, new CompactRainPathCapabilities(),
                128, 4096, out result, out pathDetail);
            int segments;
            string segmentDetail;
            bool safe = found && result != null && result.Waypoints.Length >= 3 &&
                ValidateWalkSegments(query, result, out segments, out segmentDetail);
            Console.WriteLine("synthetic_short_corner direct_unsafe={0} repaired={1} waypoints={2} detail={3}",
                directUnsafe, safe, result == null ? 0 : result.Waypoints.Length, pathDetail);
            return directUnsafe && safe;
        }

        private static bool RunSyntheticCliffMarginRegression()
        {
            float[] vertices =
            {
                0f, 0f, 0f,
                4f, 0f, 0f,
                4f, 0f, 1f,
                0f, 0f, 1f
            };
            int[] triangles = { 0, 1, 2, 0, 2, 3 };
            CompactRainNavDataset dataset = CreateSyntheticDataset(vertices, triangles,
                new CompactRainPoint(2f, 0f, 0.5f), 2f, 0.5f, 4f, 1f);
            CompactRainQuery query = new CompactRainQuery(dataset);
            CompactRainPoint start = new CompactRainPoint(0.25f, 0f, 0.10f);
            CompactRainPoint goal = new CompactRainPoint(3.75f, 0f, 0.10f);
            string directDetail;
            bool directUnsafe = !query.TryValidateWalkSegment(start, goal, out directDetail);
            CompactRainPathResult result;
            string pathDetail;
            bool found = query.TryFindPath(start, goal, new CompactRainPathCapabilities(),
                128, 4096, out result, out pathDetail);
            int segments;
            string segmentDetail;
            bool centered = found && result != null && result.Waypoints.Length >= 3 &&
                ValidateWalkSegments(query, result, out segments, out segmentDetail);
            Console.WriteLine("synthetic_cliff_margin direct_unsafe={0} centered={1} waypoints={2} detail={3}",
                directUnsafe, centered, result == null ? 0 : result.Waypoints.Length, pathDetail);
            return directUnsafe && centered;
        }

        private static bool RunSyntheticTopologyRegression()
        {
            CompactRainNavHeader header = new CompactRainNavHeader();
            header.CellSize = 0.10f;
            header.AgentRadius = 0.45f;
            header.StepHeight = 0.85f;
            header.WalkableHeight = 1.80f;
            header.ComponentCount = 1;
            float[] vertices =
            {
                0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, 1f,
                10f, 0f, 0f, 11f, 0f, 0f, 11f, 0f, 1f, 10f, 0f, 1f
            };
            CompactRainNavPolyRecord first = SyntheticSquarePoly(0, 0, 0,
                0.5f, 0.5f);
            CompactRainNavPolyRecord second = SyntheticSquarePoly(4, 6, 1,
                10.5f, 0.5f);
            CompactRainNavPortalRecord remote = new CompactRainNavPortalRecord();
            remote.VertexOne = 4;
            remote.VertexTwo = 5;
            remote.PolyStart = 0;
            remote.PolyCount = 2;
            remote.CenterX = 10.5f;
            CompactRainNavSurfaceRecord firstSurface = SyntheticSurface(0, 0.5f, 0.5f);
            CompactRainNavSurfaceRecord secondSurface = SyntheticSurface(1, 10.5f, 0.5f);
            CompactRainNavDataset dataset = new CompactRainNavDataset(header, vertices,
                new CompactRainNavPolyRecord[] { first, second },
                new CompactRainNavPortalRecord[] { remote },
                new int[] { 0, 1, 2, 3, 4, 5, 6, 7 },
                new int[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 },
                new int[] { 0, 0 }, new int[] { 0, 1 },
                new CompactRainNavLinkRecord[0], new CompactRainNavBoundaryRecord[0],
                new CompactRainNavSurfaceRecord[] { firstSurface, secondSurface });
            CompactRainQuery query = new CompactRainQuery(dataset);
            CompactRainPathResult result;
            string detail;
            bool rejected = !query.TryFindPath(new CompactRainPoint(0.5f, 0f, 0.5f),
                new CompactRainPoint(10.5f, 0f, 0.5f), new CompactRainPathCapabilities(),
                128, 4096, out result, out detail);
            bool invalidBoundary = !dataset.IsPortalOnPolyBoundary(0, 0);
            Console.WriteLine("synthetic_topology invalid_boundary={0} remote_rejected={1} detail={2}",
                invalidBoundary, rejected, detail);
            return invalidBoundary && rejected;
        }

        private static CompactRainNavPolyRecord SyntheticSquarePoly(int contourStart,
            int triangleStart, int portalStart, float centerX, float centerZ)
        {
            CompactRainNavPolyRecord poly = new CompactRainNavPolyRecord();
            poly.ContourStart = contourStart;
            poly.ContourCount = 4;
            poly.TriangleStart = triangleStart;
            poly.TriangleCount = 6;
            poly.PortalStart = portalStart;
            poly.PortalCount = 1;
            poly.Component = 0;
            poly.CenterX = centerX;
            poly.CenterZ = centerZ;
            poly.BoundsCenterX = centerX;
            poly.BoundsCenterZ = centerZ;
            poly.BoundsSizeX = 1f;
            poly.BoundsSizeY = 0.10f;
            poly.BoundsSizeZ = 1f;
            return poly;
        }

        private static CompactRainNavSurfaceRecord SyntheticSurface(int polyIndex,
            float x, float z)
        {
            CompactRainNavSurfaceRecord surface = new CompactRainNavSurfaceRecord();
            surface.PolyIndex = polyIndex;
            surface.PositionX = x;
            surface.PositionZ = z;
            surface.Component = 0;
            surface.Clearance = 0.50f;
            return surface;
        }

        private static CompactRainNavDataset CreateSyntheticDataset(float[] vertices,
            int[] triangles, CompactRainPoint surfacePoint, float boundsCenterX,
            float boundsCenterZ, float boundsSizeX, float boundsSizeZ)
        {
            CompactRainNavHeader header = new CompactRainNavHeader();
            header.CellSize = 0.10f;
            header.AgentRadius = 0.45f;
            header.StepHeight = 0.85f;
            header.WalkableHeight = 1.80f;
            header.ComponentCount = 1;
            CompactRainNavPolyRecord poly = new CompactRainNavPolyRecord();
            poly.TriangleStart = 0;
            poly.TriangleCount = triangles.Length;
            poly.Component = 0;
            poly.CenterX = surfacePoint.X;
            poly.CenterY = surfacePoint.Y;
            poly.CenterZ = surfacePoint.Z;
            poly.BoundsCenterX = boundsCenterX;
            poly.BoundsCenterY = 0f;
            poly.BoundsCenterZ = boundsCenterZ;
            poly.BoundsSizeX = boundsSizeX;
            poly.BoundsSizeY = 0.10f;
            poly.BoundsSizeZ = boundsSizeZ;
            CompactRainNavSurfaceRecord surface = new CompactRainNavSurfaceRecord();
            surface.PolyIndex = 0;
            surface.PositionX = surfacePoint.X;
            surface.PositionY = surfacePoint.Y;
            surface.PositionZ = surfacePoint.Z;
            surface.Component = 0;
            surface.Clearance = 0.50f;
            return new CompactRainNavDataset(header, vertices,
                new CompactRainNavPolyRecord[] { poly },
                new CompactRainNavPortalRecord[0], new int[0], triangles,
                new int[0], new int[0], new CompactRainNavLinkRecord[0],
                new CompactRainNavBoundaryRecord[0],
                new CompactRainNavSurfaceRecord[] { surface });
        }

        private static bool ValidateWalkSegments(CompactRainQuery query,
            CompactRainPathResult result, out int segmentCount, out string detail)
        {
            segmentCount = 0;
            detail = "ok";
            CompactRainPoint previous = result.StartProjection.Point;
            for (int i = 0; i < result.Waypoints.Length; i++)
            {
                CompactRainPoint current = result.Waypoints[i];
                if (result.Actions[i] == CompactRainQuery.WalkAction)
                {
                    segmentCount++;
                    if (!query.TryValidateWalkSegment(previous, current, out detail))
                    {
                        detail = "waypoint=" + i + " " + detail;
                        return false;
                    }
                }
                previous = current;
            }
            return true;
        }

        private static int RunStress(string path, int cycles)
        {
            CompactRainNavDataset dataset;
            CompactRainNavLoadResult load;
            if (!CompactRainNavLoader.TryLoadProcessSingleton(path, out dataset, out load) || dataset == null)
                throw new InvalidOperationException(load == null ? "stress_load_failed" : load.Status);
            if (CompactRainNavLoader.ProcessLoadCount != 1)
                throw new InvalidOperationException("stress_initial_load_count=" +
                    CompactRainNavLoader.ProcessLoadCount);
            CompactRainQuery query = new CompactRainQuery(dataset);
            CompactRainPathCapabilities capabilities = new CompactRainPathCapabilities(true,
                2.4f, 8.0f, 8.5f, 8.0f);
            int[] componentSizes = new int[dataset.ComponentCount];
            for (int i = 0; i < dataset.PolyCount; i++) componentSizes[dataset.GetPoly(i).Component]++;
            int component = 0;
            for (int i = 1; i < componentSizes.Length; i++)
                if (componentSizes[i] > componentSizes[component]) component = i;
            int startPoly = -1;
            int goalPoly = -1;
            CompactRainPoint start = new CompactRainPoint();
            float bestScore = float.MaxValue;
            for (int i = 0; i < dataset.PolyCount; i++)
            {
                if (dataset.GetPoly(i).Component != component) continue;
                if (startPoly < 0)
                {
                    startPoly = i;
                    start = TriangleCentroid(dataset, i);
                    continue;
                }
                CompactRainPoint candidate = TriangleCentroid(dataset, i);
                float distance = CompactRainPoint.DistanceXZ(start, candidate);
                if (distance < 80f || distance > 180f) continue;
                float score = Math.Abs(distance - 120f);
                if (score >= bestScore) continue;
                bestScore = score;
                goalPoly = i;
            }
            if (startPoly < 0 || goalPoly < 0) throw new InvalidOperationException("stress_corpus_failed");
            CompactRainPoint goal = TriangleCentroid(dataset, goalPoly);
            CompactRainPathResult baseline;
            string detail;
            if (!query.TryFindPath(start, goal, capabilities, 4096, 1000000, out baseline, out detail))
                throw new InvalidOperationException("stress_baseline_failed:" + detail);
            for (int i = 0; i < 32; i++)
            {
                CompactRainPathResult warm;
                if (!query.TryFindPath(start, goal, capabilities, 4096, 1000000, out warm, out detail) ||
                    !PathsEqual(baseline, warm)) throw new InvalidOperationException("stress_warmup=" + i);
            }
            ForceCollection();
            long managedBaseline = GC.GetTotalMemory(false);
            long privateBaseline = GetPrivateBytes();
            long maximumManaged = managedBaseline;
            long maximumPrivate = privateBaseline;
            int mismatches = 0;
            int cancelled = 0;
            Stopwatch elapsed = Stopwatch.StartNew();
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                CompactRainNavDataset sameDataset;
                CompactRainNavLoadResult sameLoad;
                if (!CompactRainNavLoader.TryLoadProcessSingleton(path, out sameDataset, out sameLoad) ||
                    !object.ReferenceEquals(dataset, sameDataset))
                    throw new InvalidOperationException("stress_dataset_reloaded=" + cycle);
                if (CompactRainNavLoader.ProcessLoadCount != 1)
                    throw new InvalidOperationException("stress_load_count=" + cycle + "/" +
                        CompactRainNavLoader.ProcessLoadCount);
                if ((cycle % 50) == 0)
                {
                    int epoch = query.Begin(start, goal, capabilities, 1.25f, 2.25f);
                    query.Tick(epoch, 1, 0.0);
                    query.Cancel(epoch);
                    if (query.Status != CompactRainSearchStatus.Cancelled)
                        throw new InvalidOperationException("stress_cancel_failed=" + cycle);
                    cancelled++;
                }
                CompactRainPathResult result;
                if (!query.TryFindPath(start, goal, capabilities, 4096, 1000000, out result, out detail) ||
                    !PathsEqual(baseline, result)) mismatches++;
                result = null;
                if (((cycle + 1) % 100) != 0) continue;
                ForceCollection();
                long managed = GC.GetTotalMemory(false);
                long privateBytes = GetPrivateBytes();
                if (managed > maximumManaged) maximumManaged = managed;
                if (privateBytes > maximumPrivate) maximumPrivate = privateBytes;
                Console.WriteLine("stress_checkpoint cycle={0} managed={1} managed_delta={2} private={3} private_delta={4}",
                    cycle + 1, managed, managed - managedBaseline,
                    privateBytes, privateBytes - privateBaseline);
            }
            elapsed.Stop();
            ForceCollection();
            long managedFinal = GC.GetTotalMemory(false);
            long privateFinal = GetPrivateBytes();
            long managedGrowth = managedFinal - managedBaseline;
            long privateGrowth = privateFinal - privateBaseline;
            Console.WriteLine("stress_result cycles={0} mismatches={1} cancelled={2} elapsed_ms={3}",
                cycles, mismatches, cancelled, elapsed.ElapsedMilliseconds);
            Console.WriteLine("stress_lifecycle dataset_loads={0} singleton_reuses={1}",
                CompactRainNavLoader.ProcessLoadCount, cycles);
            Console.WriteLine("stress_memory managed_base={0} managed_final={1} managed_growth={2} managed_peak_delta={3}",
                managedBaseline, managedFinal, managedGrowth, maximumManaged - managedBaseline);
            Console.WriteLine("stress_memory private_base={0} private_final={1} private_growth={2} private_peak_delta={3}",
                privateBaseline, privateFinal, privateGrowth, maximumPrivate - privateBaseline);
            bool memoryStable = managedGrowth <= 2L * 1024L * 1024L &&
                privateGrowth <= 16L * 1024L * 1024L;
            return mismatches == 0 && cancelled == 20 &&
                CompactRainNavLoader.ProcessLoadCount == 1 && memoryStable ? 0 : 5;
        }

        private static void ForceCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static long GetPrivateBytes()
        {
            Process process = null;
            try
            {
                process = Process.GetCurrentProcess();
                return process.PrivateMemorySize64;
            }
            finally { if (process != null) process.Dispose(); }
        }
    }
}
