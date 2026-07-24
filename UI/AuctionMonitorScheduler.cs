using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ASWDEBUG.UI
{
    public static partial class AuctionMonitor
    {
        private const int SingleRequestIntervalMs = 0;
        private const int CurrencyRequestIntervalMs = 250;
        private const int FullBurstStartupDelayMs = 1000;
        private const int FullBurstMinimumIntervalMs = 1000;
        private const int FullRequestGapMs = 150;
        private const int SchedulerMaintenanceIntervalMs = 1000;
        private const int BuyResponseTimeoutMs = 20000;
        // LobbyConnection 的 RPC 队列串行执行；当前请求 + 1 条预排请求即可消除帧间空档。
        private const int MaxMonitorRequestsInFlight = 2;
        private const int SchedulerErrorLogIntervalMs = 5000;

        private static readonly object _requestGate = new object();
        private static readonly object _pendingBuyGate = new object();
        private static global::LobbyConnection _requestConnection;
        private static int _requestEpoch;
        private static int _monitorRequestsInFlight;

        private static int _schedulerGeneration;
        private static int _nextTargetTick;
        private static int _nextCurrencyTick;
        private static int _nextFullBurstTick;
        private static int _nextFullTypeTick;
        private static int _nextMaintenanceTick;
        private static int _targetCursor;
        private static int _singleProbePhase;
        private static int _fullPhase = -1;
        private static readonly Dictionary<string, int> _targetProbePhases =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static int _fullRequestToken;
        private static int _nextFullRequestToken;
        private static int _nextBuyToken;
        private static int _buyStartedTick;
        private static int _lastSchedulerErrorTick;
        private static int _droppedParseResponses;

        private static WatchItem[] _cachedWatchSnapshot;
        private static List<WatchEntry> _cachedTargets = new List<WatchEntry>(0);
        private static float _cachedCurrencyWant;

        private sealed class PendingBuy
        {
            internal string Aid;
            internal string Name;
            internal string Type;
            internal int CycleElapsedMs;
            internal int RequestElapsedMs;
            internal DateTime SendUtc;
            internal int BuyToken;
            internal int Generation;
        }

        private static PendingBuy _pendingBuy;

        private sealed class MonitorRequestLease
        {
            internal readonly int Epoch;
            private int _released;

            internal MonitorRequestLease(int epoch)
            {
                Epoch = epoch;
            }

            internal void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    ReleaseMonitorRequest(this);
            }
        }

        private sealed class MonitorRpcCallbackState
        {
            private readonly MonitorRequestLease _lease;
            private readonly Action<string> _handler;

            internal MonitorRpcCallbackState(
                MonitorRequestLease lease,
                Action<string> handler)
            {
                _lease = lease;
                _handler = handler;
            }

            internal void Invoke(string data)
            {
                try
                {
                    if (_handler != null) _handler(data);
                }
                finally
                {
                    if (_lease != null) _lease.Release();
                }
            }
        }

        public static void Tick()
        {
            if (!FeatureEnabled || !_running) return;

            int now = Environment.TickCount;

            try
            {
                global::GameApp app = global::GameApp.Instance;
                global::LobbyConnection conn =
                    app != null ? app.lobby_connection : null;
                if (conn == null)
                {
                    RunSchedulerMaintenance(now);
                    return;
                }

                PrepareMonitorConnection(conn, now);
                int generation = ReadInt(ref _schedulerGeneration);
                RecoverTimedOutBuy(now);
                RefreshCachedTargets();
                PumpPendingBuy(conn, generation);

                if (IsBuyInFlight())
                {
                    RunSchedulerMaintenance(now);
                    return;
                }

                if (IsSingleMode)
                {
                    PumpSingleTarget(conn, generation, now);
                    RunSchedulerMaintenance(now);
                    return;
                }

                if (_cachedCurrencyWant > 0f &&
                    IsTickDue(now, _nextCurrencyTick))
                {
                    if (SendCurrencyBounded(
                        conn,
                        _cachedCurrencyWant,
                        generation))
                    {
                        _nextCurrencyTick =
                            unchecked(now + CurrencyRequestIntervalMs);
                    }
                }

                PumpFullFallback(
                    conn,
                    generation,
                    now,
                    _cachedTargets.Count > 0);

                PumpTargetWindow(conn, _cachedTargets, generation);
                RunSchedulerMaintenance(now);
            }
            catch (Exception ex)
            {
                LogSchedulerFailure("Tick", ex);
            }
        }

        public static bool IsMonitorRpcCallback(
            global::LobbyConnection.RpcCallback callback)
        {
            return callback != null &&
                   callback.Target is MonitorRpcCallbackState;
        }

        private static void SchedulerStart()
        {
            int now = Environment.TickCount;
            Interlocked.Increment(ref _schedulerGeneration);
            _nextTargetTick = now;
            _nextCurrencyTick = now;
            _nextFullBurstTick =
                unchecked(now + FullBurstStartupDelayMs);
            _nextFullTypeTick = now;
            _nextMaintenanceTick = now;
            _targetCursor = 0;
            _singleProbePhase = 0;
            _fullPhase = -1;
            _cachedWatchSnapshot = null;
            _targetProbePhases.Clear();
        }

        private static void SchedulerStop()
        {
            Interlocked.Increment(ref _schedulerGeneration);
            _fullPhase = -1;
            _cachedWatchSnapshot = null;
            _targetProbePhases.Clear();
            ReleasePendingBuy();
        }

        private static void SchedulerOnFullIntervalChanged(int intervalMs)
        {
            int now = Environment.TickCount;
            _fullPhase = -1;
            _nextFullBurstTick = intervalMs <= 0
                ? int.MaxValue
                : unchecked(now + intervalMs);
        }

        private static bool IsSchedulerGenerationActive(int generation)
        {
            return FeatureEnabled &&
                   _running &&
                   generation == ReadInt(ref _schedulerGeneration);
        }

        private static void PumpSingleTarget(
            global::LobbyConnection conn,
            int generation,
            int now)
        {
            if (!IsTickDue(now, _nextTargetTick)) return;

            bool sentAny = false;
            int budget = MaxMonitorRequestsInFlight;
            while (budget-- > 0 &&
                   !IsBuyInFlight() &&
                   GetMonitorRequestsInFlight() <
                   MaxMonitorRequestsInFlight)
            {
                bool sent;
                if (string.Equals(
                    SingleName,
                    CURRENCY_NAME,
                    StringComparison.Ordinal))
                {
                    sent = SingleWant > 0f &&
                           SendCurrencyBounded(
                               conn,
                               SingleWant,
                               generation);
                }
                else
                {
                    var target = new WatchEntry
                    {
                        Id = SingleId,
                        NameCN = SingleName,
                        Price = SingleWant
                    };

                    int t;
                    int st;
                    bool usedProbe = false;
                    if (SingleT == 2 || SingleT == 3)
                    {
                        t = SingleT;
                        st = SingleT == 3 ? SingleST : -1;
                    }
                    else if (TryGetTypeHint(SingleId, out t, out st))
                    {
                        if (t != 3) st = -1;
                    }
                    else
                    {
                        GetProbeType(_singleProbePhase, out t, out st);
                        usedProbe = true;
                    }

                    sent = SendTargetedBounded(
                        conn,
                        t,
                        st,
                        target,
                        generation);
                    if (sent && usedProbe)
                    {
                        _singleProbePhase =
                            (_singleProbePhase + 1) % 3;
                    }
                }

                if (!sent) break;
                sentAny = true;
            }

            if (sentAny)
                _nextTargetTick = unchecked(now + SingleRequestIntervalMs);
        }

        private static void PumpTargetWindow(
            global::LobbyConnection conn,
            List<WatchEntry> targets,
            int generation)
        {
            if (targets == null || targets.Count == 0) return;

            int budget = MaxMonitorRequestsInFlight;
            while (budget-- > 0 &&
                   !IsBuyInFlight() &&
                   GetMonitorRequestsInFlight() <
                   MaxMonitorRequestsInFlight)
            {
                if (!TrySendNextTarget(conn, targets, generation))
                    break;
            }
        }

        private static bool TrySendNextTarget(
            global::LobbyConnection conn,
            List<WatchEntry> targets,
            int generation)
        {
            int count = targets.Count;
            if (count == 0) return false;

            if (_targetCursor < 0 || _targetCursor >= count)
                _targetCursor = 0;

            WatchEntry target = targets[_targetCursor];

            int t;
            int st;
            bool usedProbe = false;
            int probePhase = 0;
            if (TryGetTypeHint(target.Id, out t, out st))
            {
                if (t != 3) st = -1;
                _targetProbePhases.Remove(target.Id);
            }
            else
            {
                if (!_targetProbePhases.TryGetValue(
                    target.Id,
                    out probePhase))
                {
                    probePhase = 0;
                }
                GetProbeType(probePhase, out t, out st);
                usedProbe = true;
            }

            if (!SendTargetedBounded(
                conn,
                t,
                st,
                target,
                generation))
            {
                return false;
            }

            if (usedProbe)
            {
                probePhase = (probePhase + 1) % 3;
                if (probePhase == 0)
                {
                    _targetProbePhases.Remove(target.Id);
                    AdvanceTargetCursor(count);
                }
                else
                {
                    _targetProbePhases[target.Id] = probePhase;
                }
            }
            else
            {
                AdvanceTargetCursor(count);
            }
            return true;
        }

        private static void AdvanceTargetCursor(int count)
        {
            _targetCursor++;
            if (_targetCursor >= count) _targetCursor = 0;
        }

        private static void PumpFullFallback(
            global::LobbyConnection conn,
            int generation,
            int now,
            bool hasTargets)
        {
            int interval = ReadInt(ref FULL_BURST_INTERVAL_MS);
            if (!hasTargets || interval <= 0)
            {
                _fullPhase = -1;
                return;
            }

            if (_fullPhase < 0)
            {
                if (!IsTickDue(now, _nextFullBurstTick)) return;
                _fullPhase = 0;
                _nextFullTypeTick = now;
            }

            if (!IsTickDue(now, _nextFullTypeTick) ||
                ReadInt(ref _fullRequestToken) != 0)
            {
                return;
            }

            int t;
            int st;
            GetProbeType(_fullPhase, out t, out st);

            int fullToken;
            if (!TryAcquireFullRequest(out fullToken)) return;

            if (!SendFullBounded(
                conn,
                t,
                st,
                generation,
                fullToken))
            {
                CompleteFullRequest(fullToken);
                return;
            }

            _fullPhase++;
            _nextFullTypeTick = unchecked(now + FullRequestGapMs);
            if (_fullPhase >= 3)
            {
                _fullPhase = -1;
                _nextFullBurstTick = unchecked(now + interval);
            }
        }

        private static bool SendTargetedBounded(
            global::LobbyConnection conn,
            int t,
            int st,
            WatchEntry target,
            int generation)
        {
            if (IsBuyInFlight()) return false;

            MonitorRequestLease lease = TryAcquireMonitorRequest(conn);
            if (lease == null) return false;

            Dictionary<string, string> args =
                BuildArgs(t, st, target.NameCN, TARGET_PAGE_S);
            DateTime requestStart = DateTime.UtcNow;

            try
            {
                conn.AddTextRpc(
                    "auction_list",
                    CreateMonitorRpcCallback(
                        lease,
                        delegate(string data)
                        {
                            QueueItemParse(
                                data,
                                requestStart,
                                false,
                                generation,
                                0);
                            PumpPendingBuy(conn, generation);
                        }),
                    args);
                return true;
            }
            catch (Exception ex)
            {
                lease.Release();
                LogSchedulerFailure("auction_list targeted", ex);
                return false;
            }
        }

        private static bool SendFullBounded(
            global::LobbyConnection conn,
            int t,
            int st,
            int generation,
            int fullToken)
        {
            if (IsBuyInFlight()) return false;

            MonitorRequestLease lease = TryAcquireMonitorRequest(conn);
            if (lease == null) return false;

            Dictionary<string, string> args =
                BuildArgs(t, st, string.Empty, 9999);
            DateTime requestStart = DateTime.UtcNow;

            try
            {
                conn.AddTextRpc(
                    "auction_list",
                    CreateMonitorRpcCallback(
                        lease,
                        delegate(string data)
                        {
                            QueueItemParse(
                                data,
                                requestStart,
                                true,
                                generation,
                                fullToken);
                            PumpPendingBuy(conn, generation);
                        }),
                    args);
                return true;
            }
            catch (Exception ex)
            {
                lease.Release();
                CompleteFullRequest(fullToken);
                LogSchedulerFailure("auction_list full", ex);
                return false;
            }
        }

        private static bool SendCurrencyBounded(
            global::LobbyConnection conn,
            float want,
            int generation)
        {
            if (IsBuyInFlight()) return false;

            MonitorRequestLease lease = TryAcquireMonitorRequest(conn);
            if (lease == null) return false;

            var args = new Dictionary<string, string>(5);
            args["currency"] = ARG_currency;
            args["order"] = ARG_order;
            args["orderField"] = ARG_orderFieldCurrency;
            args["p"] = ARG_currency_p;
            args["s"] = ARG_currency_s;
            DateTime requestStart = DateTime.UtcNow;

            try
            {
                conn.AddTextRpc(
                    "auction_currency_list",
                    CreateMonitorRpcCallback(
                        lease,
                        delegate(string data)
                        {
                            QueueCurrencyParse(
                                data,
                                requestStart,
                                want,
                                generation);
                            PumpPendingBuy(conn, generation);
                        }),
                    args);
                return true;
            }
            catch (Exception ex)
            {
                lease.Release();
                LogSchedulerFailure("auction_currency_list", ex);
                return false;
            }
        }

        private static void QueueItemParse(
            string data,
            DateTime requestStart,
            bool limitByGroupTopK,
            int generation,
            int fullToken)
        {
            if (!IsSchedulerGenerationActive(generation) ||
                IsBuyInFlight() ||
                string.IsNullOrEmpty(data))
            {
                if (fullToken != 0) CompleteFullRequest(fullToken);
                return;
            }

            // 定向查询固定只回 1 条，直接在回调中解析，避免线程池排队或解析闸门丢包。
            if (!limitByGroupTopK)
            {
                try
                {
                    FastScanAndBuy(
                        data,
                        requestStart,
                        false,
                        generation);
                }
                catch (Exception ex)
                {
                    LogSchedulerFailure("targeted item parse", ex);
                }
                finally
                {
                    if (fullToken != 0)
                        CompleteFullRequest(fullToken);
                }
                return;
            }

            if (!TryEnterParseGate())
            {
                Interlocked.Increment(ref _droppedParseResponses);
                if (fullToken != 0) CompleteFullRequest(fullToken);
                return;
            }

            try
            {
                ThreadPool.QueueUserWorkItem(
                    delegate
                    {
                        try
                        {
                            FastScanAndBuy(
                                data,
                                requestStart,
                                limitByGroupTopK,
                                generation);
                        }
                        catch (Exception ex)
                        {
                            LogSchedulerFailure("item parse", ex);
                        }
                        finally
                        {
                            ExitParseGate();
                            if (fullToken != 0)
                                CompleteFullRequest(fullToken);
                        }
                    });
            }
            catch (Exception ex)
            {
                ExitParseGate();
                if (fullToken != 0) CompleteFullRequest(fullToken);
                LogSchedulerFailure("queue item parse", ex);
            }
        }

        private static void QueueCurrencyParse(
            string data,
            DateTime requestStart,
            float want,
            int generation)
        {
            if (!IsSchedulerGenerationActive(generation) ||
                IsBuyInFlight() ||
                string.IsNullOrEmpty(data))
            {
                return;
            }

            // 金币查询同样固定只回 1 条，同步解析可消除线程池调度延迟。
            try
            {
                FastScanAndBuyCurrency(
                    data,
                    requestStart,
                    want,
                    generation);
            }
            catch (Exception ex)
            {
                LogSchedulerFailure("currency parse", ex);
            }
        }

        private static global::LobbyConnection.RpcCallback
            CreateMonitorRpcCallback(
                MonitorRequestLease lease,
                Action<string> handler)
        {
            var state = new MonitorRpcCallbackState(lease, handler);
            return new global::LobbyConnection.RpcCallback(state.Invoke);
        }

        private static MonitorRequestLease TryAcquireMonitorRequest(
            global::LobbyConnection conn)
        {
            lock (_requestGate)
            {
                if (!object.ReferenceEquals(_requestConnection, conn))
                    return null;
                if (_monitorRequestsInFlight >=
                    MaxMonitorRequestsInFlight)
                {
                    return null;
                }

                _monitorRequestsInFlight++;
                return new MonitorRequestLease(_requestEpoch);
            }
        }

        private static void ReleaseMonitorRequest(
            MonitorRequestLease lease)
        {
            lock (_requestGate)
            {
                if (lease.Epoch != _requestEpoch) return;
                if (_monitorRequestsInFlight > 0)
                    _monitorRequestsInFlight--;
            }
        }

        private static void PrepareMonitorConnection(
            global::LobbyConnection conn,
            int now)
        {
            bool changed = false;
            lock (_requestGate)
            {
                if (!object.ReferenceEquals(_requestConnection, conn))
                {
                    _requestConnection = conn;
                    _requestEpoch++;
                    if (_requestEpoch == 0) _requestEpoch = 1;
                    _monitorRequestsInFlight = 0;
                    changed = true;
                }
            }

            if (!changed) return;

            Interlocked.Increment(ref _schedulerGeneration);
            Interlocked.Exchange(ref _buyInFlight, 0);
            Interlocked.Exchange(ref _buyStartedTick, 0);
            Interlocked.Exchange(ref _fullRequestToken, 0);
            ClearPendingBuy();
            _fullPhase = -1;
            _targetProbePhases.Clear();
            _nextTargetTick = now;
            _nextCurrencyTick = now;
            _nextFullBurstTick =
                unchecked(now + FullBurstStartupDelayMs);
        }

        private static bool TryAcquireFullRequest(out int token)
        {
            token = NextNonZeroToken(ref _nextFullRequestToken);
            if (Interlocked.CompareExchange(
                ref _fullRequestToken,
                token,
                0) == 0)
            {
                return true;
            }

            token = 0;
            return false;
        }

        private static void CompleteFullRequest(int token)
        {
            if (token == 0) return;
            Interlocked.CompareExchange(
                ref _fullRequestToken,
                0,
                token);
        }

        private static bool TryAcquireBuy(int generation, out int token)
        {
            if (!IsSchedulerGenerationActive(generation))
            {
                token = 0;
                return false;
            }

            token = NextNonZeroToken(ref _nextBuyToken);
            if (Interlocked.CompareExchange(
                ref _buyInFlight,
                token,
                0) != 0)
            {
                token = 0;
                return false;
            }

            if (!IsSchedulerGenerationActive(generation))
            {
                ReleaseBuy(token);
                token = 0;
                return false;
            }

            Interlocked.Exchange(
                ref _buyStartedTick,
                Environment.TickCount);
            return true;
        }

        private static void ReleaseBuy(int token)
        {
            if (token == 0) return;
            if (Interlocked.CompareExchange(
                ref _buyInFlight,
                0,
                token) == token)
            {
                Interlocked.Exchange(ref _buyStartedTick, 0);
            }
        }

        private static bool IsBuyInFlight()
        {
            return ReadInt(ref _buyInFlight) != 0;
        }

        private static void RecoverTimedOutBuy(int now)
        {
            int token = ReadInt(ref _buyInFlight);
            if (token == 0) return;

            int started = ReadInt(ref _buyStartedTick);
            if (started == 0 ||
                unchecked((uint)(now - started)) <
                (uint)BuyResponseTimeoutMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                ref _buyInFlight,
                0,
                token) == token)
            {
                Interlocked.Exchange(ref _buyStartedTick, 0);
                FileLogger.Log(
                    "AuctionMonitor",
                    "BUY timeout; request gate released token=" +
                    token.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void QueueBuyRequest(
            string aid,
            string name,
            string type,
            int cycleElapsedMs,
            int requestElapsedMs,
            DateTime sendUtc,
            int buyToken,
            int generation)
        {
            if (!IsSchedulerGenerationActive(generation))
            {
                ReleaseBuy(buyToken);
                return;
            }

            bool queued = false;
            lock (_pendingBuyGate)
            {
                if (_pendingBuy == null &&
                    IsSchedulerGenerationActive(generation))
                {
                    _pendingBuy = new PendingBuy
                    {
                        Aid = aid,
                        Name = name,
                        Type = type,
                        CycleElapsedMs = cycleElapsedMs,
                        RequestElapsedMs = requestElapsedMs,
                        SendUtc = sendUtc,
                        BuyToken = buyToken,
                        Generation = generation
                    };
                    queued = true;
                }
            }

            if (!queued) ReleaseBuy(buyToken);
        }

        private static void PumpPendingBuy(
            global::LobbyConnection conn,
            int generation)
        {
            PendingBuy pending;
            lock (_pendingBuyGate)
            {
                pending = _pendingBuy;
                if (pending == null) return;
                _pendingBuy = null;
            }

            if (pending.Generation != generation ||
                !IsSchedulerGenerationActive(generation) ||
                ReadInt(ref _buyInFlight) != pending.BuyToken)
            {
                ReleaseBuy(pending.BuyToken);
                return;
            }

            try
            {
                DateTime actualSendUtc = DateTime.UtcNow;
                int dispatchDelayMs =
                    (int)(actualSendUtc - pending.SendUtc).TotalMilliseconds;
                if (dispatchDelayMs < 0) dispatchDelayMs = 0;

                TryBuy(
                    conn,
                    pending.Aid,
                    pending.Name,
                    pending.Type,
                    pending.CycleElapsedMs + dispatchDelayMs,
                    pending.RequestElapsedMs + dispatchDelayMs,
                    actualSendUtc,
                    pending.BuyToken);
            }
            catch (Exception ex)
            {
                ReleaseBuy(pending.BuyToken);
                LogSchedulerFailure("auction_buy", ex);
            }
        }

        private static void PromoteRpcRequestNext(
            global::LobbyConnection conn,
            global::LobbyConnection.RpcRequest request)
        {
            if (conn == null || request == null) return;

            global::LobbyConnection.RpcRequest current = conn.rpcRequest;
            if (current == null ||
                object.ReferenceEquals(current, request) ||
                object.ReferenceEquals(current.chlid, request))
            {
                return;
            }

            global::LobbyConnection.RpcRequest parent = current;
            for (int i = 0; i < 256 && parent != null; i++)
            {
                if (object.ReferenceEquals(parent.chlid, request))
                {
                    parent.chlid = request.chlid;
                    request.chlid = current.chlid;
                    current.chlid = request;
                    return;
                }
                parent = parent.chlid;
            }
        }

        private static void ReleasePendingBuy()
        {
            PendingBuy pending;
            lock (_pendingBuyGate)
            {
                pending = _pendingBuy;
                _pendingBuy = null;
            }
            if (pending != null) ReleaseBuy(pending.BuyToken);
        }

        private static void ClearPendingBuy()
        {
            lock (_pendingBuyGate)
            {
                _pendingBuy = null;
            }
        }

        private static void RefreshCachedTargets()
        {
            WatchItem[] snapshot = AuctionWatchList.GetSnapshot();
            if (object.ReferenceEquals(snapshot, _cachedWatchSnapshot))
                return;

            var targets = new List<WatchEntry>(snapshot.Length);
            float currencyWant = 0f;

            for (int i = 0; i < snapshot.Length; i++)
            {
                WatchItem item = snapshot[i];
                if (item == null ||
                    string.IsNullOrEmpty(item.Id) ||
                    item.Price <= 0f)
                {
                    continue;
                }

                if (string.Equals(
                    item.Name,
                    CURRENCY_NAME,
                    StringComparison.Ordinal))
                {
                    if (currencyWant <= 0f)
                        currencyWant = item.Price;
                    continue;
                }

                targets.Add(new WatchEntry
                {
                    Id = item.Id,
                    NameCN = item.Name,
                    Price = item.Price
                });
            }

            _cachedTargets = targets;
            _cachedCurrencyWant = currencyWant;
            _cachedWatchSnapshot = snapshot;
            _targetProbePhases.Clear();

            if (targets.Count <= 0)
            {
                _targetCursor = 0;
                _fullPhase = -1;
            }
            else if (_targetCursor >= targets.Count)
            {
                _targetCursor = 0;
            }
        }

        private static void RunSchedulerMaintenance(int now)
        {
            if (!IsTickDue(now, _nextMaintenanceTick)) return;
            _nextMaintenanceTick =
                unchecked(now + SchedulerMaintenanceIntervalMs);
            CleanupPurchased();
            HealthLogMaybe();
        }

        private static void GetProbeType(
            int phase,
            out int t,
            out int st)
        {
            switch (phase % 3)
            {
                case 0:
                    t = 2;
                    st = -1;
                    break;
                case 1:
                    t = 3;
                    st = -1;
                    break;
                default:
                    t = 3;
                    st = 400;
                    break;
            }
        }

        private static bool IsTickDue(int now, int due)
        {
            return unchecked(now - due) >= 0;
        }

        private static int NextNonZeroToken(ref int source)
        {
            int token = Interlocked.Increment(ref source);
            if (token == 0) token = Interlocked.Increment(ref source);
            return token;
        }

        private static int ReadInt(ref int value)
        {
            return Interlocked.CompareExchange(ref value, 0, 0);
        }

        private static int GetMonitorRequestsInFlight()
        {
            lock (_requestGate)
            {
                return _monitorRequestsInFlight;
            }
        }

        private static void LogSchedulerFailure(
            string operation,
            Exception ex)
        {
            int now = Environment.TickCount;
            int last = ReadInt(ref _lastSchedulerErrorTick);
            if (last != 0 &&
                unchecked((uint)(now - last)) <
                (uint)SchedulerErrorLogIntervalMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                ref _lastSchedulerErrorTick,
                now,
                last) != last)
            {
                return;
            }

            FileLogger.LogException(
                "AuctionMonitor " + operation,
                ex == null ? string.Empty : ex.ToString());
        }
    }
}
