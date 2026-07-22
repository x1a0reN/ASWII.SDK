using System;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal sealed class CompactRainQuery
    {
        internal const byte WalkAction = 0;
        internal const byte JumpAction = 1;
        internal const byte DropAction = 2;

        private readonly CompactRainPathfinder _pathfinder;
        private int _epoch;
        private int _activeEpoch;

        internal CompactRainQuery(CompactRainNavDataset dataset)
        {
            _pathfinder = new CompactRainPathfinder(dataset);
        }

        internal CompactRainSearchStatus Status { get { return _pathfinder.Status; } }
        internal CompactRainPathResult Result { get { return _pathfinder.Result; } }
        internal string Detail { get { return _pathfinder.Detail; } }
        internal long WorkspaceBytes { get { return _pathfinder.WorkspaceBytes; } }

        internal int Begin(CompactRainPoint start, CompactRainPoint goal,
            CompactRainPathCapabilities capabilities, float maximumHorizontalProjection,
            float maximumVerticalProjection)
        {
            _epoch++;
            if (_epoch == int.MaxValue) _epoch = 1;
            _activeEpoch = _epoch;
            _pathfinder.Begin(start, goal, capabilities,
                maximumHorizontalProjection, maximumVerticalProjection);
            return _activeEpoch;
        }

        internal CompactRainSearchStatus Tick(int epoch, int maximumExpansions,
            double maximumMilliseconds)
        {
            if (epoch != _activeEpoch) return CompactRainSearchStatus.Cancelled;
            return _pathfinder.Step(maximumExpansions, maximumMilliseconds);
        }

        internal void Cancel(int epoch)
        {
            if (epoch != _activeEpoch) return;
            _pathfinder.Cancel();
            _activeEpoch = 0;
        }

        internal bool TryFindPath(CompactRainPoint start, CompactRainPoint goal,
            CompactRainPathCapabilities capabilities, int expansionBatch,
            int maximumTotalExpansions, out CompactRainPathResult result, out string detail)
        {
            result = null;
            int epoch = Begin(start, goal, capabilities, 1.25f, 2.25f);
            if (_pathfinder.Status == CompactRainSearchStatus.Complete)
            {
                result = _pathfinder.Result;
                detail = _pathfinder.Detail;
                return true;
            }
            if (_pathfinder.Status == CompactRainSearchStatus.Failed)
            {
                detail = _pathfinder.Detail;
                return false;
            }
            if (expansionBatch <= 0) expansionBatch = 4096;
            if (maximumTotalExpansions <= 0) maximumTotalExpansions = 1000000;
            while (_pathfinder.Status == CompactRainSearchStatus.Pending &&
                _pathfinder.ExpandedNodes < maximumTotalExpansions)
                Tick(epoch, expansionBatch, 0.0);
            if (_pathfinder.Status == CompactRainSearchStatus.Pending)
            {
                Cancel(epoch);
                detail = "expansion_limit=" + maximumTotalExpansions;
                return false;
            }
            detail = _pathfinder.Detail;
            result = _pathfinder.Result;
            return _pathfinder.Status == CompactRainSearchStatus.Complete && result != null;
        }
    }
}
