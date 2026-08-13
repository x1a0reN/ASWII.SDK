using System;
using System.Globalization;
using ASWDEBUG.Logger;

namespace ASWDEBUG.Verify
{
    // 定制版(ReleaseA)在游戏内角色就绪后，把本地 character_id 上报到服务器，
    // 使普通版能拉取定制版名单并避让。每个进程只上报一次。
    internal static class SurvivalCharacterReport
    {
        private static bool _reported;

        public static void TryReport(Character player)
        {
            if (_reported || player == null) return;
            try
            {
                ulong characterId = ReadCharacterId(player);
                if (characterId == 0UL) return;

                SurvivalHandoff.Result handoff = SurvivalHandoff.TryLoad();
                if (handoff == null || string.IsNullOrEmpty(handoff.DirectCard)) return;

                VeriGateClient.ReportCharacterId(
                    handoff.DirectCard,
                    characterId.ToString(CultureInfo.InvariantCulture));
                _reported = true;
                FileLogger.Log("SURVIVAL", "reported character id=" + characterId);
            }
            catch (Exception error)
            {
                FileLogger.Log("SURVIVAL",
                    "character report failed: " + error.GetType().Name + ": " + error.Message);
            }
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
