using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace ASWDEBUG.UI
{
    // 依赖：AuctionWatchList、GameApp.Instance.lobby_connection（AddTextRpc）
    public static class AuctionMonitor
    {
#if AUCTION_BUILD
        public static readonly bool FeatureEnabled = true;
#else
        public static readonly bool FeatureEnabled = false;
#endif

        // ===== 调参 =====
        private const bool PARALLEL_REQUESTS = true;     // 全量兜底是否并行
        private const int RequestTimeoutMs = 15000;
        private const int InterCycleDelayMs = 60;        // 一轮结束微停
        private const int PurchasedTtlSec = 4;
        private const int FullTypeGapMs = 70;            // 三类全量扫描之间的间隔（毫秒）

        // 全量兜底节流窗口（毫秒）：两次“全量兜底”之间的最小间隔，降低长跑累计压力
        private static volatile int FULL_BURST_INTERVAL_MS = 1;

        // 解析并发阈值（避免回调风暴造成后台排队膨胀）
        private const int MaxParseConcurrency = 2;

        // —— Top-K 与定向小查询 —— //
        private const int GROUP_TOP_K = 1;   // 每个 display 只检查前 K 条（价格最低的 K 条）
        private const int TARGET_TOP_N = 10; // 每轮优先照顾前 N 个监控目标（定向）
        private const int TARGET_PAGE_S = GROUP_TOP_K; // 小查询一次就要回最便宜的 K 条

        // 固定查询参数（共用）
        private const string ARG_locale = "1";
        private const string ARG_order = "-1";                    // 升序（你环境里 -1 为升序）
        private const string ARG_orderField = "SINGLE_FIXED_PRICE";  // 按单价排序
        private const string ARG_p = "1";

        // ★ 金币专用（固定参数）
        private const string CURRENCY_NAME = "金币";
        private const int CURRENCY_T_FOR_BUY = 7; // 购买时 t 固定为 7
        private const string ARG_currency = "1";
        private const string ARG_orderFieldCurrency = "SINGLE_PRICE";
        private const string ARG_currency_p = "1";
        private const string ARG_currency_s = "1"; // 固定只取第一条（最低价）

        // 内部状态
        public static volatile bool _running;
        private static Thread _worker;
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, DateTime> _purchasedAid = new Dictionary<string, DateTime>();
        private static DateTime _cycleStartUtc = DateTime.UtcNow; // 本轮起点
        private static DateTime _lastFullBurstUtc = DateTime.MinValue;

        // ★ 有“买在途”时暂停一切 auction_list 的发送与解析（优化1核心）
        private static int _buyInFlight = 0;

        // 观察到的类型提示：id -> (t, st)（加上限，避免长跑爆涨）
        private struct TypeHint { public int T; public int ST; }
        private static readonly Dictionary<string, TypeHint> _typeHints = new Dictionary<string, TypeHint>(256);
        private const int TypeHintsMax = 8192;

        // 解析并发闸门（比 SemaphoreSlim 更兼容旧框架）
        private static int _parseInFlight = 0;

        // 健康日志
        private static long _lastHealthLogTicks;
        private const int HealthLogIntervalMs = 60000;

        // ===== 单独监控（Single Mode）=====
        public static bool IsSingleMode { get; private set; }
        public static string SingleId { get; private set; }
        public static string SingleName { get; private set; }
        public static float SingleWant { get; private set; }
        public static int SingleT { get; private set; } = -1;
        public static int SingleST { get; private set; } = -1;

        public struct NamedId { public string Id; public string Name; } // 供读取类型用

        public static bool IsRunning { get { return FeatureEnabled && _running; } }

        // 运行时调节全量兜底间隔（毫秒）。传 0 表示不节流（每轮都跑）
        public static void SetFullBurstIntervalMs(int ms)
        {
            if (ms < 0) ms = 0;
            Interlocked.Exchange(ref FULL_BURST_INTERVAL_MS, ms);
            FileLogger.Log("AuctionMonitor", $"FULL_BURST_INTERVAL_MS={FULL_BURST_INTERVAL_MS}");
        }

        public static void Start()
        {
            if (!FeatureEnabled)
            {
                FileLogger.Log("AuctionMonitor", "START ignored: disabled");
                return;
            }

            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _lastFullBurstUtc = DateTime.MinValue;
                _parseInFlight = 0;
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "AuctionMonitorFast" };
                _worker.Start();
                FileLogger.Log("AuctionMonitor", "START");
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                IsSingleMode = false;
                SingleId = SingleName = null;
                SingleWant = 0f;
                SingleT = SingleST = -1;

                if (_worker != null)
                {
                    try { _worker.Interrupt(); } catch { }
                    _worker = null;
                }
                // 清空在途买标记
                Interlocked.Exchange(ref _buyInFlight, 0);
                _lastFullBurstUtc = DateTime.MinValue;

                // 适度清理缓存，避免下次启动带着旧大表
                lock (_purchasedAid) { _purchasedAid.Clear(); }
                lock (_typeHints) { _typeHints.Clear(); }

                FileLogger.Log("AuctionMonitor", "STOP");
            }
        }

        public static void Toggle()
        {
            if (!FeatureEnabled)
            {
                Stop();
                FileLogger.Log("AuctionMonitor", "TOGGLE ignored: disabled");
                return;
            }

            if (_running) Stop(); else Start();
        }

        // ===== 单独监控 API =====
        public static void StartSingleMonitor(string id, string name, float want, int t = -1, int st = -1)
        {
            if (!FeatureEnabled)
            {
                FileLogger.Log("AuctionMonitor", "SINGLE START ignored: disabled");
                return;
            }

            if (string.IsNullOrEmpty(id)) return;
            SingleId = id;
            SingleName = name ?? id;
            SingleWant = want;
            SingleT = t;
            SingleST = st;
            IsSingleMode = true;

            if (!_running) Start();
            FileLogger.Log("AuctionMonitor", $"SINGLE START id={SingleId} name={SingleName} want={SingleWant} t={SingleT} st={SingleST}");
        }

        // ★ 需求：取消单独监控后“全部监控也要取消”
        public static void StopSingleMonitor()
        {
            FileLogger.Log("AuctionMonitor", "SINGLE STOP -> STOP ALL");
            Stop(); // 直接整体停止（包括全量监控）
        }

        // 对外：喂类型 hint
        public static void SetTypeHint(string id, int t, int st)
        {
            if (string.IsNullOrEmpty(id)) return;
            AddOrUpdateTypeHint(id, t, st);
        }

        // 对外：取类型 hint
        public static bool TryGetTypeHint(string id, out int t, out int st)
        {
            t = -1; st = -1;
            if (string.IsNullOrEmpty(id)) return false;
            lock (_typeHints)
            {
                TypeHint h;
                if (_typeHints.TryGetValue(id, out h))
                {
                    t = h.T;
                    st = h.ST;
                    return true;
                }
            }
            return false;
        }

        private static void WorkerLoop()
        {
            try
            {
                while (FeatureEnabled && _running)
                {
                    _cycleStartUtc = DateTime.UtcNow;

                    // ★ 买在途：暂停一切 list 的发送
                    if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0)
                    {
                        SleepSafe(10);
                        continue;
                    }

                    // 单独监控模式：只跑该目标的小查询，且不做全量兜底
                    if (IsSingleMode)
                    {
                        LaunchSingleBurst();
                        CleanupPurchased();
                        HealthLogMaybe();
                        SleepSafe(InterCycleDelayMs);
                        continue;
                    }

                    // —— 普通模式 —— //
                    // 快照监控（价格>0）
                    List<WatchEntry> targets = SnapshotTargetsDetailed();
                    if (targets.Count == 0)
                    {
                        // 即便没有物品目标，也可能有“金币”目标
                        LaunchCurrencyBurstIfWatching(); // 若关注金币，仍会发一次货币查询
                        HealthLogMaybe();
                        SleepSafe(250);
                        continue;
                    }

                    // A) 定向小查询（itemName 过滤，s=3）
                    if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) == 0)
                        LaunchTargetedBurst(targets);

                    // ★ 金币（若在关注列表）—— 每轮都查一次
                    if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) == 0)
                        LaunchCurrencyBurstIfWatching();

                    // B) 全量并行（兜底三类）— 带“全量节流窗口”与“三类之间的间隔”
                    if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) == 0)
                    {
                        bool runFull = true;
                        int win = FULL_BURST_INTERVAL_MS;
                        if (win > 0 && _lastFullBurstUtc != DateTime.MinValue)
                        {
                            int since = (int)(DateTime.UtcNow - _lastFullBurstUtc).TotalMilliseconds;
                            if (since < win) runFull = false;
                        }

                        if (runFull)
                        {
                            _lastFullBurstUtc = DateTime.UtcNow;

                            if (PARALLEL_REQUESTS)
                            {
                                using (var e1 = new ManualResetEvent(false))
                                using (var e2 = new ManualResetEvent(false))
                                using (var e3 = new ManualResetEvent(false))
                                {
                                    SendAuctionListAsync_Full(2, -1, e1);
                                    SleepSafe(FullTypeGapMs);

                                    if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0)
                                    {
                                        try { e2.Set(); e3.Set(); } catch { }
                                        WaitHandle.WaitAll(new WaitHandle[] { e1, e2, e3 }, RequestTimeoutMs, false);
                                    }
                                    else
                                    {
                                        SendAuctionListAsync_Full(3, -1, e2);
                                        SleepSafe(FullTypeGapMs);

                                        if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0)
                                        {
                                            try { e3.Set(); } catch { }
                                            WaitHandle.WaitAll(new WaitHandle[] { e1, e2, e3 }, RequestTimeoutMs, false);
                                        }
                                        else
                                        {
                                            SendAuctionListAsync_Full(3, 400, e3);
                                            WaitHandle.WaitAll(new WaitHandle[] { e1, e2, e3 }, RequestTimeoutMs, false);
                                        }
                                    }
                                } // using：确保事件句柄释放
                            }
                            else
                            {
                                SendAuctionListSync_Full(2, -1);
                                if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) { CleanupPurchased(); HealthLogMaybe(); SleepSafe(InterCycleDelayMs); continue; }

                                SleepSafe(FullTypeGapMs);
                                SendAuctionListSync_Full(3, -1);
                                if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) { CleanupPurchased(); HealthLogMaybe(); SleepSafe(InterCycleDelayMs); continue; }

                                SleepSafe(FullTypeGapMs);
                                SendAuctionListSync_Full(3, 400);
                            }
                        }
                    }

                    CleanupPurchased();
                    HealthLogMaybe();
                    SleepSafe(InterCycleDelayMs);
                }
            }
            catch (ThreadInterruptedException) { /* 停止 */ }
            catch (Exception ex)
            {
                FileLogger.LogException("AuctionMonitor", ex.ToString());
            }
        }

        // ======== 单独监控：只对 SingleId/SingeName 发小查询 ========
        private static void LaunchSingleBurst()
        {
            var conn = (global::GameApp.Instance != null) ? global::GameApp.Instance.lobby_connection : null;
            if (conn == null) return;
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

            // ★ 单独监控“金币”
            if (string.Equals(SingleName, CURRENCY_NAME, StringComparison.Ordinal))
            {
                float want = SingleWant > 0 ? SingleWant : 0f;
                SendAuctionCurrencyListAsync(conn, want);
                return;
            }

            // —— 常规单独监控（物品） —— //
            if (SingleT == 3 && SingleST == 400)
            {
                SendAuctionListAsync_Targeted(conn, 3, 400, new WatchEntry { Id = SingleId, NameCN = SingleName, Price = SingleWant });
            }
            else if (SingleT == 2 || SingleT == 3)
            {
                SendAuctionListAsync_Targeted(conn, SingleT, (SingleT == 3 ? SingleST : -1), new WatchEntry { Id = SingleId, NameCN = SingleName, Price = SingleWant });
            }
            else
            {
                SendAuctionListAsync_Targeted(conn, 2, -1, new WatchEntry { Id = SingleId, NameCN = SingleName, Price = SingleWant });
                SendAuctionListAsync_Targeted(conn, 3, -1, new WatchEntry { Id = SingleId, NameCN = SingleName, Price = SingleWant });
                SendAuctionListAsync_Targeted(conn, 3, 400, new WatchEntry { Id = SingleId, NameCN = SingleName, Price = SingleWant });
            }
        }

        // ======== A) 定向小查询：针对前 N 个监控目标并行飞（s=3） ========
        private static void LaunchTargetedBurst(List<WatchEntry> targets)
        {
            var conn = (global::GameApp.Instance != null) ? global::GameApp.Instance.lobby_connection : null;
            if (conn == null) return;
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return; // 有买在途就不发

            int n = Math.Min(TARGET_TOP_N, targets.Count);
            for (int i = 0; i < n; i++)
            {
                if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) break;

                WatchEntry w = targets[i];
                // ★ 物品：按历史类型提示，尽量只发 1 次；没有提示就发三类
                TypeHint hint;
                if (_typeHints.TryGetValue(w.Id, out hint))
                {
                    if (hint.T == 3 && hint.ST == 400)
                        SendAuctionListAsync_Targeted(conn, 3, 400, w);
                    else
                        SendAuctionListAsync_Targeted(conn, hint.T, -1, w);
                }
                else
                {
                    SendAuctionListAsync_Targeted(conn, 2, -1, w);
                    SendAuctionListAsync_Targeted(conn, 3, -1, w);
                    SendAuctionListAsync_Targeted(conn, 3, 400, w);
                }
            }
        }

        // ======== ★ 金币：若在关注列表则发一条货币查询 ========
        private static void LaunchCurrencyBurstIfWatching()
        {
            float want;
            if (!TryGetCurrencyWantedPrice(out want) || want <= 0f) return;

            var conn = (global::GameApp.Instance != null) ? global::GameApp.Instance.lobby_connection : null;
            if (conn == null) return;
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

            SendAuctionCurrencyListAsync(conn, want);
        }

        private static void SendAuctionListAsync_Targeted(global::LobbyConnection conn, int t, int st, WatchEntry w)
        {
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;
            var args = BuildArgs(t, st, w.NameCN, TARGET_PAGE_S); // s=3
            DateTime reqStart = DateTime.UtcNow;

            try
            {
                conn.AddTextRpc(
                    "auction_list",
                    new global::LobbyConnection.RpcCallback(delegate (string data)
                    {
                        try
                        {
                            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) == 0)
                            {
                                if (TryEnterParseGate())
                                {
                                    ThreadPool.QueueUserWorkItem(_ =>
                                    {
                                        try { FastScanAndBuy(data, reqStart, true); }
                                        catch (Exception ex) { FileLogger.LogException("AuctionMonitor", ex.ToString()); }
                                        finally { ExitParseGate(); }
                                    });
                                }
                                // 如果没抢到解析闸门，直接丢弃这帧回包，避免排队膨胀
                            }
                        }
                        catch (Exception ex) { FileLogger.LogException("AuctionMonitor", ex.ToString()); }
                    }),
                    args
                );
            }
            catch (Exception ex)
            {
                FileLogger.LogException("AuctionMonitor", ex.ToString());
            }
        }

        // ======== B) 全量并行 / 顺序（s=9999，但解析时仅看每组前三条） ========
        private static void SendAuctionListAsync_Full(int t, int st, ManualResetEvent done)
        {
            var conn = (global::GameApp.Instance != null) ? global::GameApp.Instance.lobby_connection : null;
            if (conn == null) { try { done.Set(); } catch { } return; }
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) { try { done.Set(); } catch { } return; }

            var args = BuildArgs(t, st, "", 9999);
            DateTime reqStart = DateTime.UtcNow;

            try
            {
                conn.AddTextRpc(
                    "auction_list",
                    new global::LobbyConnection.RpcCallback(delegate (string data)
                    {
                        try
                        {
                            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) == 0)
                            {
                                if (TryEnterParseGate())
                                {
                                    ThreadPool.QueueUserWorkItem(_ =>
                                    {
                                        try { FastScanAndBuy(data, reqStart, true); }
                                        catch (Exception ex) { FileLogger.LogException("AuctionMonitor", ex.ToString()); }
                                        finally { ExitParseGate(); }
                                    });
                                }
                                // 抢不到闸门就跳过，防止长时间内堆积
                            }
                        }
                        catch (Exception ex) { FileLogger.LogException("AuctionMonitor", ex.ToString()); }
                        finally { try { done.Set(); } catch { } }
                    }),
                    args
                );
            }
            catch (Exception ex)
            {
                FileLogger.LogException("AuctionMonitor", ex.ToString());
                try { done.Set(); } catch { }
            }
        }

        private static void SendAuctionListSync_Full(int t, int st)
        {
            var conn = (global::GameApp.Instance != null) ? global::GameApp.Instance.lobby_connection : null;
            if (conn == null) return;
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

            var args = BuildArgs(t, st, "", 9999);
            DateTime reqStart = DateTime.UtcNow;

            string result = null;
            using (var evt = new AutoResetEvent(false))
            {
                try
                {
                    conn.AddTextRpc(
                        "auction_list",
                        new global::LobbyConnection.RpcCallback(delegate (string data)
                        {
                            result = data;
                            try { evt.Set(); } catch { }
                        }),
                        args
                    );
                }
                catch (Exception ex)
                {
                    FileLogger.LogException("AuctionMonitor", ex.ToString());
                    return;
                }

                if (evt.WaitOne(RequestTimeoutMs) && !string.IsNullOrEmpty(result))
                {
                    if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) == 0)
                    {
                        // 同步分支只有 1 份结果，不会风暴，这里直接解析
                        FastScanAndBuy(result, reqStart, true);
                    }
                }
            } // using 释放事件
        }

        private static Dictionary<string, string> BuildArgs(int t, int st, string itemName, int s)
        {
            var d = new Dictionary<string, string>(12);
            d["itemName"] = itemName ?? "";
            d["locale"] = ARG_locale;
            d["order"] = ARG_order;
            d["orderField"] = ARG_orderField; // SINGLE_FIXED_PRICE
            d["p"] = ARG_p;
            d["s"] = s.ToString(CultureInfo.InvariantCulture);
            d["t"] = t.ToString(CultureInfo.InvariantCulture);
            if (st >= 0) d["st"] = st.ToString(CultureInfo.InvariantCulture);
            return d;
        }

        // ========= 极速扫描解析 & 命中即买（每组 Top-K） =========
        private static void FastScanAndBuy(string data, DateTime reqStart, bool limitByGroupTopK)
        {
            if (string.IsNullOrEmpty(data)) return;
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

            // 找到 items 块
            int idxItems = data.IndexOf("items", StringComparison.Ordinal);
            if (idxItems < 0) return;
            int idxEq = data.IndexOf('=', idxItems);
            if (idxEq < 0) return;
            int idxOpen = data.IndexOf('{', idxEq);
            if (idxOpen < 0) return;

            Dictionary<string, int> groupSeen = limitByGroupTopK ? new Dictionary<string, int>(32) : null;

            int i = idxOpen + 1;
            int depth = 1;

            while (_running && i < data.Length && depth > 0)
            {
                if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

                int nextOpen = data.IndexOf('{', i);
                int nextClose = data.IndexOf('}', i);
                if (nextClose < 0) break;

                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    int itemStart = nextOpen;
                    int itemEnd = FindMatchingBrace(data, itemStart);
                    if (itemEnd < 0) break;

                    string id = ExtractQuotedValueAfterKey(data, "display", itemStart, itemEnd);
                    if (!string.IsNullOrEmpty(id))
                    {
                        int tVal, stVal;
                        if (TryParseInt(ExtractSimpleValueAfterKey(data, "type", itemStart, itemEnd), out tVal))
                        {
                            if (!TryParseInt(ExtractSimpleValueAfterKey(data, "subType", itemStart, itemEnd), out stVal)) stVal = -1;
                            AddOrUpdateTypeHint(id, tVal, stVal);
                        }

                        if (groupSeen != null)
                        {
                            int c;
                            if (!groupSeen.TryGetValue(id, out c)) c = 0;
                            if (c >= GROUP_TOP_K)
                            {
                                i = itemEnd + 1;
                                continue;
                            }
                            groupSeen[id] = c + 1;
                        }

                        float want;
                        if (TryGetWantedPrice(id, out want) && want > 0f)
                        {
                            // 单独监控时，用单独目标的 want 覆盖
                            if (IsSingleMode && id == SingleId && SingleWant > 0f) want = SingleWant;

                            float unitPrice = ParseUnitPrice(data, itemStart, itemEnd);
                            if (unitPrice >= 0f && unitPrice <= want)
                            {
                                string aid = ExtractQuotedValueAfterKey(data, "aid", itemStart, itemEnd);
                                string auctioneerName = ExtractQuotedValueAfterKey(data, "auctioneerName", itemStart, itemEnd);
                                string type = ExtractSimpleValueAfterKey(data, "type", itemStart, itemEnd);
                                if (!string.IsNullOrEmpty(aid) && !string.IsNullOrEmpty(type) && MarkPurchasedOnce(aid))
                                {
                                    if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

                                    // 占位：标记“买在途”
                                    Interlocked.Increment(ref _buyInFlight);

                                    DateTime sendUtc = DateTime.UtcNow;
                                    int cycleElapsedMs = (int)(sendUtc - _cycleStartUtc).TotalMilliseconds;
                                    int reqElapsedMs = (int)(sendUtc - reqStart).TotalMilliseconds;

                                    try
                                    {
                                        TryBuy(aid, auctioneerName, type, cycleElapsedMs, reqElapsedMs, sendUtc);
                                    }
                                    catch (Exception ex)
                                    {
                                        Interlocked.Decrement(ref _buyInFlight);
                                        FileLogger.LogException("AuctionMonitor", ex.ToString());
                                    }

                                    // 命中一笔后立即停止处理该回包
                                    return;
                                }
                            }
                        }
                    }

                    i = itemEnd + 1;
                }
                else
                {
                    depth--;
                    i = nextClose + 1;
                }
            }
        }

        private static bool TryGetWantedPrice(string id, out float want)
        {
            // 单独监控：优先使用单独 want
            if (IsSingleMode && id == SingleId && SingleWant > 0f)
            {
                want = SingleWant;
                return true;
            }

            want = 0f;
            IList<WatchItem> list = AuctionWatchList.All;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w != null && w.Id == id && w.Price > 0f) { want = w.Price; return true; }
            }
            return false;
        }

        private static int FindMatchingBrace(string s, int startIndex)
        {
            int depth = 0;
            for (int k = startIndex; k < s.Length; k++)
            {
                char c = s[k];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return k;
                }
            }
            return -1;
        }

        private static string ExtractQuotedValueAfterKey(string s, string key, int start, int end)
        {
            int k = s.IndexOf(key, start, end - start, StringComparison.Ordinal);
            if (k < 0) return null;
            k = s.IndexOf('=', k);
            if (k < 0 || k >= end) return null;

            int q1 = s.IndexOf('"', k);
            if (q1 < 0 || q1 >= end) return null;
            int q2 = s.IndexOf('"', q1 + 1);
            if (q2 < 0 || q2 > end) return null;

            string v = s.Substring(q1 + 1, q2 - q1 - 1);
            int brace = v.IndexOf('{'); if (brace >= 0) v = v.Substring(0, brace);
            int comma = v.IndexOf(','); if (comma >= 0) v = v.Substring(0, comma);
            return v.Trim();
        }

        private static string ExtractSimpleValueAfterKey(string s, string key, int start, int end)
        {
            int k = s.IndexOf(key, start, end - start, StringComparison.Ordinal);
            if (k < 0) return null;
            k = s.IndexOf('=', k);
            if (k < 0 || k >= end) return null;

            k++;
            while (k < end && (s[k] == ' ' || s[k] == '\t')) k++;

            int j = k;
            while (j < end)
            {
                char ch = s[j];
                if (ch == ',' || ch == '\n' || ch == '\r') break;
                j++;
            }
            return s.Substring(k, j - k).Trim();
        }

        private static bool TryParseInt(string v, out int x)
        {
            x = 0;
            if (string.IsNullOrEmpty(v) || v == "nil") return false;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out x);
        }

        private static float ParseUnitPrice(string s, int start, int end)
        {
            float single;
            if (TryParseFloat(ExtractSimpleValueAfterKey(s, "singleFixedPrice", start, end), out single))
                return single;

            float fixedP;
            if (TryParseFloat(ExtractSimpleValueAfterKey(s, "fixedPrice", start, end), out fixedP))
            {
                int qty;
                if (int.TryParse(ExtractSimpleValueAfterKey(s, "quantity", start, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out qty) && qty > 0)
                    return fixedP / qty;
                return fixedP;
            }
            return -1f;
        }

        private static bool TryParseFloat(string v, out float f)
        {
            f = 0f;
            if (string.IsNullOrEmpty(v) || v == "nil") return false;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
        }

        // === 下单 + 统计 & 日志（物品/金币复用） ===
        private static void TryBuy(string aid, string name, string t, int cycleElapsedMs, int reqElapsedMs, DateTime sendUtc)
        {
            var conn = (global::GameApp.Instance != null) ? global::GameApp.Instance.lobby_connection : null;
            if (conn == null) { Interlocked.Decrement(ref _buyInFlight); return; }

            var args = new Dictionary<string, string>(4);
            args["aid"] = aid;
            args["t"] = t;

            try
            {
                conn.AddTextRpc(
                    "auction_buy",
                    new global::LobbyConnection.RpcCallback(delegate (string data)
                    {
                        try
                        {
                            DateTime recvUtc = DateTime.UtcNow;
                            int rttMs = (int)(recvUtc - sendUtc).TotalMilliseconds;
                            int resultElapsedMs = (int)(recvUtc - _cycleStartUtc).TotalMilliseconds;

                            FileLogger.Log(
                                "AuctionMonitor",
                                "BUY aid=" + aid +
                                " name=" + name +
                                " t=" + t +
                                " cycle_elapsed_ms=" + cycleElapsedMs +
                                " req_elapsed_ms=" + reqElapsedMs +
                                " rtt_ms=" + rttMs +
                                " result_elapsed_ms=" + resultElapsedMs +
                                " data=" + (data ?? "null")
                            );
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _buyInFlight);
                        }
                    }),
                    args
                );
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _buyInFlight);
                FileLogger.LogException("AuctionMonitor", ex.ToString());
            }
        }

        // ===== 快照 =====
        private struct WatchEntry { public string Id; public string NameCN; public float Price; }

        private static List<WatchEntry> SnapshotTargetsDetailed()
        {
            var list = new List<WatchEntry>(64);
            IList<WatchItem> src = AuctionWatchList.All;
            for (int i = 0; i < src.Count; i++)
            {
                var w = src[i];
                if (w == null || string.IsNullOrEmpty(w.Id) || w.Price <= 0f) continue;
                // ★ “金币”不纳入物品小查询/全量清单里，由货币链路处理
                if (string.Equals(w.Name, CURRENCY_NAME, StringComparison.Ordinal)) continue;
                list.Add(new WatchEntry { Id = w.Id, NameCN = w.Name, Price = w.Price });
            }
            return list;
        }

        // ★ 获取金币价格阈值（来自监控列表，按名字匹配“金币”）
        private static bool TryGetCurrencyWantedPrice(out float want)
        {
            want = 0f;
            // 单独监控“金币”时优先用 SingleWant
            if (IsSingleMode && string.Equals(SingleName, CURRENCY_NAME, StringComparison.Ordinal) && SingleWant > 0f)
            {
                want = SingleWant;
                return true;
            }

            IList<WatchItem> list = AuctionWatchList.All;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w != null &&
                    string.Equals(w.Name, CURRENCY_NAME, StringComparison.Ordinal) &&
                    w.Price > 0f)
                {
                    want = w.Price;
                    return true;
                }
            }
            return false;
        }

        // ★ 发送货币列表请求（固定参数）
        private static void SendAuctionCurrencyListAsync(global::LobbyConnection conn, float want)
        {
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

            var args = new Dictionary<string, string>(8);
            args["currency"] = ARG_currency;
            args["order"] = ARG_order; // -1 升序
            args["orderField"] = ARG_orderFieldCurrency; // SINGLE_PRICE
            args["p"] = ARG_currency_p;
            args["s"] = ARG_currency_s;

            DateTime reqStart = DateTime.UtcNow;

            try
            {
                conn.AddTextRpc(
                    "auction_currency_list",
                    new global::LobbyConnection.RpcCallback(delegate (string data)
                    {
                        try
                        {
                            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) == 0)
                            {
                                if (TryEnterParseGate())
                                {
                                    ThreadPool.QueueUserWorkItem(_ =>
                                    {
                                        try { FastScanAndBuyCurrency(data, reqStart, want); }
                                        catch (Exception ex) { FileLogger.LogException("AuctionMonitor", ex.ToString()); }
                                        finally { ExitParseGate(); }
                                    });
                                }
                            }
                        }
                        catch (Exception ex) { FileLogger.LogException("AuctionMonitor", ex.ToString()); }
                    }),
                    args
                );
            }
            catch (Exception ex)
            {
                FileLogger.LogException("AuctionMonitor", ex.ToString());
            }
        }

        // ★ 解析货币列表并尝试购买：条件 singlePrice * 10000 <= want
        private static void FastScanAndBuyCurrency(string data, DateTime reqStart, float want)
        {
            if (string.IsNullOrEmpty(data)) return;
            if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

            // items = { ... } 结构
            int idxItems = data.IndexOf("items", StringComparison.Ordinal);
            if (idxItems < 0) return;
            int idxEq = data.IndexOf('=', idxItems);
            if (idxEq < 0) return;
            int idxOpen = data.IndexOf('{', idxEq);
            if (idxOpen < 0) return;

            int i = idxOpen + 1;
            int depth = 1;

            while (_running && i < data.Length && depth > 0)
            {
                if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

                int nextOpen = data.IndexOf('{', i);
                int nextClose = data.IndexOf('}', i);
                if (nextClose < 0) break;

                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    int itemStart = nextOpen;
                    int itemEnd = FindMatchingBrace(data, itemStart);
                    if (itemEnd < 0) break;

                    // 解析单条
                    float singlePrice;
                    if (TryParseFloat(ExtractSimpleValueAfterKey(data, "singlePrice", itemStart, itemEnd), out singlePrice))
                    {
                        // 条件：singlePrice * 10000 <= want
                        float scaled = singlePrice * 10000f;
                        if (scaled <= want && want > 0f)
                        {
                            string aid = ExtractQuotedValueAfterKey(data, "aid", itemStart, itemEnd);
                            string auctioneerName = ExtractQuotedValueAfterKey(data, "auctioneerName", itemStart, itemEnd);
                            if (!string.IsNullOrEmpty(aid) && MarkPurchasedOnce(aid))
                            {
                                if (Interlocked.CompareExchange(ref _buyInFlight, 0, 0) > 0) return;

                                // 占位：标记“买在途”
                                Interlocked.Increment(ref _buyInFlight);

                                DateTime sendUtc = DateTime.UtcNow;
                                int cycleElapsedMs = (int)(sendUtc - _cycleStartUtc).TotalMilliseconds;
                                int reqElapsedMs = (int)(sendUtc - reqStart).TotalMilliseconds;

                                try
                                {
                                    // ★ t 固定为 7
                                    TryBuy(aid, CURRENCY_NAME, CURRENCY_T_FOR_BUY.ToString(CultureInfo.InvariantCulture), cycleElapsedMs, reqElapsedMs, sendUtc);
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Decrement(ref _buyInFlight);
                                    FileLogger.LogException("AuctionMonitor", ex.ToString());
                                }

                                return; // 命中一笔后即可结束
                            }
                        }
                    }

                    i = itemEnd + 1;
                }
                else
                {
                    depth--;
                    i = nextClose + 1;
                }
            }
        }

        private static void TryBuyCurrency_NoUse() { /* 占位：统一走 TryBuy(aid, "金币", "7", ...) */ }

        private static void CleanupPurchased()
        {
            DateTime now = DateTime.UtcNow;
            lock (_purchasedAid)
            {
                List<string> rm = null;
                foreach (KeyValuePair<string, DateTime> kv in _purchasedAid)
                {
                    if (kv.Value < now)
                    {
                        if (rm == null) rm = new List<string>();
                        rm.Add(kv.Key);
                    }
                }
                if (rm != null)
                {
                    for (int i = 0; i < rm.Count; i++) _purchasedAid.Remove(rm[i]);
                }
            }
        }

        // 同一 aid TTL 内只尝试一次
        private static bool MarkPurchasedOnce(string aid)
        {
            if (string.IsNullOrEmpty(aid)) return false;
            DateTime now = DateTime.UtcNow;
            lock (_purchasedAid)
            {
                DateTime exp;
                if (_purchasedAid.TryGetValue(aid, out exp) && now <= exp) return false;
                _purchasedAid[aid] = now.AddSeconds(PurchasedTtlSec);
                return true;
            }
        }

        private static void SleepSafe(int ms)
        {
            try { if (ms > 0) Thread.Sleep(ms); } catch { }
        }

        // ====== 读取类型：逐个小查询解析 t/st（供 UI 按钮调用）======
        public static void ResolveTypeHintsAsync(
            List<NamedId> items,
            int perItemTimeoutMs,
            Action<string, int, int> onOne,
            Action onDone)
        {
            if (items == null || items.Count == 0) { onDone?.Invoke(); return; }

            new Thread(() =>
            {
                try
                {
                    var conn = (global::GameApp.Instance != null) ? global::GameApp.Instance.lobby_connection : null;
                    if (conn == null) { onDone?.Invoke(); return; }

                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        // ★ 安全：外部已跳过金币，这里再防一次
                        if (string.Equals(it.Name, CURRENCY_NAME, StringComparison.Ordinal))
                        {
                            onOne?.Invoke(it.Id, -1, -1);
                            Thread.Sleep(8);
                            continue;
                        }

                        int t, st;
                        if (TryResolveTypeForOne(conn, it.Name, perItemTimeoutMs, out t, out st))
                        {
                            AddOrUpdateTypeHint(it.Id, t, st);
                            onOne?.Invoke(it.Id, t, st);
                        }
                        else
                        {
                            // 失败用 -1/-1
                            onOne?.Invoke(it.Id, -1, -1);
                        }
                        // 避免把服务器打挂
                        Thread.Sleep(8);
                    }
                }
                catch (Exception e)
                {
                    FileLogger.LogException("AuctionMonitor", "ResolveTypeHintsAsync ex: " + e);
                }
                finally
                {
                    onDone?.Invoke();
                }
            })
            { IsBackground = true, Name = "ResolveTypeHints" }.Start();
        }

        private static bool TryResolveTypeForOne(global::LobbyConnection conn, string itemName, int timeoutMs, out int t, out int st)
        {
            t = -1; st = -1;
            // 依次试：t=2；t=3；t=3&st=400，每次 s=1 取第一条
            if (QueryOnceAndParseType(conn, 2, -1, itemName, timeoutMs, out t, out st)) return true;
            if (QueryOnceAndParseType(conn, 3, -1, itemName, timeoutMs, out t, out st)) return true;
            if (QueryOnceAndParseType(conn, 3, 400, itemName, timeoutMs, out t, out st)) return true;
            return false;
        }

        private static bool QueryOnceAndParseType(global::LobbyConnection conn, int qt, int qst, string itemName, int timeoutMs, out int t, out int st)
        {
            t = -1; st = -1;
            var args = BuildArgs(qt, qst, itemName ?? "", 1);
            string result = null;

            using (var evt = new AutoResetEvent(false))
            {
                try
                {
                    conn.AddTextRpc(
                        "auction_list",
                        new global::LobbyConnection.RpcCallback(delegate (string data)
                        {
                            result = data;
                            try { evt.Set(); } catch { }
                        }),
                        args
                    );
                }
                catch (Exception ex)
                {
                    FileLogger.LogException("AuctionMonitor", "QueryOnce ex: " + ex);
                    return false;
                }

                if (!evt.WaitOne(Math.Max(300, timeoutMs))) return false;
                if (string.IsNullOrEmpty(result)) return false;
            }

            // 解析第一条 item 的 type/subType
            int idxItems = result.IndexOf("items", StringComparison.Ordinal);
            if (idxItems < 0) return false;
            int idxEq = result.IndexOf('=', idxItems);
            if (idxEq < 0) return false;
            int idxOpen = result.IndexOf('{', idxEq);
            if (idxOpen < 0) return false;

            int itemStart = result.IndexOf('{', idxOpen + 1);
            if (itemStart < 0) return false;
            int itemEnd = FindMatchingBrace(result, itemStart);
            if (itemEnd < 0) return false;

            int tt, sst;
            if (TryParseInt(ExtractSimpleValueAfterKey(result, "type", itemStart, itemEnd), out tt))
            {
                if (!TryParseInt(ExtractSimpleValueAfterKey(result, "subType", itemStart, itemEnd), out sst)) sst = -1;
                t = tt; st = sst;
                return true;
            }
            return false;
        }

        // ===== 附加：工具方法们 =====
        private static void AddOrUpdateTypeHint(string id, int t, int st)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_typeHints)
            {
                if (_typeHints.Count >= TypeHintsMax && !_typeHints.ContainsKey(id))
                {
                    // 简单裁剪一些旧键（不追求严格 LRU，防爆涨即可）
                    int drop = _typeHints.Count - TypeHintsMax + 1024;
                    foreach (var k in new List<string>(_typeHints.Keys))
                    {
                        _typeHints.Remove(k);
                        if (--drop <= 0) break;
                    }
                }
                _typeHints[id] = new TypeHint { T = t, ST = st };
            }
        }

        private static bool TryEnterParseGate()
        {
            while (true)
            {
                int cur = _parseInFlight;
                if (cur >= MaxParseConcurrency) return false;
                if (Interlocked.CompareExchange(ref _parseInFlight, cur + 1, cur) == cur) return true;
            }
        }

        private static void ExitParseGate()
        {
            Interlocked.Decrement(ref _parseInFlight);
        }

        private static void HealthLogMaybe()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if ((nowTicks - _lastHealthLogTicks) / TimeSpan.TicksPerMillisecond >= HealthLogIntervalMs)
            {
                _lastHealthLogTicks = nowTicks;
                try
                {
                    var p = Process.GetCurrentProcess();
                    FileLogger.Log("AuctionMonitor.Health",
                        $"handles={p.HandleCount} threads={p.Threads.Count} " +
                        $"wsMB={p.WorkingSet64 / 1048576} privMB={p.PrivateMemorySize64 / 1048576} " +
                        $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} " +
                        $"parseInFlight={_parseInFlight}");
                }
                catch { }
            }
        }
    }
}
