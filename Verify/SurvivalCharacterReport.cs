using System;
using System.Globalization;
using System.Threading;
using ASWDEBUG.Logger;

namespace ASWDEBUG.Verify
{
    // 定制版(ReleaseA)在游戏内角色就绪后，把本地 character_id 上报到服务器，
    // 使普通版能拉取定制版名单并避让。
    // 卡密由 ConsoleManager 在引导时从一次性 handoff 转交（handoff 文件读取后即删除）。
    // 上报在后台线程执行且带退避重试，绝不阻塞 Unity 主线程。
    internal static class SurvivalCharacterReport
    {
        private static readonly object _gate = new object();
        private static string _directCard;
        private static bool _workerStarted;
        private static ulong _pendingCharacterId;

        internal static void SetDirectCard(string directCard)
        {
            lock (_gate)
            {
                _directCard = directCard;
            }
        }

        public static void TryReport(Character player)
        {
            ulong characterId = ReadCharacterId(player);
            if (characterId == 0UL) return;

            lock (_gate)
            {
                if (_workerStarted) return;
                if (string.IsNullOrEmpty(_directCard)) return;
                _pendingCharacterId = characterId;
                _workerStarted = true;
            }

            Thread worker = new Thread(ReportWorker)
            {
                IsBackground = true,
                Name = "SurvivalCharacterReport"
            };
            worker.Start();
        }

        // 后台线程：激活 + 建会话 + 上报共 3 次网络往返，失败按退避重试，最多 5 次。
        private static void ReportWorker()
        {
            string card;
            ulong characterId;
            lock (_gate)
            {
                card = _directCard;
                characterId = _pendingCharacterId;
            }
            if (string.IsNullOrEmpty(card) || characterId == 0UL) return;

            int[] retryDelaysMilliseconds = { 0, 15000, 60000, 300000, 900000 };
            for (int attempt = 0; attempt < retryDelaysMilliseconds.Length; attempt++)
            {
                if (retryDelaysMilliseconds[attempt] > 0)
                {
                    Thread.Sleep(retryDelaysMilliseconds[attempt]);
                }
                try
                {
                    VeriGateClient.ReportCharacterId(
                        card,
                        characterId.ToString(CultureInfo.InvariantCulture));
                    FileLogger.Log("SURVIVAL",
                        "reported character id=" + characterId +
                        " attempt=" + (attempt + 1));
                    return;
                }
                catch (Exception error)
                {
                    FileLogger.Log("SURVIVAL",
                        "character report failed attempt=" + (attempt + 1) +
                        ": " + error.GetType().Name + ": " + error.Message);
                }
            }
            FileLogger.Log("SURVIVAL",
                "character report gave up character_id=" + characterId);
        }

        private static ulong ReadCharacterId(Character player)
        {
            try
            {
                CharacterInfoData info = player.character_info;
                return info != null ? info.character_id : 0UL;
            }
            catch
            {
                return 0UL;
            }
        }
    }
}
