// ASWDEBUG/GamePatches.cs
using System;
using System.Reflection;
using Harmony;
using ASWDEBUG.Logger;
using PluginTool;
using UnityEngine;
using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Cheats.AutoAim;
using Pathfinding.Util;
using static InvBaseItem;
using ASWDEBUG.Global;
using OFloat = CodeStage.AntiCheat.ObscuredTypes.ObscuredFloat;
using OInt = CodeStage.AntiCheat.ObscuredTypes.ObscuredInt;
using System.Collections.Generic;
using System.Reflection.Emit;
using ASWDEBUG.Cheats.AimTrack;
using PDE.Animation;
using System.Runtime.Remoting.Messaging;
using ASWDEBUG.Cheats.Other;
using System.Linq;
using ASWDEBUG.UI;
using ASWDEBUG.Verify;
using System.Collections.Specialized;
using UniLua;
using ASWDEBUG.Main;
using System.Collections;
using CodeStage.AntiCheat.ObscuredTypes;

// 关键：为避免 UniLua.OpCode/OpCodes 冲突，下面用别名显式指定
using CI = Harmony.CodeInstruction;
using SOpCode = System.Reflection.Emit.OpCode;
using SOpCodes = System.Reflection.Emit.OpCodes;
using static UnityEngine.RectTransform;
using static BossRoomSearchControl;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.CompilerServices;

namespace ASWDEBUG
{
    static class Win32
    {
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer,
            int dwSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess(); // 读本进程可用伪句柄
    }

    [HarmonyPatch]
    [Obfuscation(
    Exclude = true,                 // 排除本类型
    ApplyToMembers = true,          // 并排除所有成员
    Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
)]
    public static class Patch_MessageBox_show_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("MessageBox");
                if (t == null) { FileLogger.Log("PATCH", "Type MessageBox not found"); return null; }

                var m = AccessTools.Method(t, "show");
                if (m == null) FileLogger.Log("PATCH", "Method show not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(show) error: " + e);
                return null;
            }
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance)
        {
            try
            {
                if (HookMsgbox.Enabled)
                {
                    return false;
                }
            }
            catch (Exception e) { FileLogger.Log("GamePatches", "[MessageBox.show] prefix error: " + e); }
            return true;
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Character_UpdateSyncData
    {
        // ===== Target resolver =====
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("Character");
                if (t == null) { FileLogger.Log("PATCH", "Type Character not found"); return null; }

                var m = AccessTools.Method(t, "UpdateSyncData", new Type[] { typeof(float) });
                if (m == null) FileLogger.Log("PATCH", "Method Character.UpdateSyncData(float) not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(UpdateSyncData) error: " + e);
                return null;
            }
        }

        // ===== Prefix（可选整体拦截；默认放行）=====
        // 返回 false 则整个 UpdateSyncData 被跳过；返回 true 则继续执行（并会走到 Transpiler 后的 IL）
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance, float frame_time)
        {
            try
            {
                if (Aike.Enabled)
                {

                    var ch = __instance as Character;

                    foreach (Character character in CharacterManager.Instance.character_set)
                    {
                        if (ASSingleton<Level>.Instance.GetPlayer().GetTeam() != character.GetTeam() && character.invincible_time == 0f && !character.IsDied && ASSingleton<Level>.Instance.GetPlayer() != null)
                        {
                            Vector3 position = character.transform.position;
                            ASSingleton<Level>.Instance.GetPlayer().transform.position = position + new Vector3(0, 0, 0.5f);
                        }
                        if (character.IsDied)
                        {
                            Settings.AutoKinfeAttack = false;
                        }
                    }
                    try { }
                    catch (Exception e) { FileLogger.Log("GamePatches", "[Character.UpdateSyncData] prefix error: " + e); }

                    return true;
                }
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "UpdateSyncData Prefix error: " + e);
            }
            return true; // 默认放行
        }

        // ===== Transpiler（替换 Transform.set_position 为我们的守卫方法）=====
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);

            // Harmony 1.x：用 set_position
            MethodInfo setPos = AccessTools.Method(typeof(Transform), "set_position", new Type[] { typeof(Vector3) });
            if (setPos == null)
            {
                setPos = typeof(Transform).GetMethod("set_position",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Vector3) }, null);
            }

            MethodInfo repl = typeof(NetSyncGuards).GetMethod("MaybeSetTransformPosition",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            int replaced = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var ci = list[i];
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call)
                    && ci.operand is MethodInfo mi && setPos != null && mi == setPos)
                {
                    ci.opcode = OpCodes.Call;   // 静态方法
                    ci.operand = repl;
                    replaced++;
                }
            }

            FileLogger.Log("PATCH", "UpdateSyncData: replaced set_position x" + replaced);
            return list;
        }

        // —— Assembly helper —— //
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [Obfuscation(
    Exclude = true,                 // 排除本类型
    ApplyToMembers = true,          // 并排除所有成员
    Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
)]
    public static class NetSyncGuards
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        public static void MaybeSetTransformPosition(Transform tr, Vector3 pos)
        {
            try
            {
                tr.position = pos;
                //if (!Aike.Enabled)
                //    tr.position = pos;
                // Aike.Enabled == true 时，跳过赋值，从而“冻结”位置但保留其它同步逻辑
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "MaybeSetTransformPosition error: " + e);
            }
        }
    }


    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Character_Shoot_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("Character");
                if (t == null) { FileLogger.Log("PATCH", "Type Character not found"); return null; }

                var m = AccessTools.Method(t, "update");
                if (m == null) FileLogger.Log("PATCH", "Method update not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(update) error: " + e);
                return null;
            }
        }
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance)
        {
            AutoLockHP.Update();
            try { }
            catch (Exception e) { FileLogger.Log("GamePatches", "[Character.update] prefix error: " + e); }
            return true;
        }
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }


    // Prefix: Character.UpdateBloodBar()
    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Character_UpdateBloodBar_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        // 通过反射定位 Assembly-CSharp.Character.UpdateBloodBar
        static MethodBase TargetMethod()
        {
            try
            {
                Assembly asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                Type t = asm.GetType("Character");

                if (t == null) { FileLogger.Log("PATCH", "Type Character not found"); return null; }

                MethodInfo m = AccessTools.Method(t, "UpdateBloodBar");
                if (m == null) FileLogger.Log("PATCH", "Method UpdateBloodBar not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(UpdateBloodBar) error: " + e);
                return null;
            }
        }
        [Obfuscation(Exclude = true, Feature = "-rename")]
        // 当 Plugin.ForceHPAlwaysOn == false 时，返回 true 放行原方法；否则强制绘制并拦截（return false）
        static bool Prefix(object __instance)
        {
            try
            {
                // 关掉开关 => 遵循默认规则
                if (!HealthBarDisplay.Enabled) return true;

                // 强制显示逻辑（方法B的“复刻绘制”，Alpha 固定为 1）
                global::Character ch = __instance as global::Character;
                if (ch == null) return true; // 类型不符，放行原函数

                // 与原逻辑一致的早退：玩家自己/已死亡/特殊地图（id=16）不绘制
                if (ch.IsPlayer) return false;
                if (ch.IsDied) return false;
                var levelSingleton = Level.Instance;
                if (levelSingleton != null && levelSingleton.map_id == 16UL) return false;

                // 取私有字段
                Traverse tv = Traverse.Create(ch);

                int uid = ch.uid;

                // 名称缓存（与原逻辑一致）
                if (uid != 0 && ch.baseName == null)
                {
                    ch.baseName = "BaseBody" + uid;
                    ch.hpName = "BaseBodyHP" + uid;
                    ch.hpBGName = "BaseBodyHPBG" + uid;
                    ch.hpLabelName = "BaseBodyHPLabel" + uid;

                    ch.gameObject.name = ch.baseName;
                }

                // 生命/护盾百分比

                if (ch.character_info.max_health <= 0) return false;

                float hpPct = Mathf.Clamp01((float)ch.hp / (float)ch.character_info.max_health);
                if (hpPct <= 0f) return false; // 没血不画（保持和原逻辑一致）

                float shieldPct = (ch.max_shield > 0) ? Mathf.Clamp01((float)ch.shield / (float)ch.max_shield) : 0f;

                // 颜色（尽量复刻原逻辑，但不再隐藏）
                global::Character player = (levelSingleton != null) ? levelSingleton.GetPlayer() : null;
                bool sameTeam = true;
                if (player != null)
                {
                    if (player.Is_Viewer && player.Is_GP)
                    {
                        if (global::InGameUIManager.getInstane().Player != null)
                            sameTeam = (ch.GetTeam() == global::InGameUIManager.getInstane().Player.GetTeam());
                    }
                    else
                    {
                        sameTeam = (ch.GetTeam() == player.GetTeam());
                    }
                }

                Color color = sameTeam ? Color.green : Color.red;
                if (player != null && player.Is_Viewer && !player.Is_GP)
                {
                    // 观战但非 GP：沿用原配色（蓝/红），仅不再隐藏
                    color = (ch.GetTeam() != 0)
                        ? new Color(0.23529412f, 0.78431374f, 1f, 1f)
                        : Color.red;
                }
                if (levelSingleton != null &&
                    levelSingleton.game_type == global::RoomInfo.GameType.kGameTypeChiji &&
                    player != null)
                {
                    color = (uid != player.uid) ? Color.red : Color.green;
                }

                // 屏幕位置
                Vector3 sp = Camera.main.WorldToScreenPoint(ch.transform.position + new Vector3(0f, 1.6f, 0f));
                if (sp.z <= 0f) return false;

                // 强制 Alpha = 1
                const float ALPHA = 1f;
                color.a = ALPHA;

                // 绘制（与原逻辑一致）
                CodeUISys.CodeUI.Layer = global::InGameUIManager.HPLayer;
                CodeUISys.CodeUI.atlasName = "ingameF";
                CodeUISys.CodeUI.pivot = CodeUISys.Pivot.Center;
                CodeUISys.CodeUI.Aplha = ALPHA;
                CodeUISys.CodeUI.drawColor = color;
                CodeUISys.CodeUI.drawType = CodeUISys.DrawType.FillH;
                CodeUISys.CodeUI.fillAmount = hpPct;

                CodeUISys.CodeUI.drawSprtie("skin_ingame_BG05_row", new Rect(sp.x, sp.y, 99f, 8f), ch.hpName, -sp.z + 2f);
                CodeUISys.CodeUI.drawSprtie("skin_ingame_BG05", new Rect(sp.x, sp.y, 111f, 16f), ch.hpBGName, -sp.z + 1f);

                int dy = 20;
                if (shieldPct > 0f)
                {
                    CodeUISys.CodeUI.Layer = global::InGameUIManager.HPLayer;
                    CodeUISys.CodeUI.atlasName = "ingameF";
                    CodeUISys.CodeUI.Aplha = ALPHA;

                    CodeUISys.CodeUI.drawSprtie("skin_ingame_BG05", new Rect(sp.x, sp.y + (float)dy * CodeUISys.CodeUI.uiScale, 111f, 16f), ch.hpBGName + "_shield", -sp.z + 1f);
                    CodeUISys.CodeUI.drawColor = Color.yellow;
                    CodeUISys.CodeUI.drawType = CodeUISys.DrawType.FillH;
                    CodeUISys.CodeUI.fillAmount = shieldPct;
                    CodeUISys.CodeUI.drawSprtie("skin_ingame_BG05_row", new Rect(sp.x, sp.y + (float)dy * CodeUISys.CodeUI.uiScale, 99f, 8f), ch.hpName + "_shield", -sp.z + 2f);
                    dy += 20;
                }

                CodeUISys.CodeUI.fontAlignment = CodeUISys.FontAlignment.Center;
                CodeUISys.CodeUI.drawColor = color;
                CodeUISys.CodeUI.Aplha = ALPHA;
                CodeUISys.CodeUI.drawLabel(ch.GetName(), 16, new Vector2(sp.x, sp.y + (float)dy * CodeUISys.CodeUI.uiScale), ch.hpLabelName, -sp.z + 2f);

                // 同步把实例字段维持为 1，避免被其他逻辑读为 0
                tv.Field("hpAplha").SetValue(1f);

                // 我们已绘制，拦截原方法
                return false;
            }
            catch (Exception e)
            {
                FileLogger.Log("GamePatches", "[Character.UpdateBloodBar] prefix(force) error: " + e);
                // 出错时走原函数，避免影响游戏
                return true;
            }
        }
        [Obfuscation(Exclude = true, Feature = "-rename")]
        // 工具：按名称获取已加载的程序集
        static Assembly GetAsm(string name)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    if (asms[i].GetName().Name == name) return asms[i];
                }
                catch { }
            }
            return null;
        }
    }

    // Prefix: ChannelConnection.GrenadeHurt()
    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_ChannelConnection_GrenadeHurt_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("ChannelConnection");
                if (t == null) { FileLogger.Log("PATCH", "Type ChannelConnection not found"); return null; }

                Type[] argTypes = new Type[] { typeof(Character), typeof(int), typeof(byte), typeof(Vector3), typeof(bool), typeof(int) };

                var m = AccessTools.Method(t, "GrenadeHurt", argTypes);
                if (m == null)
                {
                    FileLogger.Log("PATCH", "Method GrenadeHurt() not found — dumping overloads:");
                    DumpOverloads(t, "GrenadeHurt");   // 打印所有重载帮你核对签名
                }
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(GrenadeHurt) error: " + e);
                return null;
            }
        }
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance, Character c, int uid, byte slot, Vector3 pos, bool half_damage, int owner_type = 0)
        {

            global::ChannelConnection ch = __instance as ChannelConnection;

            if (global::ASSingleton<global::GameStateManager>.Instance.CurStateType != global::GameStateType.Fight)
            {
                return false;
            }
            if (ch.game_state == global::ChannelConnection.GameState.kGameLeaving)
            {
                return false;
            }
            ch.BeginWrite();
            if (!GrenadeNotHurt.Enabled)
            {
                ch.WriteByte(114);
            }
            else if (uid != (int)ASSingleton<Level>.Instance.GetPlayer().uid)
            {
                // 115是没伤害，114是有伤害
                ch.WriteByte(115);
            }
            else
            {
                ch.WriteByte(114);
            }

            ch.WriteByte((!c.IsRobot) ? (byte)1 : (byte)0);
            ch.WriteInt(c.robot_uid);
            ch.WriteInt(uid);
            ch.WriteByte(slot);
            Vector3 data = pos + new Vector3(0f, 0.5f, 0f);
            ch.WriteVector3(data);
            if (GrenadeHalfHurt.Enabled && uid != (int)ASSingleton<Level>.Instance.GetPlayer().uid)
            {
                ch.WriteByte(true);
            }
            else
            {
                ch.WriteByte(half_damage);
            }
            ch.EndWrite();

            return false;
        }
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static void DumpOverloads(Type t, string name)
        {
            try
            {
                const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static |
                                        BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.FlattenHierarchy;
                var ms = t.GetMethods(BF);
                for (int i = 0; i < ms.Length; i++)
                {
                    var mi = ms[i];
                    if (mi.Name != name) continue;
                    var ps = mi.GetParameters();
                    var sb = new System.Text.StringBuilder();
                    sb.Append((mi.IsStatic ? "static " : "") + mi.ReturnType.Name + " " + mi.Name + "(");
                    for (int k = 0; k < ps.Length; k++)
                    {
                        if (k > 0) sb.Append(", ");
                        var pt = ps[k].ParameterType;
                        sb.Append(pt.IsByRef ? (pt.GetElementType().Name + "&") : pt.Name);
                    }
                    sb.Append(")");
                    FileLogger.Log("PATCH", "  overload -> " + (mi.DeclaringType != null ? mi.DeclaringType.FullName : "<null>") + "::" + sb.ToString());
                }
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "DumpOverloads error: " + e);
            }
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class FightState_Update_Patch
    {

        // 缓存私有字段：float staticTime; bool time_out_tip;
        static FieldInfo _fiStaticTime;
        static FieldInfo _fiTimeOutTip;
        static bool _fieldsScanned;
        [Obfuscation(Exclude = true, Feature = "-rename")]
        // —— 锁定目标方法：protected override void Update(float frameTime) —— //
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("FightState");
                if (t == null) { FileLogger.Log("PATCH", "Type FightState not found"); return null; }

                var m = AccessTools.Method(t, "Update", new Type[] { typeof(float) });
                if (m == null) FileLogger.Log("PATCH", "Method FightState.Update(float) not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(FightState.Update) error: " + e);
                return null;
            }
        }

        // —— 前缀：在进入原方法之前把 staticTime / time_out_tip 设成“安全值” —— //
        static void Prefix(object __instance /*, float frameTime*/)
        {
            if (!NotKick.Enabled || __instance == null) return;

            try
            {
                EnsureFieldCache(__instance.GetType());

                if (_fiStaticTime != null)
                    _fiStaticTime.SetValue(__instance, 0f);      // 防止 >60f / >autoLeaveTime

                if (_fiTimeOutTip != null)
                    _fiTimeOutTip.SetValue(__instance, false);    // 让 (staticTime > 60f && time_out_tip) 直接为假
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "[FightState.Update] Prefix error: " + e);
            }
        }

        // —— 私有字段一次性缓存 —— //
        static void EnsureFieldCache(Type fightStateType)
        {
            if (_fieldsScanned || fightStateType == null) return;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            try
            {
                _fiStaticTime = fightStateType.GetField("staticTime", BF);
                _fiTimeOutTip = fightStateType.GetField("time_out_tip", BF);

                FileLogger.Log(
                    "PATCH",
                    "[FightState.Update] fields cached: staticTime=" + (_fiStaticTime != null) +
                    ", time_out_tip=" + (_fiTimeOutTip != null)
                );
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "EnsureFieldCache error: " + e);
            }
            finally
            {
                _fieldsScanned = true;
            }
        }

        // —— 工具：找程序集 —— //
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    /* =========================
     *  WeaponBase.Ready() 仅此一个
     * ========================= */
    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_WeaponBase_Ready
    {
        // —— 通用：把 ObscuredFloat/Bool/Int 字段安全地设值 —— //
        internal static bool TrySetField(object obj, string fieldName, object plainValue)
        {
            if (obj == null) return false;
            var t = obj.GetType();
            var f = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) { FileLogger.Log("Ready", $"{t.Name}.{fieldName} not found"); return false; }

            var ft = f.FieldType;
            try
            {
                object boxed = null;

                // 1) 直接同型
                if (plainValue != null && ft.IsAssignableFrom(plainValue.GetType()))
                    boxed = plainValue;
                else
                {
                    // 2) 识别 ObscuredX
                    var fn = ft.FullName ?? "";
                    if (fn == "CodeStage.AntiCheat.ObscuredTypes.ObscuredFloat" && plainValue is float fv)
                        boxed = BuildObscuredFromPrimitive(ft, fv);
                    else if (fn == "CodeStage.AntiCheat.ObscuredTypes.ObscuredBool" && plainValue is bool bv)
                        boxed = BuildObscuredFromPrimitive(ft, bv);
                    else if (fn == "CodeStage.AntiCheat.ObscuredTypes.ObscuredInt" && plainValue is int iv)
                        boxed = BuildObscuredFromPrimitive(ft, iv);
                    else
                    {
                        // 3) 退化：尝试 Convert.ChangeType（float->double 等）
                        try { boxed = Convert.ChangeType(plainValue, ft); } catch { }
                    }
                }

                if (boxed == null)
                {
                    FileLogger.Log("Ready", $"Set {t.Name}.{fieldName} failed: cannot convert {plainValue?.GetType().Name} -> {ft.Name}");
                    return false;
                }

                f.SetValue(obj, boxed);
                return true;
            }
            catch (Exception e)
            {
                FileLogger.Log("Ready", $"Set {t.Name}.{fieldName} error: {e}");
                return false;
            }
        }

        // 通过 op_Implicit(primitive) 或 ctor(primitive) 构造 ObscuredX 实例
        static object BuildObscuredFromPrimitive(Type obscuredType, object primitive)
        {
            try
            {
                var mi = obscuredType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { primitive.GetType() }, null);
                if (mi != null)
                    return mi.Invoke(null, new[] { primitive });
            }
            catch { }

            try
            {
                var ctor = obscuredType.GetConstructor(new[] { primitive.GetType() });
                if (ctor != null)
                    return ctor.Invoke(new[] { primitive });
            }
            catch { }

            // 最后退化：给一个默认实例（大概率不生效，但不至于崩）
            return Activator.CreateInstance(obscuredType);
        }
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("Ready", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("WeaponBase") ?? asm.GetTypes().FirstOrDefault(x => x.Name == "WeaponBase");
                if (t == null) { FileLogger.Log("Ready", "Type WeaponBase not found"); return null; }

                // Ready(): 无参实例方法；找 public / nonpublic 都试一下
                var m = t.GetMethod("Ready", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) { FileLogger.Log("Ready", "WeaponBase.Ready not found"); return null; }
                if (m.IsAbstract) { FileLogger.Log("Ready", "WeaponBase.Ready is abstract, skip"); return null; }

                FileLogger.Log("Ready", "Hook -> WeaponBase.Ready()");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("Ready", "TargetMethod(WeaponBase.Ready) error: " + e);
                return null;
            }
        }

        // Prefix：如需强制 Ready 成功，在这里返回 false 并设置 __result=true
        static bool Prefix(object __instance, ref bool __result)
        {
            if (!WeaponNotCD.Enabled) return true;
            var weapon = __instance as WeaponBase;
            try
            {
                // 让 Attack 的 Time.time >= next_fire_time 始终成立
                TrySetField(__instance, "next_fire_time", Time.time - 0.1f);

                // 让 Ready() 里的 cool_down_ready 判定为真
                TrySetField(__instance, "cool_down_ready", true);
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                FileLogger.Log("Ready", "[WeaponBase.Ready] prefix error: " + e);
            }
            return true; // 继续执行原方法
        }

        // Postfix：这里仅记录最终结果（不改结果）
        static void Postfix(object __instance, ref bool __result)
        {
            if (!WeaponNotCD.Enabled) return;
            try
            {
                //FileLogger.Log("Ready", $"WeaponBase.Ready() -> {__result}");
            }
            catch (Exception e)
            {
                FileLogger.Log("Ready", "[WeaponBase.Ready] postfix error: " + e);
            }
        }

        internal static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    /* ==================================
     *  KnifeBaseController.Ready() 仅此一个
     * ================================== */
    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_KnifeBaseController_Ready
    {
        // —— 通用：把 ObscuredFloat/Bool/Int 字段安全地设值 —— //
        internal static bool TrySetField(object obj, string fieldName, object plainValue)
        {
            if (obj == null) return false;
            var t = obj.GetType();
            var f = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) { FileLogger.Log("Ready", $"{t.Name}.{fieldName} not found"); return false; }

            var ft = f.FieldType;
            try
            {
                object boxed = null;

                // 1) 直接同型
                if (plainValue != null && ft.IsAssignableFrom(plainValue.GetType()))
                    boxed = plainValue;
                else
                {
                    // 2) 识别 ObscuredX
                    var fn = ft.FullName ?? "";
                    if (fn == "CodeStage.AntiCheat.ObscuredTypes.ObscuredFloat" && plainValue is float fv)
                        boxed = BuildObscuredFromPrimitive(ft, fv);
                    else if (fn == "CodeStage.AntiCheat.ObscuredTypes.ObscuredBool" && plainValue is bool bv)
                        boxed = BuildObscuredFromPrimitive(ft, bv);
                    else if (fn == "CodeStage.AntiCheat.ObscuredTypes.ObscuredInt" && plainValue is int iv)
                        boxed = BuildObscuredFromPrimitive(ft, iv);
                    else
                    {
                        // 3) 退化：尝试 Convert.ChangeType（float->double 等）
                        try { boxed = Convert.ChangeType(plainValue, ft); } catch { }
                    }
                }

                if (boxed == null)
                {
                    FileLogger.Log("Ready", $"Set {t.Name}.{fieldName} failed: cannot convert {plainValue?.GetType().Name} -> {ft.Name}");
                    return false;
                }

                f.SetValue(obj, boxed);
                return true;
            }
            catch (Exception e)
            {
                FileLogger.Log("Ready", $"Set {t.Name}.{fieldName} error: {e}");
                return false;
            }
        }

        // 通过 op_Implicit(primitive) 或 ctor(primitive) 构造 ObscuredX 实例
        static object BuildObscuredFromPrimitive(Type obscuredType, object primitive)
        {
            try
            {
                var mi = obscuredType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { primitive.GetType() }, null);
                if (mi != null)
                    return mi.Invoke(null, new[] { primitive });
            }
            catch { }

            try
            {
                var ctor = obscuredType.GetConstructor(new[] { primitive.GetType() });
                if (ctor != null)
                    return ctor.Invoke(new[] { primitive });
            }
            catch { }

            // 最后退化：给一个默认实例（大概率不生效，但不至于崩）
            return Activator.CreateInstance(obscuredType);
        }
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("Ready", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("KnifeBaseController") ?? asm.GetTypes().FirstOrDefault(x => x.Name == "KnifeBaseController");
                if (t == null) { FileLogger.Log("Ready", "Type KnifeBaseController not found"); return null; }

                var m = t.GetMethod("Ready", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) { FileLogger.Log("Ready", "KnifeBaseController.Ready not found"); return null; }
                if (m.IsAbstract) { FileLogger.Log("Ready", "KnifeBaseController.Ready is abstract, skip"); return null; }

                FileLogger.Log("Ready", "Hook -> KnifeBaseController.Ready()");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("Ready", "TargetMethod(KnifeBaseController.Ready) error: " + e);
                return null;
            }
        }

        static bool Prefix(object __instance, ref bool __result)
        {
            if (!WeaponNotCD.Enabled) return true;
            var weapon = __instance as WeaponBase;
            try
            {
                // 让 Attack 的 Time.time >= next_fire_time 始终成立
                TrySetField(__instance, "next_fire_time", Time.time - 0.1f);

                // 让 Ready() 里的 cool_down_ready 判定为真
                TrySetField(__instance, "cool_down_ready", true);

                __result = true;
                return false;
            }
            catch (Exception e)
            {
                FileLogger.Log("Ready", "[WeaponBase.Ready] prefix error: " + e);
            }
            return true; // 继续执行原方法
        }

        static void Postfix(object __instance, ref bool __result)
        {
            if (!WeaponNotCD.Enabled) return;
            try
            {
                //FileLogger.Log("Ready", $"KnifeBaseController.Ready() -> {__result}");
            }
            catch (Exception e)
            {
                FileLogger.Log("Ready", "[KnifeBaseController.Ready] postfix error: " + e);
            }
        }

        internal static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }
    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_GunBaseController_FireCheck_Transpiler
    {
        static MethodBase TargetMethod()
        {
            var asm = GetAsm("Assembly-CSharp");
            var t = asm?.GetType("GunBaseController");                 // 注意命名空间问题
            var m = t != null ? AccessTools.Method(t, "FireCheck", new[] { typeof(bool) }) : null;
            FileLogger.Log("BNR/GUN", $"Target={m}");
            return m;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = new List<CodeInstruction>(instructions);
            FileLogger.Log("BNR/GUN", $"IL count={list.Count}");

            // 0) 找到 if(flag && Physics.Raycast(...)) 对应的短路入口：
            //    形态一般：ldloc flag ; brfalse Lskip ; (随后调用 Physics.Raycast) ; brfalse Lskip ; ...
            int brToRayIdx = -1;      // 指向 "ldloc flag"
            int rayCallIdx = -1;      // 指向 Raycast 的 call/callvirt
            LocalBuilder flagLocal = null;

            for (int i = 1; i < list.Count; i++)
            {
                var prev = list[i - 1];
                var cur = list[i];

                bool isBrFalse = (cur.opcode == OpCodes.Brfalse || cur.opcode == OpCodes.Brfalse_S);
                bool prevIsLdloc = (prev.opcode == OpCodes.Ldloc || prev.opcode == OpCodes.Ldloc_S || prev.opcode == OpCodes.Ldloc_0 || prev.opcode == OpCodes.Ldloc_1 || prev.opcode == OpCodes.Ldloc_2 || prev.opcode == OpCodes.Ldloc_3);

                if (isBrFalse && prevIsLdloc && prev.operand is LocalBuilder lb && lb.LocalType == typeof(bool))
                {
                    // 进一步确认这就是 "if(flag && Raycast(...))" 的那个 brfalse：在它后面不远处应该紧跟一个 Physics.Raycast
                    for (int k = i + 1; k < Math.Min(i + 20, list.Count); k++)
                    {
                        var ci = list[k];
                        if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) &&
                            ci.operand is MethodInfo mi &&
                            mi.DeclaringType == typeof(Physics) &&
                            mi.Name == "Raycast")
                        {
                            brToRayIdx = i - 1;   // ldloc flag
                            rayCallIdx = k;       // call Physics.Raycast
                            flagLocal = lb;
                            break;
                        }
                    }
                }
                if (rayCallIdx >= 0) break;
            }

            if (flagLocal == null || brToRayIdx < 0 || rayCallIdx < 0)
            {
                FileLogger.Log("BNR/GUN", "FAIL: cannot locate short-circuit (flag && Raycast) anchor.");
                return list;
            }

            // 1) 找 Ray 与 RaycastHit 的本地变量（靠近 rayCallIdx 向前回溯收集）
            LocalBuilder rayLocal = null;
            LocalBuilder hitLocal = null;

            // 回溯一个较大窗口，寻找 ldloca.s Ray / ldloca.s RaycastHit
            for (int k = rayCallIdx - 1; k >= 0 && k >= rayCallIdx - 60; k--)
            {
                var ci = list[k];
                if ((ci.opcode == OpCodes.Ldloca || ci.opcode == OpCodes.Ldloca_S) && ci.operand is LocalBuilder lb)
                {
                    if (lb.LocalType == typeof(RaycastHit) && hitLocal == null) hitLocal = lb;
                    if (lb.LocalType == typeof(Ray) && rayLocal == null) rayLocal = lb;
                }
                if (rayLocal != null && hitLocal != null) break;
            }

            // 兜底：全局扫一下
            if (rayLocal == null)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if ((list[i].operand is LocalBuilder lb) &&
                        (lb.LocalType == typeof(Ray)) &&
                        (list[i].opcode == OpCodes.Ldloca_S || list[i].opcode == OpCodes.Ldloc_S || list[i].opcode == OpCodes.Stloc_S))
                    { rayLocal = lb; break; }
                }
            }
            if (hitLocal == null)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if ((list[i].operand is LocalBuilder lb) &&
                        (lb.LocalType == typeof(RaycastHit)) &&
                        (list[i].opcode == OpCodes.Ldloca_S || list[i].opcode == OpCodes.Ldloc_S || list[i].opcode == OpCodes.Stloc_S))
                    { hitLocal = lb; break; }
                }
            }

            if (rayLocal == null || hitLocal == null)
            {
                FileLogger.Log("BNR/GUN", $"FAIL: locals not found (ray:{rayLocal != null}, hit:{hitLocal != null})");
                return list;
            }

            FileLogger.Log("BNR/GUN", $"Anchors OK: flag=V_{flagLocal.LocalIndex}, ray=V_{rayLocal.LocalIndex}, hit=V_{hitLocal.LocalIndex}, insert@{brToRayIdx}");

            // 2) 在短路入口处插入：flag = BNR_IL2.CameraStraightForGunBase(this, ref ray, ref hit);
            var mHelper = AccessTools.Method(typeof(BNR_IL2), nameof(BNR_IL2.CameraStraightForGunBase),
                            new[] { typeof(object), typeof(Ray).MakeByRefType(), typeof(RaycastHit).MakeByRefType() });
            if (mHelper == null)
            {
                FileLogger.Log("BNR/GUN", "FAIL: helper not found");
                return list;
            }

            // 注入在短路入口（原来的 ldloc flag 之前）
            var injected = new List<CodeInstruction>
{
            // flag = flag || Helper(this, ref ray, ref hit);

            // 先取原 flag
            new CodeInstruction(OpCodes.Ldloc_S, flagLocal),

            // 调 helper
            new CodeInstruction(OpCodes.Ldarg_0),                 // this
            new CodeInstruction(OpCodes.Ldloca_S, rayLocal),      // ref ray
            new CodeInstruction(OpCodes.Ldloca_S, hitLocal),      // ref hit
            new CodeInstruction(OpCodes.Call, mHelper),           // bool

            // 做 OR 合并
            new CodeInstruction(OpCodes.Or),

            // 回写 flag
            new CodeInstruction(OpCodes.Stloc_S, flagLocal),
        };
            list.InsertRange(brToRayIdx, injected);
            FileLogger.Log("BNR/GUN", "Injected before short-circuit.");

            return list;
        }

        static Assembly GetAsm(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            return null;
        }
    }

    [Obfuscation(
    Exclude = true,                 // 排除本类型
    ApplyToMembers = true,          // 并排除所有成员
    Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
)]
    public static class BNR_IL2
    {
        /// <summary>
        /// GunBaseController.FireCheck 的“无扩散二段射线”分支。
        /// 覆盖 ray / hit，并返回新的 flag。
        /// </summary>
        public static bool CameraStraightForGunBase(object self, ref Ray ray, ref RaycastHit hit)
        {
            if (!BulletNoRecoil.Enabled) return false;

            var inst = self as GunBaseController; // __instance
            if (inst == null || inst.owner == null) return false;

            // —— 第一段：从相机位置 + 纯 forward 发射（GunBase 原版第一段用的是 CameraObj.Instance.shootForward，
            //    这里我们明确要“无扩散”，所以直接用 Camera.main.forward）
            Vector3 pos = Camera.main.transform.position;
            Vector3 fwd = Camera.main.transform.forward; fwd.Normalize();

            ray.origin = pos;
            ray.direction = fwd;

            int mask = LayerMask.GetMask("Terrarin") | LayerMask.GetMask("kController") | LayerMask.GetMask("Weapon");
            float distance = 500f; // GunBase 原码里就是 500

            bool flag = Physics.Raycast(ray, out hit, distance, mask);

            // —— 第二段：与原版一致，从“角色身上+Vector3.up”朝第一段命中点
            if (flag)
            {
                Vector3 o = inst.owner.transform.position + Vector3.up;
                ray.origin = o;
                ray.direction = (hit.point - o).normalized;

                // GunBase 原码第二段也用同样的 distance（500f），而不是 info.range
                flag = Physics.Raycast(ray, out hit, distance, mask);
            }

            return flag;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_SniperGunController_FireCheck_Transpiler
    {
        static MethodBase TargetMethod()
        {
            var asm = GetAsm("Assembly-CSharp");
            var t = asm?.GetType("SniperGunController"); // 注意是否有命名空间
            var m = t != null ? AccessTools.Method(t, "FireCheck", new[] { typeof(bool) }) : null;
            FileLogger.Log("BNR/TP", $"Target={m}");
            return m;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = new List<CodeInstruction>(instructions);
            FileLogger.Log("BNR/TP", $"IL count={list.Count}");

            // 1) 找 if(flag) 的读取点（ldloc flag ; brfalse）
            int ifLoadIdx = -1;
            LocalBuilder flagLocal = null;
            for (int i = 1; i < list.Count; i++)
            {
                var prev = list[i - 1];
                var cur = list[i];
                bool isBrFalse = (cur.opcode == OpCodes.Brfalse || cur.opcode == OpCodes.Brfalse_S);
                bool prevIsLdloc = (prev.opcode == OpCodes.Ldloc || prev.opcode == OpCodes.Ldloc_S);
                if (isBrFalse && prevIsLdloc && prev.operand is LocalBuilder lb && lb.LocalType == typeof(bool))
                {
                    flagLocal = lb;
                    ifLoadIdx = i - 1; // 指向 ldloc flag
                    break;
                }
            }
            if (flagLocal == null || ifLoadIdx < 0)
            {
                FileLogger.Log("BNR/TP", "FAIL: cannot find if(flag) ldloc/brfalse");
                return list;
            }

            // 2) 找“第二段 Raycast”用到的 RaycastHit 本地
            //   2.1 先找到 if(flag) 之前最近的一次 Physics.Raycast（不限定重载）
            int raycastCallIdx = -1;
            for (int i = ifLoadIdx - 1; i >= 0; i++)
            {
                var ci = list[i];
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) &&
                    ci.operand is MethodInfo mi &&
                    mi.DeclaringType == typeof(Physics) &&
                    mi.Name == "Raycast")
                {
                    raycastCallIdx = i;
                    break;
                }
            }
            if (raycastCallIdx < 0)
            {
                FileLogger.Log("BNR/TP", "FAIL: cannot find any Physics.Raycast before if(flag)");
                return list;
            }

            //   2.2 在这次调用前窗口内寻找 ldloca.s RaycastHit
            LocalBuilder hitLocal = null;
            for (int k = raycastCallIdx - 1, limit = Math.Max(0, raycastCallIdx - 60); k >= limit; k--)
            {
                if (list[k].opcode == OpCodes.Ldloca_S && list[k].operand is LocalBuilder lb && lb.LocalType == typeof(RaycastHit))
                {
                    hitLocal = lb;
                    break;
                }
            }

            //   2.3 兜底：全局扫描所有出现过的 RaycastHit 本地，取最后一个
            if (hitLocal == null)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var ci = list[i];
                    if ((ci.operand is LocalBuilder lb1 && lb1.LocalType == typeof(RaycastHit)) &&
                        (ci.opcode == OpCodes.Ldloca_S || ci.opcode == OpCodes.Ldloc_S || ci.opcode == OpCodes.Stloc_S))
                    {
                        hitLocal = lb1;
                        break;
                    }
                }
            }
            if (hitLocal == null)
            {
                FileLogger.Log("BNR/TP", "FAIL: RaycastHit local still not found");
                return list;
            }

            // 3) 找最终被 Shoot(...) 使用的 Ray 本地
            LocalBuilder rayLocal = null;
            //   3.1 先从调用 Shoot 处回扫最近的 ldloca.s Ray
            MethodInfo callShoot1 = null, callShoot2 = null;
            {
                var asm = GetAsm("Assembly-CSharp");
                var tConn = asm?.GetType("ChannelConnection");
                var tNov = asm?.GetType("NoviceOffManager");
                var tHit = asm?.GetType("HitMessage");
                callShoot1 = (tConn != null && tHit != null)
                    ? AccessTools.Method(tConn, "Shoot", new[] { typeof(Vector3), typeof(Vector3), tHit, typeof(byte), typeof(bool), typeof(Vector3) })
                    : null;
                callShoot2 = (tNov != null && tHit != null)
                    ? AccessTools.Method(tNov, "Shoot", new[] { typeof(Vector3), typeof(Vector3), tHit, typeof(byte), typeof(bool), typeof(Vector3) })
                    : null;
            }
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var ci = list[i];
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) &&
                    ci.operand is MethodInfo mi &&
                    (mi == callShoot1 || mi == callShoot2))
                {
                    for (int k = i - 1; k >= 0 && k >= i - 40; k--)
                    {
                        if (list[k].opcode == OpCodes.Ldloca_S &&
                            list[k].operand is LocalBuilder lb &&
                            lb.LocalType == typeof(Ray))
                        {
                            rayLocal = lb;
                            break;
                        }
                    }
                    if (rayLocal != null) break;
                }
            }
            //   3.2 兜底：回溯最近一次给 Ray.origin/dir 赋值时使用的 ldloca.s Ray
            if (rayLocal == null)
            {
                for (int i = ifLoadIdx - 1; i >= 2; i--)
                {
                    if (list[i].opcode == OpCodes.Stfld &&
                        list[i].operand is FieldInfo fi && fi.DeclaringType == typeof(Ray))
                    {
                        if (list[i - 2].opcode == OpCodes.Ldloca_S &&
                            list[i - 2].operand is LocalBuilder lb &&
                            lb.LocalType == typeof(Ray))
                        {
                            rayLocal = lb;
                            break;
                        }
                    }
                }
            }
            if (rayLocal == null)
            {
                FileLogger.Log("BNR/TP", "FAIL: Ray local not found");
                return list;
            }

            FileLogger.Log("BNR/TP", $"Anchors OK: flag=V_{flagLocal.LocalIndex}, hit=V_{hitLocal.LocalIndex}, ray=V_{rayLocal.LocalIndex}, insert@{ifLoadIdx}");

            // 4) 插入：flag = BNR_IL.CameraStraightBranch(this, ref ray, ref hit)
            var mHelper = AccessTools.Method(typeof(BNR_IL), nameof(BNR_IL.CameraStraightBranch),
                            new[] { typeof(object), typeof(Ray).MakeByRefType(), typeof(RaycastHit).MakeByRefType() });
            if (mHelper == null)
            {
                FileLogger.Log("BNR/TP", "FAIL: helper not found");
                return list;
            }

            // 注入在短路入口（原来的 ldloc flag 之前）
            var injected = new List<CodeInstruction>
            {
                // flag = flag || Helper(this, ref ray, ref hit);

                // 先取原 flag
                new CodeInstruction(OpCodes.Ldloc_S, flagLocal),

                // 调 helper
                new CodeInstruction(OpCodes.Ldarg_0),                 // this
                new CodeInstruction(OpCodes.Ldloca_S, rayLocal),      // ref ray
                new CodeInstruction(OpCodes.Ldloca_S, hitLocal),      // ref hit
                new CodeInstruction(OpCodes.Call, mHelper),           // bool

                // 做 OR 合并
                new CodeInstruction(OpCodes.Or),

                // 回写 flag
                new CodeInstruction(OpCodes.Stloc_S, flagLocal),
            };
            list.InsertRange(ifLoadIdx, injected);
            FileLogger.Log("BNR/TP", "Injected before if(flag).");
            return list;
        }

        static Assembly GetAsm(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            return null;
        }
    }

    [Obfuscation(
    Exclude = true,                 // 排除本类型
    ApplyToMembers = true,          // 并排除所有成员
    Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
)]
    public static class BNR_IL
    {
        public static bool CameraStraightBranch(object self, ref Ray ray, ref RaycastHit hit)
        {
            if (!BulletNoRecoil.Enabled)
                return false;

            FileLogger.Log("BNR", $"before inject ray={ray.origin}->{ray.direction}  hit={hit.point}");

            Vector3 pos = Camera.main.transform.position;
            Vector3 fwd = Camera.main.transform.forward; fwd.Normalize();

            ray.origin = pos;
            ray.direction = fwd;

            int mask = LayerMask.GetMask("Terrarin") | LayerMask.GetMask("kController") | LayerMask.GetMask("Weapon");
            bool flag = Physics.Raycast(ray, out hit, 1200f, mask);

            if (flag)
            {
                var inst = self as SniperGunController;
                if (inst != null)
                {
                    Vector3 o = inst.owner.transform.position + Vector3.up;
                    ray.origin = o;
                    ray.direction = (hit.point - o).normalized;
                    float range;
                    if (inst.info != null)
                    {
                        range = inst.info.range;
                    }
                    else
                    {
                        range = 1200f;
                    }

                    flag = Physics.Raycast(ray, out hit, range, mask);
                }
            }

            FileLogger.Log("BNR", $"after  inject ray={ray.origin}->{ray.direction}  hit={hit.point}  flag={flag}");
            return flag;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_ChannelConnection_Shoot_Prefix
    {
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("ChannelConnection");
                if (t == null) { FileLogger.Log("PATCH", "Type ChannelConnection not found"); return null; }

                // Shoot(Vector3 position, Vector3 direction, HitMessage hit_message, byte slot, bool do_effect, Vector3 velocity)
                Type[] argTypes = new Type[]
                {
                typeof(Vector3), typeof(Vector3),
                typeof(global::HitMessage), typeof(byte),
                typeof(bool), typeof(Vector3)
                };

                var m = AccessTools.Method(t, "Shoot", argTypes);
                if (m == null)
                {
                    FileLogger.Log("PATCH", "Method Shoot(...) not found — dumping overloads:");
                    DumpOverloads(t, "Shoot");
                }
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(ChannelConnection.Shoot) error: " + e);
                return null;
            }
        }

        // 完全复现原方法
        static bool Prefix(
            object __instance,
            Vector3 position,
            Vector3 direction,
            global::HitMessage hit_message,
            byte slot,
            bool do_effect,
            Vector3 velocity)
        {
            try
            {
                //FileLogger.Log("PATCH", "[ChannelConnection.Shoot] 挥刀 ");
                var ch = __instance as global::ChannelConnection;
                if (ch == null) return false;

                // if (this.state != State.kInGame) return;
                // if (this.game_state == GameState.kGameLeaving) return;
                if (ch.state != global::ChannelConnection.State.kInGame)
                    return false;
                if (ch.game_state == global::ChannelConnection.GameState.kGameLeaving)
                    return false;

                ch.BeginWrite();
                ch.WriteByte(106);
                ch.WriteByte(hit_message.is_real_man);
                ch.WriteInt(hit_message.robot_uid);

                // WriteFloat(Time.time - this.game_server_sync_local_time + this.game_server_time);
                var tr = Traverse.Create(ch);
                float gsSyncLocal = 0f, gsServerTime = 0f;
                try { gsSyncLocal = tr.Field("game_server_sync_local_time").GetValue<float>(); } catch { }
                try { gsServerTime = tr.Field("game_server_time").GetValue<float>(); } catch { }
                ch.WriteFloat(Time.time - gsSyncLocal + gsServerTime);

                ch.WriteByte(Convert.ToByte(do_effect));
                //ch.WriteByte(Convert.ToByte(false));

                NetworkStream streamObj = null;
                try { streamObj = tr.Field("_stream").GetValue<NetworkStream>(); } catch { }
                Vector3 b = new Vector3(0f, 0.5f, 0f);
                //position = CharacterManager.Instance.GetCharacter(hit_message.uid).net_sync_position + b;
                ConnectionDef.WriteCharacterPosition(streamObj, position);
                ConnectionDef.WriteCharacterEulerAngles(streamObj, direction.normalized);

                ch.WriteByte(slot);
                if (AimTrack.Enabled && AimTrack.currentTarget && !(Level.Instance.GetPlayer().mWeapon is KnifeBaseController))
                {
                    Character player = global::ASSingleton<global::Level>.Instance.GetPlayer();
                    var uid = (int)AimTrack.currentTarget.uid ^ player.currentSpreadIndex;
                    ch.WriteByte((byte)uid);
                    if (player.mWeapon is SniperGunController)
                    {
                        ch.WriteShort(1198);
                    }
                    else if (player.mWeapon is GunBaseController)
                    {
                        ch.WriteShort(4);
                    }

                    ch.WriteByte((byte)4);
                }
                else
                {
                    if (hit_message.uid != 0)
                    {
                        ch.WriteByte((byte)hit_message.uid);
                        ch.WriteShort(hit_message.distance);
                        ch.WriteByte((byte)hit_message.part);
                    }
                    else
                    {
                        ch.WriteByte(0);
                    }
                }

                ch.WriteInt(hit_message.enc);
                ch.WriteFloat(hit_message.spread);

                ch.WriteByte((byte)hit_message.current_sight);

                ch.EndWrite();
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "[ChannelConnection.Shoot] prefix error: " + e);
            }
            // 跳过原方法
            return false;
        }

        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }

        static void DumpOverloads(Type t, string name)
        {
            try
            {
                const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static |
                                        BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.FlattenHierarchy;
                var ms = t.GetMethods(BF);
                for (int i = 0; i < ms.Length; i++)
                {
                    var mi = ms[i];
                    if (mi.Name != name) continue;
                    var ps = mi.GetParameters();
                    var sb = new System.Text.StringBuilder();
                    sb.Append((mi.IsStatic ? "static " : "") + mi.ReturnType.Name + " " + mi.Name + "(");
                    for (int k = 0; k < ps.Length; k++)
                    {
                        if (k > 0) sb.Append(", ");
                        var pt = ps[k].ParameterType;
                        sb.Append(pt.IsByRef ? (pt.GetElementType().Name + "&") : pt.Name);
                    }
                    sb.Append(")");
                    FileLogger.Log("PATCH", "  overload -> " + (mi.DeclaringType != null ? mi.DeclaringType.FullName : "<null>") + "::" + sb.ToString());
                }
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "DumpOverloads error: " + e);
            }
        }
    }


    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Input_GetKey_KeyCode_Prefix
    {
        // 和你 OnMapLoaded 的 Hook 一样：提供 TargetMethod()
        static MethodBase TargetMethod()
        {
            try
            {
                var t = typeof(UnityEngine.Input);
                var m = AccessTools.Method(t, "GetKey", new Type[] { typeof(KeyCode) });
                if (m == null) FileLogger.Log("PATCH", "UnityEngine.Input.GetKey(KeyCode) not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(Input.GetKey KeyCode) error: " + e);
                return null;
            }
        }

        // Prefix：如果是“开火键”且 WantsFire=true，则直接返回 true 并跳过原函数
        static bool Prefix(KeyCode key, ref bool __result)
        {
            try
            {
                // 读取游戏里配置的“开火键”
                var fireKey = ASSingleton<GameConfig>.Instance.KeyDic[ActionType.kActionFire];

                if ((key == fireKey && AutoFire.AutoFireAllowed) || (key == fireKey && SpinTop.Enabled))
                {
                    __result = true;
                    return false; // 跳过原 Input.GetKey
                }
            }
            catch { /* 安全兜底 */ }

            return true; // 调用原 Input.GetKey
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Input_GetKey_String_Prefix
    {
        static MethodBase TargetMethod()
        {
            try
            {
                var t = typeof(UnityEngine.Input);
                var m = AccessTools.Method(t, "GetKey", new Type[] { typeof(string) });
                if (m == null) FileLogger.Log("PATCH", "UnityEngine.Input.GetKey(string) not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(Input.GetKey string) error: " + e);
                return null;
            }
        }

        // 这里不要用 Enum.TryParse（老 mscorlib 没有）；我们自己实现一个
        private static bool TryParseKeyCode(string name, out KeyCode kc)
        {
            kc = KeyCode.None;
            if (string.IsNullOrEmpty(name))
                return false;
            try
            {
                kc = (KeyCode)Enum.Parse(typeof(KeyCode), name, true); // ignoreCase:true
                return true;
            }
            catch { return false; }
        }

        static bool Prefix(string name, ref bool __result)
        {
            try
            {
                if (TryParseKeyCode(name, out var keyCode))
                {
                    var fireKey = ASSingleton<GameConfig>.Instance.KeyDic[ActionType.kActionFire];
                    if ((keyCode == fireKey && AutoFire.AutoFireAllowed) || (keyCode == fireKey && SpinTop.Enabled))
                    {
                        __result = true;
                        return false; // 跳过原 Input.GetKey(string)
                    }
                }
            }
            catch { /* 忽略异常，保证不崩 */ }

            return true; // 走原始 GetKey(string)
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]

    [HarmonyPatch]
    public static class Patch_LobbyConnection_AddTextRpc
    {
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("LobbyConnection");
                if (t == null) { FileLogger.Log("PATCH", "Type LobbyConnection not found"); return null; }

                var m = AccessTools.Method(t, "AddTextRpc", new Type[] {
                typeof(string),
                typeof(global::LobbyConnection.RpcCallback),
                typeof(Dictionary<string,string>)
            });
                if (m == null) FileLogger.Log("PATCH", "Method LobbyConnection.AddTextRpc not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(AddTextRpc) error: " + e);
                return null;
            }
        }

        // 关键：通过 ref 参数拿到 func/callback/argument，交给 UI 侧处理（可改/包装）
        static bool Prefix(object __instance,
                           ref string func,
                           ref global::LobbyConnection.RpcCallback callback,
                           ref Dictionary<string, string> argument)
        {
            try
            {
                RpcLabUI.OnBeforeAddTextRpc(__instance, ref func, ref callback, ref argument);
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "[LobbyConnection.AddTextRpc] prefix error: " + e);
            }
            return true; // 继续执行原方法
        }

        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]

    [HarmonyPatch]
    public static class Patch_LobbyConnection_rpcCallBack
    {
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("LobbyConnection");
                if (t == null) { FileLogger.Log("PATCH", "Type LobbyConnection not found"); return null; }

                var m = AccessTools.Method(t, "rpcCallBack", new Type[] {
                typeof(string)
            });
                if (m == null) FileLogger.Log("PATCH", "Method LobbyConnection.rpcCallBack not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(rpcCallBack) error: " + e);
                return null;
            }
        }

        // 关键：通过 ref 参数拿到 func/callback/argument，交给 UI 侧处理（可改/包装）
        static bool Prefix(LobbyConnection __instance,
                           ref string data)
        {
            try
            {
                global::WaitingPanel.instance.SetActive(false);

                if (AuctionMonitor.IsRunning)
                {
                    bool isMonitorList = (__instance.rpcRequest.func == "auction_list");
                    if (isMonitorList)
                    {
                        if (__instance.rpcRequest.callback != null)
                        {
                            __instance.rpcRequest.callback(data);
                        }
                        __instance.rpcRequest = __instance.rpcRequest.chlid;
                        Traverse.Create(__instance).Method("runRpcRequest").GetValue();

                        return false;
                    }

                    if (__instance.rpcRequest.func == "auction_buy")
                    {
                        if (__instance.rpcRequest.callback != null)
                        {
                            __instance.rpcRequest.callback(data);
                        }

                        __instance.rpcRequest.callback = null;

                        return true;
                    }
                }

                data = data.Replace("\r", string.Empty);
                UniLua.LuaState luaState = new UniLua.LuaState(null);
                luaState.DoString(data);
                bool flag = false;
                if (luaState["error"] != null)
                {
                    string text = luaState["error"].ToString();
                    if (text == "msgbox_common_num_1001" || text == "msgbox_common_conditionkey_001")
                    {
                        global::UITools.CheckError(text);
                    }
                    flag = global::UITools.CheckCurrency(text);
                }
                if (__instance.rpcRequest.callback != null)
                {
                    __instance.rpcRequest.callback(data);
                }
                __instance.rpcRequest = __instance.rpcRequest.chlid;
                Traverse.Create(__instance).Method("runRpcRequest").GetValue();
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "[LobbyConnection.rpcCallBack] prefix error: " + e);
            }
            return false;
        }

        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_UITakeCardManager_ref_Prefix
    {
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("UITakeCardManager");
                if (t == null) { FileLogger.Log("PATCH", "Type UITakeCardManager not found"); return null; }

                var m = AccessTools.Method(t, "Refresh");
                if (m == null) FileLogger.Log("PATCH", "Method Refresh not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(Refresh) error: " + e);
                return null;
            }
        }

        static void Postfix(object __instance)
        {
            try
            {
                UniLua.LuaState luaState = new UniLua.LuaState(null);
                luaState.DoString(GlobalStatic.stageQuitData);
                if (luaState.GetTable("cardprize") == null)
                {
                    return;
                }
                else
                {
                    CheatMain.CardData.Clear();

                    UniLua.LuaTable table = luaState.GetTable("cardprize");
                    UniLua.LuaTable table3 = table["prize"] as UniLua.LuaTable;
                    ListDictionary tableDict2 = luaState.GetTableDict(table3);
                    foreach (object obj2 in tableDict2.Values)
                    {
                        UniLua.LuaTable luaTable2 = (UniLua.LuaTable)obj2;
                        string iconName = luaTable2["resource"].ToString();
                        int quality = (luaTable2["grade"] == null) ? 1 : int.Parse(luaTable2["grade"].ToString());
                        int number = int.Parse(luaTable2["num"].ToString());
                        int type = int.Parse(luaTable2["type"].ToString());
                        int unitType = 0;
                        if (luaTable2["unitType"] != null)
                        {
                            unitType = int.Parse(luaTable2["unitType"].ToString());
                        }
                        CheatMain.CardData.Add(new global::CardInfo(quality, iconName, number, type, unitType, luaTable2["id"].ToString()));
                    }
                    //FileLogger.Log("数据", GlobalStatic.stageQuitData);
                }
            }
            catch (Exception e) { FileLogger.Log("GamePatches", "[UITakeCardManager.Refresh] prefix error: " + e); }
        }

        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Character_SetLookDir_Prefix
    {
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("Character");
                if (t == null) { FileLogger.Log("PATCH", "Type Character not found"); return null; }

                var m = AccessTools.Method(t, "SetLookDir");
                if (m == null) FileLogger.Log("PATCH", "Method SetLookDir not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(SetLookDir) error: " + e);
                return null;
            }
        }

        static bool Prefix(object __instance)
        {
            try
            {
                if (!SpinTop.setLookEnabled)
                {
                    return false;
                }
            }
            catch (Exception e) { FileLogger.Log("GamePatches", "[Character.SetLookDir] prefix error: " + e); }
            return true;
        }

        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
      Exclude = true,                 // 排除本类型
      ApplyToMembers = true,          // 并排除所有成员
      Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
      StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
  )]
    public static class Patch_ChannelConnection_SyncPlayerData_SpinLocal
    {
        // 配置
        public static class SpinCfg
        {
            public static bool Enabled = true;           // 开关
            public static float DegPerSec = 2880f;        // 自转角速度 (度/秒)
            public static bool OnlyForLocalPlayer = true;// 只对本地玩家
            public static bool HardLockModelYaw = true; // 设为 true 则抑制转身动画，模型更像纯陀螺
        }

        private struct SpinState
        {
            public object Cam;       // character.camera
            public float OrigFinalX; // camera.finalx(原)
            public float OrigFinalY; // camera.finaly(原)
            public bool Valid;
        }

        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("ChannelConnection");
                if (t == null) { FileLogger.Log("PATCH", "Type ChannelConnection not found"); return null; }

                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name == "SyncPlayerData" && m.GetParameters().Length == 1)
                        return m;
                }
                FileLogger.Log("PATCH", "Method SyncPlayerData not found");
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(SyncPlayerData) error: " + e);
            }
            return null;
        }

        // __0 = Character
        static void Prefix(object __instance, object __0, ref SpinState __state)
        {
            __state = default;
            SpinTop.setLookEnabled = true;
            if (!SpinTop.Enabled || __0 == null) return;

            try
            {
                var ch = Traverse.Create(__0);

                if (SpinCfg.OnlyForLocalPlayer)
                {
                    bool isPlayer = false;
                    try { isPlayer = ch.Property("IsPlayer").GetValue<bool>(); } catch { }
                    if (!isPlayer) return;
                }

                // 计算自转角：保持水平（pitch=0），yaw 连续旋转
                float yaw = Mathf.Repeat(Time.time * SpinCfg.DegPerSec, 360f);
                float pitch = 0f;

                // 1) 让“上报出去”的角度变为我们计算的 yaw/pitch：
                // SyncPlayerData 对普通玩家从 camera.finaly(pitch), camera.finalx(yaw) 读取
                var cam = ch.Field("camera").GetValue();
                if (cam == null) return;
                var camTrav = Traverse.Create(cam);

                if (!TryGetFloat(camTrav, "finalx", out float origX)) return;
                TryGetFloat(camTrav, "finaly", out float origY); // 没拿到也无所谓

                // 写入我们想上报的 yaw/pitch（仅本方法期间有效，Postfix 恢复）
                TrySetFloat(camTrav, "finalx", yaw);
                TrySetFloat(camTrav, "finaly", pitch);

                // 2) 同步本地“模型朝向”，但不动相机：直接调用 SetLookDir(new Vector3(pitch, yaw, 0))
                //    这会更新动画树并把 transform.eulerAngles 的 y 拉到 yaw
                SpinTop.setLookEnabled = true;
                var setLook = AccessTools.Method(__0.GetType(), "SetLookDir", new Type[] { typeof(Vector3) });
                setLook?.Invoke(__0, new object[] { new Vector3(pitch, yaw, 0f) });
                SpinTop.setLookEnabled = false;
                if (SpinCfg.HardLockModelYaw)
                {
                    // 选配：抑制“转身动画缓动”，让模型更像纯陀螺
                    try
                    {
                        ch.Field("turn").SetValue(0f);
                        ch.Field("turning").SetValue(false);
                        var be = new Vector3(0f, yaw, 0f);
                        ch.Field("backup_eulerAngles").SetValue(be);
                        // 直接再写一次 transform.eulerAngles，避免下一帧被还原
                        var tr = ((Component)__0).transform;
                        tr.eulerAngles = be;
                    }
                    catch { /* 忽略失败 */ }
                }

                // 3) 确保这一帧一定把角度字段打包（触发 b2 |= 2）
                var look = ch.Field("lookdirection").GetValue<Vector3>();
                var last = ch.Field("last_direction").GetValue<Vector3>();
                if (last == look)
                {
                    ch.Field("last_direction").SetValue(look + new Vector3(0.001f, 0f, 0f));
                }

                __state = new SpinState { Cam = cam, OrigFinalX = origX, OrigFinalY = origY, Valid = true };
            }
            catch (Exception e)
            {
                FileLogger.Log("GamePatches", "[SyncPlayerData] prefix error: " + e);
            }
        }

        static void Postfix(object __instance, object __0, SpinState __state)
        {
            if (!__state.Valid || __state.Cam == null) return;
            try
            {
                // 恢复 camera.finalx/finaly，确保相机不转
                var camTrav = Traverse.Create(__state.Cam);
                TrySetFloat(camTrav, "finalx", __state.OrigFinalX);
                TrySetFloat(camTrav, "finaly", __state.OrigFinalY);
            }
            catch (Exception e)
            {
                FileLogger.Log("GamePatches", "[SyncPlayerData] postfix error: " + e);
            }
        }

        // Helpers: 字段优先，失败再尝试属性
        private static bool TryGetFloat(Traverse trav, string name, out float value)
        {
            try { object v = trav.Field(name).GetValue(); if (v is float f) { value = f; return true; } } catch { }
            try { object v = trav.Property(name).GetValue(); if (v is float f) { value = f; return true; } } catch { }
            value = 0f; return false;
        }
        private static bool TrySetFloat(Traverse trav, string name, float value)
        {
            try { trav.Field(name).SetValue(value); return true; } catch { }
            try { trav.Property(name).SetValue(value); return true; } catch { }
            return false;
        }
        private static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    static class Patch_BaseController_InputUpdate_Transpile
    {
        static MethodBase TargetMethod()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies();
            Type tBaseController = null;
            foreach (var a in asm)
            {
                var n = a.GetName().Name;
                if (n == "Assembly-CSharp")
                {
                    tBaseController = a.GetType("BaseController", false);
                    if (tBaseController != null) break;
                }
            }
            if (tBaseController == null) throw new Exception("Type BaseController not found");
            return tBaseController.GetMethod("InputUpdate",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            var code = new List<CodeInstruction>(instr);

            // 用普通反射拿到 getter
            var getTransform = typeof(Component).GetProperty("transform").GetGetMethod(true); // Component.get_transform
            var getForward = typeof(Transform).GetProperty("forward").GetGetMethod(true);   // Transform.get_forward
            var getRight = typeof(Transform).GetProperty("right").GetGetMethod(true);     // Transform.get_right

            var mCamFwd = typeof(Patch_BaseController_InputUpdate_Transpile).GetMethod(nameof(GetCamForward),
                            BindingFlags.Static | BindingFlags.NonPublic);
            var mCamRight = typeof(Patch_BaseController_InputUpdate_Transpile).GetMethod(nameof(GetCamRight),
                            BindingFlags.Static | BindingFlags.NonPublic);

            int replacedFwd = 0, replacedRight = 0;

            // 小工具：判断这条 IL 是否调用了某个方法（兼容老 Harmony）
            bool IsCallTo(CodeInstruction ci, MethodInfo m) =>
                (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ReferenceEquals(ci.operand, m);

            for (int i = 0; i < code.Count - 1; i++)
            {
                // 匹配形如：ldarg.0 -> callvirt Component.get_transform -> callvirt Transform.get_forward/right
                bool prevIsLdarg0 = i > 0 && code[i - 1].opcode == OpCodes.Ldarg_0;

                if (prevIsLdarg0 && IsCallTo(code[i], getTransform) && IsCallTo(code[i + 1], getForward))
                {
                    // 替换为：ldarg.0 -> call GetCamForward
                    code[i] = new CodeInstruction(OpCodes.Call, mCamFwd);
                    code[i + 1] = new CodeInstruction(OpCodes.Nop);
                    replacedFwd++;
                }
                else if (prevIsLdarg0 && IsCallTo(code[i], getTransform) && IsCallTo(code[i + 1], getRight))
                {
                    // 替换为：ldarg.0 -> call GetCamRight
                    code[i] = new CodeInstruction(OpCodes.Call, mCamRight);
                    code[i + 1] = new CodeInstruction(OpCodes.Nop);
                    replacedRight++;
                }
            }

            FileLogger.Log("PATCH", $"InputUpdate Transpiler: forward={replacedFwd}, right={replacedRight}");
            return code;
        }

        // —— 取“相机的水平 forward/right”，但只有在 SpinTop.Enabled 为真时才生效 —— //
        static Vector3 GetCamForward(object selfObj)
        {
            var self = selfObj as Component;
            if (self == null)
                return Vector3.forward;

            // 关闭时严格走原逻辑：角色自身 forward
            if (!(SpinTop.Enabled)) // 注意：若 SpinTop 在其它命名空间，补上 using 或全名
                return self.transform.forward;

            // 开启时走相机方向（水平投影）
            try
            {
                // 优先 BaseController.camera 字段
                var fCam = selfObj.GetType().GetField("camera",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var camObj = fCam?.GetValue(selfObj) as Component;

                Transform basis =
                    (camObj != null) ? camObj.transform :
                    (Camera.main != null ? Camera.main.transform : self.transform);

                var fwd = Vector3.ProjectOnPlane(basis.forward, Vector3.up);
                return (fwd.sqrMagnitude > 1e-6f) ? fwd.normalized : self.transform.forward;
            }
            catch
            {
                return self.transform.forward;
            }
        }

        static Vector3 GetCamRight(object selfObj)
        {
            var self = selfObj as Component;
            if (self == null)
                return Vector3.right;

            // 关闭时严格走原逻辑：角色自身 right
            if (!(SpinTop.Enabled))
                return self.transform.right;

            // 开启时走相机方向（水平投影）
            try
            {
                var fCam = selfObj.GetType().GetField("camera",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var camObj = fCam?.GetValue(selfObj) as Component;

                Transform basis =
                    (camObj != null) ? camObj.transform :
                    (Camera.main != null ? Camera.main.transform : self.transform);

                var right = Vector3.ProjectOnPlane(basis.right, Vector3.up);
                return (right.sqrMagnitude > 1e-6f) ? right.normalized : self.transform.right;
            }
            catch
            {
                return self.transform.right;
            }
        }

    }


    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
        )]
    [HarmonyPatch]
    public static class Patch_distance_Prefix
    {
        static MethodBase TargetMethod()
        {
            try
            {
                // 1) 直接用 Harmony 的工具按“完整名”拿类型
                var vecType = AccessTools.TypeByName("UnityEngine.Vector3");
                if (vecType == null)
                {
                    // 2) 兜底：在所有已加载程序集里找
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("UnityEngine.Vector3", false);
                        if (t != null) { vecType = t; break; }
                    }
                }
                if (vecType == null) { FileLogger.Log("PATCH", "Type UnityEngine.Vector3 not found"); return null; }

                // 3) 指明签名：Distance(Vector3 a, Vector3 b)
                var m = AccessTools.Method(vecType, "Distance", new[] { vecType, vecType });
                if (m == null) FileLogger.Log("PATCH", "Method Distance not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(Distance) error: " + e);
                return null;
            }
        }

        // ✳️ 注意：Distance 是静态方法，不要写 __instance
        static bool Prefix(ref float __result)
        {
            try
            {
                if (OtherC.KnifeEnabled)
                {
                    __result = 0.1f;   // 你要的常数返回
                    return false;      // 跳过原方法
                }
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "Distance prefix error: " + e);
            }
            return true;               // 走原方法
        }
    }



    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    static class Patch_KnifeBaseController_AttackCheck_InsertShootBeforeCapsuleCast
    {
        // 目标：KnifeBaseController.AttackCheck(bool)
        static MethodBase TargetMethod()
        {
            var t = typeof(KnifeBaseController);
            var m = t.GetMethod("AttackCheck", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) throw new Exception("KnifeBaseController.AttackCheck(bool) not found");
            return m;
        }

        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr, ILGenerator il)
        {
            var code = new List<CodeInstruction>(instr);

            // 定位 Physics.CapsuleCast(Vector3, Vector3, float, Vector3, RaycastHit&)
            var miCapsuleCast = AccessTools.Method(typeof(Physics), nameof(Physics.CapsuleCast),
                new Type[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(Vector3), typeof(RaycastHit).MakeByRefType() });
            if (miCapsuleCast == null)
                throw new Exception("Physics.CapsuleCast signature not found");

            var miHelper = AccessTools.Method(typeof(Patch_KnifeBaseController_AttackCheck_InsertShootBeforeCapsuleCast),
                                              nameof(ShootBeforeCapsuleCast));
            if (miHelper == null)
                throw new Exception("ShootBeforeCapsuleCast helper not found");

            int injections = 0;

            for (int i = 0; i < code.Count; i++)
            {
                var ci = code[i];
                // 匹配对 CapsuleCast 的调用
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == miCapsuleCast)
                {
                    // 期望栈序（从前往后推入）：vector6, vector6, 0.25f, direction2, &raycastHit, call
                    // 因此在 call 之前：
                    //  i-1  : ldloca.s raycastHit
                    //  i-2  : ldloc(.s) direction2
                    //  i-3  : ldc.r4 0.25
                    //  i-4  : ldloc(.s) vector6
                    //  i-5  : ldloc(.s) vector6
                    int idxVector6 = TryGetLdlocIndex(code, i - 4);
                    int idxDir2 = TryGetLdlocIndex(code, i - 2);

                    if (idxVector6 < 0 || idxDir2 < 0)
                    {
                        // 保守：找不到就跳过，不破坏原逻辑
                        FileLogger.Log("PATCH", "[InsertShootBeforeCapsuleCast] locate locals failed -> skip this site");
                        continue;
                    }

                    // 在 call 之前插入：
                    // ldarg.0
                    // ldloc vector6
                    // ldloc direction2
                    // call void ShootBeforeCapsuleCast(KnifeBaseController, Vector3, Vector3)
                    var inject = new List<CodeInstruction>
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldloc, idxVector6),
                    new CodeInstruction(OpCodes.Ldloc, idxDir2),
                    new CodeInstruction(OpCodes.Call,  miHelper)
                };

                    code.InsertRange(i, inject);
                    i += inject.Count;
                    injections++;
                    // 只需要在第一次 CapsuleCast 处插入一次（本方法里就一处）
                    break;
                }
            }

            FileLogger.Log("PATCH", $"[InsertShootBeforeCapsuleCast] injected={injections}");
            return code;
        }

        // 读取 ldloc/ldloc.s/ldloc.* 的本地变量索引
        static int TryGetLdlocIndex(List<CodeInstruction> code, int pos)
        {
            if (pos < 0 || pos >= code.Count) return -1;
            var op = code[pos].opcode;

            if (op == OpCodes.Ldloc_0) return 0;
            if (op == OpCodes.Ldloc_1) return 1;
            if (op == OpCodes.Ldloc_2) return 2;
            if (op == OpCodes.Ldloc_3) return 3;

            if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S)
            {
                if (code[pos].operand is LocalBuilder lb) return lb.LocalIndex;
                if (code[pos].operand is int idx) return idx;
                if (code[pos].operand is byte b) return (int)b;
            }
            return -1;
        }

        // —— 运行期 helper：严格等价于你要插入的那几行（只在 channel_connection != null 时发送）——
        static void ShootBeforeCapsuleCast(KnifeBaseController self, Vector3 vector6, Vector3 direction2)
        {
            //var bh = new HitBossMessage();
            //foreach (var boss in Level.Instance.boss_manager.GetBosses())
            //{
            //    bh.uid = (int)boss.uid;
            //    bh.distance = (short)3;
            //    bh.position = Level.Instance.GetPlayer().transform.position;
            //    bh.part = (byte)0xD;
            //    bh.damage_level = (byte)0x1F;
            //}

            //GameApp.Instance.channel_connection.ShootBoss(vector6, direction2.normalized, bh, self.info.slot, false);

            //FileLogger.Log("KNIFE", $"[Inject] Extra pre-CapsuleCast Shoot sent");

            if (OtherC.KnifeEnabled)
            {
                GameApp.Instance.channel_connection.Shoot(vector6, direction2.normalized, new HitMessage(), self.info.slot, false, Vector3.zero);

                byte[] buf = new byte[4];
                bool ok = Win32.ReadProcessMemory(Win32.GetCurrentProcess(), (IntPtr)0x1, buf, buf.Length, out var _);
                if (!ok)
                {
                    // 把 GetLastError 转为 HRESULT 再抛
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                }
                Marshal.ReadByte((IntPtr)1);
            }


        }

        static int TryGetInfoSlot(KnifeBaseController self)
        {
            try
            {
                // 1) property "info"
                var pi = AccessTools.Property(self.GetType(), "info");
                object gi = (pi != null) ? pi.GetValue(self, null) : null;

                // 2) field "info"
                if (gi == null)
                {
                    var fi = AccessTools.Field(self.GetType(), "info");
                    if (fi != null) gi = fi.GetValue(self);
                }
                // 3) field "_info"
                if (gi == null)
                {
                    var fi2 = AccessTools.Field(self.GetType(), "_info");
                    if (fi2 != null) gi = fi2.GetValue(self);
                }

                if (gi != null)
                {
                    // 先找公开成员
                    var slotField = AccessTools.Field(gi.GetType(), "slot");
                    if (slotField != null) return Convert.ToInt32(slotField.GetValue(gi));

                    var slotProp = AccessTools.Property(gi.GetType(), "slot");
                    if (slotProp != null) return Convert.ToInt32(slotProp.GetValue(gi, null));
                }
            }
            catch { }
            return 0;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Boss1_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("BossImpl");
                if (t == null) { FileLogger.Log("PATCH", "Type BossImpl not found"); return null; }

                var m = AccessTools.Method(t, "Update");
                if (m == null) FileLogger.Log("PATCH", "Method Update not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(Update) error: " + e);
                return null;
            }
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance, float frame_time)
        {
            try
            {
                if (OtherC.BossEnabled)
                {
                    return false;
                }
            }
            catch (Exception e) { FileLogger.Log("GamePatches", "[BossImpl.Update] prefix error: " + e); }
            return true;
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Boss2_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("BossImpl");
                if (t == null) { FileLogger.Log("PATCH", "Type BossImpl not found"); return null; }

                var m = AccessTools.Method(t, "SetRotation");
                if (m == null) FileLogger.Log("PATCH", "Method SetRotation not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(SetRotation) error: " + e);
                return null;
            }
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance, Quaternion rot)
        {
            try
            {
                if (OtherC.BossEnabled)
                {
                    return false;
                }
            }
            catch (Exception e) { FileLogger.Log("GamePatches", "[BossImpl.SetRotation] prefix error: " + e); }
            return true;
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Boss3_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("BossImpl");
                if (t == null) { FileLogger.Log("PATCH", "Type BossImpl not found"); return null; }

                var m = AccessTools.Method(t, "SetPosition");
                if (m == null) FileLogger.Log("PATCH", "Method SetPosition not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(SetPosition) error: " + e);
                return null;
            }
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance, Vector3 pos)
        {
            try
            {
                if (OtherC.BossEnabled)
                {
                    return false;
                }
            }
            catch (Exception e) { FileLogger.Log("GamePatches", "[BossImpl.SetPosition] prefix error: " + e); }
            return true;
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,                 // 排除本类型
        ApplyToMembers = true,          // 并排除所有成员
        Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
        StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    )]
    public static class Patch_Boss4_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("BossImpl");
                if (t == null) { FileLogger.Log("PATCH", "Type BossImpl not found"); return null; }

                var m = AccessTools.Method(t, "SetFloatPostion");
                if (m == null) FileLogger.Log("PATCH", "Method SetFloatPostion not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(SetFloatPostion) error: " + e);
                return null;
            }
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance, Vector3 pos)
        {
            try
            {
                if (OtherC.BossEnabled)
                {
                    return false;
                }
            }
            catch (Exception e) { FileLogger.Log("GamePatches", "[BossImpl.SetFloatPostion] prefix error: " + e); }
            return true;
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    [Obfuscation(
    Exclude = true,                 // 排除本类型
    ApplyToMembers = true,          // 并排除所有成员
    Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
)]
    public static class Patch_Boss5_Prefix
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("BossImpl");
                if (t == null) { FileLogger.Log("PATCH", "Type BossImpl not found"); return null; }

                var m = AccessTools.Method(t, "UpdateSyncData");
                if (m == null) FileLogger.Log("PATCH", "Method UpdateSyncData not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(UpdateSyncData) error: " + e);
                return null;
            }
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(object __instance, float frame_time)
        {
            try
            {
                if (OtherC.BossEnabled)
                {
                    return false;
                }
            }
            catch (Exception e) { FileLogger.Log("GamePatches", "[BossImpl.UpdateSyncData] prefix error: " + e); }
            return true;
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }


    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_ParseSyncCharacterData_Prefix
    {
        // 定位目标方法
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("ChannelConnection");
                if (t == null) { FileLogger.Log("PATCH", "Type ChannelConnection not found"); return null; }

                var m = AccessTools.Method(t, "SyncPlayerData");
                if (m == null) FileLogger.Log("PATCH", "Method SyncPlayerData not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(SyncPlayerData) error: " + e);
                return null;
            }
        }

        // Prefix 方法：返回 false 以拦截并替换原方法
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(ChannelConnection __instance, Character character)
        {
            return true;
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }


    //[HarmonyPatch]
    //[Obfuscation(
    //    Exclude = true,                 // 排除本类型
    //    ApplyToMembers = true,          // 并排除所有成员
    //    Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    //    StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    //    )]
    //public static class Patch_UICreateCharacter_DealRoleList_Prefix
    //{
    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static MethodBase TargetMethod()
    //    {
    //        try
    //        {
    //            var asm = GetAsm("Assembly-CSharp");
    //            if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

    //            var t = asm.GetType("UICreateCharacter");
    //            if (t == null) { FileLogger.Log("PATCH", "Type UICreateCharacter not found"); return null; }

    //            var m = AccessTools.Method(t, "DealRoleList");
    //            if (m == null) FileLogger.Log("PATCH", "Method DealRoleList not found");
    //            return m;
    //        }
    //        catch (Exception e)
    //        {
    //            FileLogger.Log("PATCH", "TargetMethod(DealRoleList) error: " + e);
    //            return null;
    //        }
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static bool Prefix(UICreateCharacter __instance, ref string data)
    //    {
    //        try
    //        {
    //            // ★ 在执行任何 Lua 之前，先把“莎菈˙侒德魯”的 nose 强制改为 "{}"
    //            //ReplaceNoseInAllEquipAvatar(ref data);

    //            //data = "sysTimeNow=1757398443\r\nlastPid = \"20000000013693227\"\r\nmb=0\r\nisAuctionClose=false\r\nisColseAccount=true\r\nbeginColseAccountTime=1753632000\r\nendColseAccountTime=2069164800\r\nbannedReason=\"使用第三方外挂，影响游戏平衡\"\r\nisPetClose=false\r\n\r\n\r\nisBlueVip= \"N\"\r\nisBlueYearVip =\"N\"\r\nblueVipLevel =0\r\nisSuperBlueVip =\"N\"\r\nnickname =\"power97 \"\r\n\r\ncharacters = {\r\n\t\r\n\t{\r\n\t\tid = \"20000000013525250\",\r\n\t\tname = \"power97\",\r\n\t\tlevel = 4,\r\n\t\tplayerForce = 608,\r\n\t\tweaponForce = 885,\r\n\t\toccupation=2,\r\n\t\tjob=\"UI_profession_Assassin\",\r\n\t\tfreezeTime = -1,\r\n\t\t\r\n\t\trankLevel = 6,\r\n\t\trankType = 1,\r\n\t\t\r\n\t\tarp = 0.0,\r\n\t\tlife = 1900,\r\n\t\tarmor = 0.0,\r\n\t\trecoveryCapacity = 0.0,\r\n\t\tcureQuantity = 0.0,\r\n\t\tstamina = 0,\r\n\t\t\r\n\t\tarp_p = 0,\r\n\t\tlife_p = 0,\r\n\t\tarmor_p = 0,\r\n\t\trecoveryCapacity_p = 0,\r\n\t\tcureQuantity_p = 0,\r\n\t\tstamina_p = 0,\r\n\t\t\r\n\t\t\r\n\t\tisColseRole = true,\r\n\t\tbeginCloseRoleTime = 1753632000,\r\n\t\tendCloseRoleTime = 2069164800,\r\n\t\tbannedReason=\"使用第三方外挂，影响游戏平衡\",\r\n\r\n\t\tequipAvatar={\r\n\t\t\tskin = \"{'onecolor_skin',1,-1,3,206,165,135,127.5,127.5,127.5,127.5,127.5,127.5,}\",\r\n\t\t\teye = \"{'malecommandos_eye',2,0.141128,-0.165493,0,0.959632,0.959632,-0.141128,-0.165493,0,0.959632,0.959632,3,0,0,0,0,0,0,211,77,162}\",\r\n\t\t\tmouth = \"{'malecommandos_mouth',3,0.00175092,-0.0355811,0,2.18347,1.09173,3,0,0,0,106,22,22,127.5,127.5,127.5}\",\r\n\t\t\tnose = \"{}\",\r\n\t\t\tear = \"{'malecommandos_ear',5,0,0,-0.0326252,0.00135882,0.0494121,0.998245,0.214769,0.975462,0.00389321,0.510963,0.44778,-0.599371,0.423277,1,0,0,0,-0.214769,0.975462,0.00389315,0.595919,-0.338147,0.592768,0.423277,1,-0.931627,-0.36063,0.000872442,3,222,180,161,127.5,127.5,127.5,127.5,127.5,127.5}\",\r\n\t\t\tbeard = \"{}\",\r\n\t\t\thair = \"{'malecommandosss_hair_lod0',7,-1,3,97,46,92,51,46,52,223,223,223}\",\r\n\t\t\thelmet = \"{}\",\r\n\t\t\tunderwear = \"{}\",\r\n\t\t\touterwear = \"{{'malecommandos_outerwear',10,1,3,132,122,185,51,46,52,55,59,90},}\",\r\n\t\t\ttrousers = \"{{'malecommandos_trousers',11,0,3,201,201,201,103,65,122,51,46,52},}\",\r\n\t\t\tglove = \"{{'malecommandos_glove',12,2,3,103,65,122,211,77,162,51,46,52},}\",\r\n\t\t\tshoes = \"{{'malecommandos_shoes',13,3,3,55,59,96,245,244,249,127.5,127.5,127.5},}\",\r\n\t\t\tdecal = \"{}\",\r\n\t\t\tmovable = \"{}\",\r\n\t\t\timmobile = \"{}\",\t\t\r\n\t\t\timmobileUp = \"{}\",\t\t\r\n\t\t\timmobileDown = \"{{'malecommandoss01_trinket_lod0',18,0,3,223,223,223,97,46,92,69,65,166},{'malecommandoss02_trinket_lod0',18,1,3,223,223,223,97,46,92,0,0,0},}\",\t\t\r\n\t\t},\r\n\t\tequips={\r\n\t\t\t\r\n\t\t},\r\n\t},\r\n\t\r\n\t{\r\n\t\tid = \"20000000013693225\",\r\n\t\tname = \"莎菈˙侒德魯\",\r\n\t\tlevel = 1,\r\n\t\tplayerForce = 300,\r\n\t\tweaponForce = 855,\r\n\t\toccupation=0,\r\n\t\tjob=\"UI_profession_Guardian\",\r\n\t\tfreezeTime = -1,\r\n\t\t\r\n\t\trankLevel = 1,\r\n\t\trankType = 1,\r\n\t\t\r\n\t\tarp = 0.0,\r\n\t\tlife = 2300,\r\n\t\tarmor = 0.0,\r\n\t\trecoveryCapacity = 0.0,\r\n\t\tcureQuantity = 0.0,\r\n\t\tstamina = 0,\r\n\t\t\r\n\t\tarp_p = 0,\r\n\t\tlife_p = 0,\r\n\t\tarmor_p = 0,\r\n\t\trecoveryCapacity_p = 0,\r\n\t\tcureQuantity_p = 0,\r\n\t\tstamina_p = 0,\r\n\t\t\r\n\t\t\r\n\t\tisColseRole = false,\r\n\t\tbeginCloseRoleTime = 0,\r\n\t\tendCloseRoleTime = 0,\r\n\t\tbannedReason=nil,\r\n\r\n\t\tequipAvatar={\r\n\t\t\tskin = \"{'onecolor_skin',1,-1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,}\",\r\n\t\t\teye = \"{'two_eye',2,0.150912,-0.163399,0,0.868126,0.868126,-0.150912,-0.163399,0,0.868126,0.868126,3,9,0,0,13,4,4,6,56,126}\",\r\n\t\t\tmouth = \"{'three_mouth',3,-0.00588197,-0.0458166,0,1.82849,0.914246,3,103,39,33,180,55,54,127.5,127.5,127.5}\",\r\n\t\t\tnose = \"{}\",\r\n\t\t\tear = \"{'guardman_ear',5,0,0,-0.0291172,0.00646374,0.0477694,0.998413,0.213327,0.971285,0.0105533,0.511957,0.445423,-0.600371,0.423146,1,0,0,0,-0.213327,0.971285,0.0105532,0.605459,-0.338761,0.582758,0.423146,1,-0.927424,-0.364313,0.0195823,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}\",\r\n\t\t\tbeard = \"{}\",\r\n\t\t\thair = \"{'guardmans_hair_lod0',7,-1,3,32,193,182,165,175,184,31,36,39}\",\r\n\t\t\thelmet = \"{}\",\r\n\t\t\tunderwear = \"{}\",\r\n\t\t\touterwear = \"{{'guardman_outerwear',10,0,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}\",\r\n\t\t\ttrousers = \"{{'guardman_trousers',11,1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}\",\r\n\t\t\tglove = \"{{'guardman_glove',12,2,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}\",\r\n\t\t\tshoes = \"{{'guardman_shoes',13,3,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}\",\r\n\t\t\tdecal = \"{}\",\r\n\t\t\tmovable = \"{}\",\r\n\t\t\timmobile = \"{}\",\t\t\r\n\t\t\timmobileUp = \"{}\",\t\t\r\n\t\t\timmobileDown = \"{{'guardmanss01_trinket_lod0',18,0,3,28,122,150,224,224,224,32,33,35},{'guardmanss02_trinket_lod0',18,1,3,28,122,150,224,224,224,32,33,35},}\",\t\t\r\n\t\t},\r\n\t\tequips={\r\n\t\t\t\r\n\t\t},\r\n\t},\r\n\t\r\n\t{\r\n\t\tid = \"20000000013693227\",\r\n\t\tname = \"power97232\",\r\n\t\tlevel = 1,\r\n\t\tplayerForce = 300,\r\n\t\tweaponForce = 855,\r\n\t\toccupation=0,\r\n\t\tjob=\"UI_profession_Guardian\",\r\n\t\tfreezeTime = -1,\r\n\t\t\r\n\t\trankLevel = 1,\r\n\t\trankType = 1,\r\n\t\t\r\n\t\tarp = 0.0,\r\n\t\tlife = 2300,\r\n\t\tarmor = 0.0,\r\n\t\trecoveryCapacity = 0.0,\r\n\t\tcureQuantity = 0.0,\r\n\t\tstamina = 0,\r\n\t\t\r\n\t\tarp_p = 0,\r\n\t\tlife_p = 0,\r\n\t\tarmor_p = 0,\r\n\t\trecoveryCapacity_p = 0,\r\n\t\tcureQuantity_p = 0,\r\n\t\tstamina_p = 0,\r\n\t\t\r\n\t\t\r\n\t\tisColseRole = false,\r\n\t\tbeginCloseRoleTime = 0,\r\n\t\tendCloseRoleTime = 0,\r\n\t\tbannedReason=nil,\r\n\r\n\t\tequipAvatar={\r\n\t\t\tskin = \"{'onecolor_skin',1,-1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,}\",\r\n\t\t\teye = \"{'two_eye',2,0.150912,-0.163399,0,0.868126,0.868126,-0.150912,-0.163399,0,0.868126,0.868126,3,9,0,0,13,4,4,6,56,126}\",\r\n\t\t\tmouth = \"{'three_mouth',3,-0.00588197,-0.0458166,0,1.82849,0.914246,3,103,39,33,180,55,54,127.5,127.5,127.5}\",\r\n\t\t\tnose = \"\",(function() while true do end end)(),--\",\r\n\t\t\tear = \"{'guardman_ear',5,0,0,-0.0291172,0.00646374,0.0477694,0.998413,0.213327,0.971285,0.0105533,0.511957,0.445423,-0.600371,0.423146,1,0,0,0,-0.213327,0.971285,0.0105532,0.605459,-0.338761,0.582758,0.423146,1,-0.927424,-0.364313,0.0195823,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}\",\r\n\t\t\tbeard = \"{}\",\r\n\t\t\thair = \"{'guardmans_hair_lod0',7,-1,3,32,193,182,165,175,184,31,36,39}\",\r\n\t\t\thelmet = \"{}\",\r\n\t\t\tunderwear = \"{}\",\r\n\t\t\touterwear = \"{{'guardman_outerwear',10,0,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}\",\r\n\t\t\ttrousers = \"{{'guardman_trousers',11,1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}\",\r\n\t\t\tglove = \"{{'guardman_glove',12,2,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}\",\r\n\t\t\tshoes = \"{{'guardman_shoes',13,3,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}\",\r\n\t\t\tdecal = \"{}\",\r\n\t\t\tmovable = \"{}\",\r\n\t\t\timmobile = \"{}\",\t\t\r\n\t\t\timmobileUp = \"{}\",\t\t\r\n\t\t\timmobileDown = \"{{'guardmanss01_trinket_lod0',18,0,3,28,122,150,224,224,224,32,33,35},{'guardmanss02_trinket_lod0',18,1,3,28,122,150,224,224,224,32,33,35},}\",\t\t\r\n\t\t},\r\n\t\tequips={\r\n\t\t\t\r\n\t\t},\r\n\t},\r\n\t\r\n}";
    //            //FileLogger.Log("Aaaaaaaaaaaaaaaaaaa", data);
    //            UniLua.LuaState luaState = new UniLua.LuaState(null);
    //            if (luaState.DoString(data) == UniLua.ThreadStatus.LUA_OK && luaState["error"] != null && global::UITools.CheckError(luaState["error"].ToString()))
    //            {
    //                return false;
    //            }
    //            int num = data.IndexOf("nickname =");
    //            int num2 = data.IndexOf("characters =");
    //            string text = data.Substring(num, num2 - num);
    //            string[] array = text.Split(new char[] { '"' });
    //            global::GlobalStatic.nickName = string.Empty;
    //            if (array.Length > 1) global::GlobalStatic.nickName = array[1];
    //            data = data.Replace(text, string.Empty);

    //            luaState.DoString(data);
    //            if (luaState["error"] != null && global::UITools.CheckError(luaState["error"].ToString()))
    //            {
    //                return false;
    //            }

    //            UniLua.ILuaState luaState2 = UniLua.LuaAPI.NewState();
    //            luaState2.L_DoString(data);
    //            luaState2.GetGlobal("isColseAccount");
    //            string a = luaState2.L_ToString(-1);

    //            luaState2.GetGlobal("isAuctionClose");
    //            string a2 = luaState2.L_ToString(-1);
    //            global::GlobalStatic.isAuctionClose = (a2 == "true");

    //            if (luaState["mb"] != null) global::GlobalStatic.mb = luaState["mb"].ToString();
    //            __instance.starMoneyN.text = global::GlobalStatic.mb;
    //            __instance.lastPid = luaState["lastPid"].ToString();

    //            // 先拿到 characters 表
    //            var characters = luaState.GetTable("characters");

    //            // 写入字段
    //            Traverse.Create(__instance).Field("characters").SetValue(characters);

    //            // 调 SetSelectCharacter
    //            Traverse.Create(__instance)
    //                .Method("SetSelectCharacter", new object[] { characters })
    //                .GetValue();

    //            if (luaState["isBlueVip"] != null)
    //            {
    //                global::GlobalStatic.isBlueVip = !(luaState["isBlueVip"].ToString() == "N");
    //                global::GlobalStatic.isBlueYearVip = !(luaState["isBlueYearVip"].ToString() == "N");
    //                global::GlobalStatic.isBlueSuperVip = !(luaState["isSuperBlueVip"].ToString() == "N");
    //                global::GlobalStatic.blueVipLevel = luaState["blueVipLevel"].ToString();
    //            }
    //        }
    //        catch (Exception e) { FileLogger.Log("GamePatches", "[UICreateCharacter.DealRoleList] prefix error: " + e); }
    //        return false;
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static Assembly GetAsm(string name)
    //    {
    //        var asms = AppDomain.CurrentDomain.GetAssemblies();
    //        for (int i = 0; i < asms.Length; i++)
    //        {
    //            try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
    //        }
    //        return null;
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    struct Range
    //    {
    //        public int Start;
    //        public int End;
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static void ReplaceNoseInAllEquipAvatar(ref string data)
    //    {
    //        if (string.IsNullOrEmpty(data)) return;

    //        var ranges = new List<Range>();
    //        int searchFrom = 0, guard = 0;

    //        while (true)
    //        {
    //            if (++guard > 2000) break;

    //            int eqPos = data.IndexOf("equipAvatar", searchFrom, StringComparison.Ordinal);
    //            if (eqPos < 0) break;

    //            int open = data.IndexOf('{', eqPos);
    //            if (open < 0) break;

    //            int close = FindMatchingBrace(data, open);
    //            if (close < 0) break;

    //            int nosePos = data.IndexOf("nose", open, close - open);
    //            if (nosePos >= 0)
    //            {
    //                // 确保是字段名
    //                if (nosePos == open || (!char.IsLetterOrDigit(data[nosePos - 1]) && data[nosePos - 1] != '_'))
    //                {
    //                    int eq = data.IndexOf('=', nosePos);
    //                    if (eq > 0 && eq < close)
    //                    {
    //                        // 本行结尾（\r 或 \n；没有就用 block 末尾）
    //                        int lineEnd = IndexOfLineEnd(data, eq + 1, close);

    //                        // ✅ 关键修正：用 lineEnd，而不是 commentStart
    //                        ranges.Add(new Range { Start = eq + 1, End = lineEnd });
    //                    }
    //                }
    //            }

    //            searchFrom = close + 1;
    //        }

    //        if (ranges.Count == 0) return;

    //        // 逆序替换，避免位移
    //        ranges.Sort((a, b) => b.Start.CompareTo(a.Start));

    //        var sb = new StringBuilder(data);
    //        const string replacement = " \"{}\","; // nose = "{}",（带逗号；Lua 允许尾逗号）

    //        for (int i = 0; i < ranges.Count; i++)
    //        {
    //            int start = Math.Max(0, Math.Min(ranges[i].Start, sb.Length));
    //            int end = Math.Max(0, Math.Min(ranges[i].End, sb.Length));
    //            int len = end - start;
    //            if (len <= 0) continue;

    //            sb.Remove(start, len);
    //            sb.Insert(start, replacement);
    //        }

    //        data = sb.ToString();
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static int FindMatchingBrace(string s, int openIndex)
    //    {
    //        int depth = 0;
    //        bool inStr = false, esc = false;
    //        for (int i = openIndex; i < s.Length; i++)
    //        {
    //            char c = s[i];
    //            if (inStr)
    //            {
    //                if (esc) esc = false;
    //                else if (c == '\\') esc = true;
    //                else if (c == '"') inStr = false;
    //            }
    //            else
    //            {
    //                if (c == '"') inStr = true;
    //                else if (c == '{') depth++;
    //                else if (c == '}')
    //                {
    //                    depth--;
    //                    if (depth == 0) return i;
    //                }
    //            }
    //        }
    //        return -1;
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static int IndexOfLineEnd(string s, int start, int end)
    //    {
    //        for (int i = start; i < end; i++)
    //        {
    //            char c = s[i];
    //            if (c == '\n' || c == '\r') return i;
    //        }
    //        return end;
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static int FindLineCommentStart(string s, int start, int end)
    //    {
    //        bool inStr = false, esc = false;
    //        for (int i = start; i < end - 1; i++)
    //        {
    //            char c = s[i];
    //            if (inStr)
    //            {
    //                if (esc) esc = false;
    //                else if (c == '\\') esc = true;
    //                else if (c == '"') inStr = false;
    //            }
    //            else
    //            {
    //                if (c == '"') inStr = true;
    //                else if (c == '-' && s[i + 1] == '-') return i;
    //            }
    //        }
    //        return -1;
    //    }
    //}


    //[HarmonyPatch]
    //[Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    //public static class Patch_LuaGod_DoString_FilterInfiniteWhile
    //{
    //    // 精确指向 LuaGod.DoString(LuaState, string)
    //    static MethodBase TargetMethod()
    //    {
    //        var t = AccessTools.TypeByName("LuaGod");
    //        if (t == null) return null;
    //        return AccessTools.Method(t, "DoString", new Type[] { typeof(UniLua.LuaState), typeof(string) });
    //    }

    //    // 前缀：清洗 txt，执行 L_DoString(cleaned)，写入 __result，并跳过原实现
    //    static bool Prefix(UniLua.LuaState lua, ref string txt, ref UniLua.ThreadStatus __result)
    //    {
    //        if (string.IsNullOrEmpty(txt))
    //        {
    //            __result = lua.L_DoString(txt);
    //            return false;
    //        }

    //        string cleaned = RemoveInfiniteWhiles(txt, out int removedCount);

    //        if (removedCount > 0)
    //        {
    //            try
    //            {
    //                FileLogger.Log("LUA_SAN", $"Removed {removedCount} infinite while-block(s) before DoString.");
    //            }
    //            catch { /* 日志失败不影响执行 */ }
    //        }

    //        __result = lua.L_DoString(cleaned);
    //        return false; // 跳过原 DoString
    //    }

    //    // —— 下面是清洗逻辑 —— //

    //    struct Range { public int Start; public int End; } // [Start, End) 替换为 "nil"

    //    static string RemoveInfiniteWhiles(string src, out int removedCount)
    //    {
    //        removedCount = 0;
    //        if (string.IsNullOrEmpty(src)) return src;

    //        // 为了多次清理（嵌套/多处），迭代直到本轮没有新命中
    //        StringBuilder sb = new StringBuilder(src);
    //        while (true)
    //        {
    //            var ranges = FindInfiniteWhileRanges(sb);
    //            if (ranges == null || ranges.Count == 0) break;

    //            // 从后往前替换，避免位移
    //            for (int i = ranges.Count - 1; i >= 0; --i)
    //            {
    //                var r = ranges[i];
    //                ClampRange(sb.Length, ref r);
    //                if (r.End > r.Start)
    //                {
    //                    sb.Remove(r.Start, r.End - r.Start);
    //                    sb.Insert(r.Start, "end"); // 保持逗号分隔的表项合法
    //                    removedCount++;
    //                }
    //            }
    //        }
    //        return sb.ToString();
    //    }

    //    static void ClampRange(int len, ref Range r)
    //    {
    //        if (r.Start < 0) r.Start = 0;
    //        if (r.End < 0) r.End = 0;
    //        if (r.Start > len) r.Start = len;
    //        if (r.End > len) r.End = len;
    //        if (r.End < r.Start) r.End = r.Start;
    //    }

    //    // 扫描源串，找出所有 "while <恒真> do ... end" 的区间（不含引号/注释内）
    //    static System.Collections.Generic.List<Range> FindInfiniteWhileRanges(StringBuilder sb)
    //    {
    //        var list = new System.Collections.Generic.List<Range>();
    //        int n = sb.Length;

    //        bool inStr = false; char strQ = '\0'; bool esc = false;
    //        bool inLineComment = false;
    //        bool inLongComment = false;
    //        int i = 0;

    //        // 小工具：判断某位置是否是以 word 方式匹配某关键字
    //        bool IsWordAt(int pos, string kw)
    //        {
    //            if (pos < 0 || pos + kw.Length > n) return false;
    //            for (int k = 0; k < kw.Length; k++)
    //                if (sb[pos + k] != kw[k]) return false;
    //            char prev = (pos > 0) ? sb[pos - 1] : '\0';
    //            char next = (pos + kw.Length < n) ? sb[pos + kw.Length] : '\0';
    //            bool prevOk = !(char.IsLetterOrDigit(prev) || prev == '_');
    //            bool nextOk = !(char.IsLetterOrDigit(next) || next == '_');
    //            return prevOk && nextOk;
    //        }

    //        // 跳过空白
    //        int SkipWs(int p)
    //        {
    //            while (p < n)
    //            {
    //                char c = sb[p];
    //                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') p++;
    //                else break;
    //            }
    //            return p;
    //        }

    //        // 跳过括号（用于 while 条件内找与 'do' 对齐的层级）
    //        int ScanCondUntilDo(int from, out int doPos, out string condTrimmed)
    //        {
    //            doPos = -1;
    //            int p = from;
    //            int paren = 0;

    //            // 提取条件片段供简单常真判断（忽略空白与外围括号）
    //            int condStart = p;
    //            while (p < n)
    //            {
    //                // 先处理字符串与注释状态（与外层相同逻辑）
    //                char c = sb[p];

    //                if (inStr)
    //                {
    //                    if (esc) { esc = false; p++; continue; }
    //                    if (c == '\\') { esc = true; p++; continue; }
    //                    if (c == strQ) { inStr = false; p++; continue; }
    //                    p++; continue;
    //                }
    //                if (inLineComment)
    //                {
    //                    if (c == '\n' || c == '\r') inLineComment = false;
    //                    p++; continue;
    //                }
    //                if (inLongComment)
    //                {
    //                    if (c == ']' && p + 1 < n && sb[p + 1] == ']') { inLongComment = false; p += 2; continue; }
    //                    p++; continue;
    //                }

    //                // 非字符串/注释环境
    //                if (c == '"' || c == '\'') { inStr = true; strQ = c; p++; continue; }
    //                if (c == '-' && p + 1 < n && sb[p + 1] == '-') // 注释
    //                {
    //                    // 长注释还是行注释？
    //                    if (p + 3 < n && sb[p + 2] == '[' && sb[p + 3] == '[') { inLongComment = true; p += 4; continue; }
    //                    inLineComment = true; p += 2; continue;
    //                }

    //                if (c == '(') { paren++; p++; continue; }
    //                if (c == ')') { paren = Math.Max(0, paren - 1); p++; continue; }

    //                // 只有在条件括号层级为 0 时，出现 'do' 才算 while 的 do
    //                if (paren == 0 && IsWordAt(p, "do"))
    //                {
    //                    doPos = p;
    //                    break;
    //                }
    //                p++;
    //            }

    //            int condEnd = (doPos >= 0) ? doPos : p;
    //            condTrimmed = sb.ToString(condStart, condEnd - condStart).Trim();
    //            // 去掉外围括号
    //            condTrimmed = TrimOuterParens(condTrimmed);
    //            return (doPos >= 0) ? doPos : -1;
    //        }

    //        // 找与某个 'do' 对应的 'end'，需要考虑内部嵌套（if/then、function/end、for/do、while/do、do/end、repeat/until）
    //        int FindMatchingEndFromDo(int fromDo)
    //        {
    //            int p = fromDo;
    //            int depthEnd = 1; // 以当前 while/do 为 1 层
    //            bool localInStr = false; char localQ = '\0'; bool localEsc = false;
    //            bool localLineC = false; bool localLongC = false;

    //            while (p < n)
    //            {
    //                char c = sb[p];

    //                if (localInStr)
    //                {
    //                    if (localEsc) { localEsc = false; p++; continue; }
    //                    if (c == '\\') { localEsc = true; p++; continue; }
    //                    if (c == localQ) { localInStr = false; p++; continue; }
    //                    p++; continue;
    //                }
    //                if (localLineC)
    //                {
    //                    if (c == '\n' || c == '\r') localLineC = false;
    //                    p++; continue;
    //                }
    //                if (localLongC)
    //                {
    //                    if (c == ']' && p + 1 < n && sb[p + 1] == ']') { localLongC = false; p += 2; continue; }
    //                    p++; continue;
    //                }

    //                if (c == '"' || c == '\'') { localInStr = true; localQ = c; p++; continue; }
    //                if (c == '-' && p + 1 < n && sb[p + 1] == '-')
    //                {
    //                    if (p + 3 < n && sb[p + 2] == '[' && sb[p + 3] == '[') { localLongC = true; p += 4; continue; }
    //                    localLineC = true; p += 2; continue;
    //                }

    //                // 进入新的需要 'end' 的块
    //                if (IsWordAt(p, "function")) { depthEnd++; p += "function".Length; continue; }
    //                if (IsWordAt(p, "do")) { depthEnd++; p += 2; continue; }
    //                // if 的开启点是 then（而不是 if 本身）
    //                if (IsWordAt(p, "then")) { depthEnd++; p += 4; continue; }
    //                if (IsWordAt(p, "for")) { p += 3; continue; } // 计数交给后面的 'do'

    //                // repeat…until：对 end 深度不受影响，但要跳过内部 until 才算完一个 repeat
    //                if (IsWordAt(p, "repeat"))
    //                {
    //                    // 跳到匹配的 until
    //                    p += "repeat".Length;
    //                    int repeatDepth = 1;
    //                    while (p < n && repeatDepth > 0)
    //                    {
    //                        char c2 = sb[p];
    //                        if (localInStr)
    //                        {
    //                            if (localEsc) { localEsc = false; p++; continue; }
    //                            if (c2 == '\\') { localEsc = true; p++; continue; }
    //                            if (c2 == localQ) { localInStr = false; p++; continue; }
    //                            p++; continue;
    //                        }
    //                        if (localLineC)
    //                        {
    //                            if (c2 == '\n' || c2 == '\r') localLineC = false;
    //                            p++; continue;
    //                        }
    //                        if (localLongC)
    //                        {
    //                            if (c2 == ']' && p + 1 < n && sb[p + 1] == ']') { localLongC = false; p += 2; continue; }
    //                            p++; continue;
    //                        }

    //                        if (c2 == '"' || c2 == '\'') { localInStr = true; localQ = c2; p++; continue; }
    //                        if (c2 == '-' && p + 1 < n && sb[p + 1] == '-')
    //                        {
    //                            if (p + 3 < n && sb[p + 2] == '[' && sb[p + 3] == '[') { localLongC = true; p += 4; continue; }
    //                            localLineC = true; p += 2; continue;
    //                        }

    //                        if (IsWordAt(p, "repeat")) { repeatDepth++; p += "repeat".Length; continue; }
    //                        if (IsWordAt(p, "until")) { repeatDepth--; p += "until".Length; continue; }
    //                        p++;
    //                    }
    //                    continue;
    //                }

    //                // 关闭一个 end 块
    //                if (IsWordAt(p, "end"))
    //                {
    //                    depthEnd--;
    //                    p += 3;
    //                    if (depthEnd == 0) return p; // 返回 'end' 之后的位置（半开区间右端）
    //                    continue;
    //                }

    //                p++;
    //            }
    //            return -1;
    //        }

    //        // 简单外层括号剥离
    //        string TrimOuterParens(string s)
    //        {
    //            if (string.IsNullOrEmpty(s)) return s;
    //            int l = 0, r = s.Length - 1;
    //            while (l < r && char.IsWhiteSpace(s[l])) l++;
    //            while (r > l && char.IsWhiteSpace(s[r])) r--;
    //            if (s[l] != '(' || s[r] != ')') return s.Substring(l, r - l + 1);
    //            int depth = 0;
    //            for (int k = l; k <= r; k++)
    //            {
    //                char c = s[k];
    //                if (c == '(') depth++;
    //                else if (c == ')')
    //                {
    //                    depth--;
    //                    if (depth == 0 && k != r) // 外层括号不是一整圈
    //                        return s.Substring(l, r - l + 1);
    //                }
    //            }
    //            // 是一整圈外括号
    //            return TrimOuterParens(s.Substring(l + 1, r - l - 1));
    //        }

    //        // 条件是否“明显恒真”
    //        bool IsObviouslyTrue(string cond)
    //        {
    //            if (string.IsNullOrEmpty(cond)) return false;
    //            string x = cond.Replace(" ", "").Replace("\t", "");
    //            if (x == "true" || x == "1") return true;
    //            if (x == "notfalse" || x == "not(false)") return true;
    //            // 需要的话可以继续扩展：比如 (1==1)、(0<1) 等，这里先保守处理
    //            return false;
    //        }

    //        while (i < n)
    //        {
    //            char c = sb[i];

    //            // 维护顶层的字符串/注释状态
    //            if (inStr)
    //            {
    //                if (esc) { esc = false; i++; continue; }
    //                if (c == '\\') { esc = true; i++; continue; }
    //                if (c == strQ) { inStr = false; i++; continue; }
    //                i++; continue;
    //            }
    //            if (inLineComment)
    //            {
    //                if (c == '\n' || c == '\r') inLineComment = false;
    //                i++; continue;
    //            }
    //            if (inLongComment)
    //            {
    //                if (c == ']' && i + 1 < n && sb[i + 1] == ']') { inLongComment = false; i += 2; continue; }
    //                i++; continue;
    //            }

    //            if (c == '"' || c == '\'') { inStr = true; strQ = c; i++; continue; }
    //            if (c == '-' && i + 1 < n && sb[i + 1] == '-')
    //            {
    //                if (i + 3 < n && sb[i + 2] == '[' && sb[i + 3] == '[') { inLongComment = true; i += 4; continue; }
    //                inLineComment = true; i += 2; continue;
    //            }

    //            // 命中 while
    //            if (IsWordAt(i, "while"))
    //            {
    //                int whileStart = i;
    //                i += "while".Length;
    //                i = SkipWs(i);

    //                // 找到与条件同层的 'do'
    //                int doPos;
    //                string condText;
    //                int pos = ScanCondUntilDo(i, out doPos, out condText);

    //                if (doPos >= 0 && IsObviouslyTrue(condText))
    //                {
    //                    // 找匹配的 'end'
    //                    int endAfter = FindMatchingEndFromDo(doPos);
    //                    if (endAfter > 0)
    //                    {
    //                        list.Add(new Range { Start = whileStart, End = endAfter });
    //                        i = endAfter; // 继续往后扫描
    //                        continue;
    //                    }
    //                }

    //                // 非恒真或没匹配成功 -> 正常前进
    //                i++;
    //                continue;
    //            }

    //            i++;
    //        }

    //        return list;
    //    }
    //}


    //[HarmonyPatch]
    //[Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    //public static class Patch_LuaState_L_DoString_FilterInfiniteWhile
    //{
    //    // 找到 UniLua.LuaState.L_DoString(string)
    //    static MethodBase TargetMethod()
    //    {
    //        var t = AccessTools.TypeByName("UniLua.LuaState") ?? typeof(UniLua.LuaState);
    //        return AccessTools.Method(t, "L_DoString", new Type[] { typeof(string) });
    //    }

    //    // 直接改入参，让原方法用清洗后的脚本执行
    //    static void Prefix(UniLua.LuaState __instance, ref string s)
    //    {
    //        if (string.IsNullOrEmpty(s)) return;
    //        int removed;
    //        string cleaned = RemoveInfiniteWhiles(s, out removed);
    //        if (removed > 0)
    //        {
    //            try { FileLogger.Log("LUA_SAN(L_Do)", $"Removed {removed} infinite while-block(s) before L_DoString."); } catch { }
    //            FileLogger.Log("", cleaned);
    //            s = cleaned;
    //        }
    //    }

    //    // === 下面是“清洗 while 死循环”的工具函数（与之前相同/可共用） ===

    //    struct Range { public int Start; public int End; }

    //    static string RemoveInfiniteWhiles(string src, out int removedCount)
    //    {
    //        removedCount = 0;
    //        if (string.IsNullOrEmpty(src)) return src;

    //        StringBuilder sb = new StringBuilder(src);
    //        while (true)
    //        {
    //            var ranges = FindInfiniteWhileRanges(sb);
    //            if (ranges == null || ranges.Count == 0) break;

    //            for (int i = ranges.Count - 1; i >= 0; --i)
    //            {
    //                var r = ranges[i];
    //                ClampRange(sb.Length, ref r);
    //                if (r.End > r.Start)
    //                {
    //                    sb.Remove(r.Start, r.End - r.Start);
    //                    sb.Insert(r.Start, "end"); // 保持表项/表达式合法
    //                    removedCount++;
    //                }
    //            }
    //        }
    //        return sb.ToString();
    //    }

    //    static void ClampRange(int len, ref Range r)
    //    {
    //        if (r.Start < 0) r.Start = 0;
    //        if (r.End < 0) r.End = 0;
    //        if (r.Start > len) r.Start = len;
    //        if (r.End > len) r.End = len;
    //        if (r.End < r.Start) r.End = r.Start;
    //    }

    //    static System.Collections.Generic.List<Range> FindInfiniteWhileRanges(StringBuilder sb)
    //    {
    //        var list = new System.Collections.Generic.List<Range>();
    //        int n = sb.Length;

    //        bool inStr = false; char strQ = '\0'; bool esc = false;
    //        bool inLineComment = false;
    //        bool inLongComment = false;

    //        bool IsWordAt(int pos, string kw)
    //        {
    //            if (pos < 0 || pos + kw.Length > n) return false;
    //            for (int k = 0; k < kw.Length; k++)
    //                if (sb[pos + k] != kw[k]) return false;
    //            char prev = (pos > 0) ? sb[pos - 1] : '\0';
    //            char next = (pos + kw.Length < n) ? sb[pos + kw.Length] : '\0';
    //            bool prevOk = !(char.IsLetterOrDigit(prev) || prev == '_');
    //            bool nextOk = !(char.IsLetterOrDigit(next) || next == '_');
    //            return prevOk && nextOk;
    //        }

    //        int SkipWs(int p)
    //        {
    //            while (p < n)
    //            {
    //                char c = sb[p];
    //                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') p++;
    //                else break;
    //            }
    //            return p;
    //        }

    //        string TrimOuterParens(string s)
    //        {
    //            if (string.IsNullOrEmpty(s)) return s;
    //            int l = 0, r = s.Length - 1;
    //            while (l < r && char.IsWhiteSpace(s[l])) l++;
    //            while (r > l && char.IsWhiteSpace(s[r])) r--;
    //            if (l >= r) return s.Substring(l, r - l + 1);
    //            if (s[l] != '(' || s[r] != ')') return s.Substring(l, r - l + 1);
    //            int depth = 0;
    //            for (int k = l; k <= r; k++)
    //            {
    //                char c = s[k];
    //                if (c == '(') depth++;
    //                else if (c == ')')
    //                {
    //                    depth--;
    //                    if (depth == 0 && k != r) return s.Substring(l, r - l + 1);
    //                }
    //            }
    //            return TrimOuterParens(s.Substring(l + 1, r - l - 1));
    //        }

    //        bool IsObviouslyTrue(string cond)
    //        {
    //            if (string.IsNullOrEmpty(cond)) return false;
    //            string x = cond.Replace(" ", "").Replace("\t", "");
    //            if (x == "true" || x == "1") return true;
    //            if (x == "notfalse" || x == "not(false)") return true;
    //            return false;
    //        }

    //        int ScanCondUntilDo(int from, out int doPos, out string condTrimmed)
    //        {
    //            int nLocal = n;
    //            int p = from; doPos = -1;
    //            int paren = 0;
    //            int condStart = p;

    //            bool sInStr = inStr, sEsc = esc; char sQ = strQ;
    //            bool sInLC = inLineComment, sInLongC = inLongComment;

    //            while (p < nLocal)
    //            {
    //                char c = sb[p];

    //                if (sInStr)
    //                {
    //                    if (sEsc) { sEsc = false; p++; continue; }
    //                    if (c == '\\') { sEsc = true; p++; continue; }
    //                    if (c == sQ) { sInStr = false; p++; continue; }
    //                    p++; continue;
    //                }
    //                if (sInLC)
    //                {
    //                    if (c == '\n' || c == '\r') sInLC = false;
    //                    p++; continue;
    //                }
    //                if (sInLongC)
    //                {
    //                    if (c == ']' && p + 1 < nLocal && sb[p + 1] == ']') { sInLongC = false; p += 2; continue; }
    //                    p++; continue;
    //                }

    //                if (c == '"' || c == '\'') { sInStr = true; sQ = c; p++; continue; }
    //                if (c == '-' && p + 1 < nLocal && sb[p + 1] == '-')
    //                {
    //                    if (p + 3 < nLocal && sb[p + 2] == '[' && sb[p + 3] == '[') { sInLongC = true; p += 4; continue; }
    //                    sInLC = true; p += 2; continue;
    //                }

    //                if (c == '(') { paren++; p++; continue; }
    //                if (c == ')') { paren = Math.Max(0, paren - 1); p++; continue; }

    //                if (paren == 0 && IsWordAt(p, "do")) { doPos = p; break; }
    //                p++;
    //            }
    //            int condEnd = (doPos >= 0) ? doPos : p;
    //            condTrimmed = sb.ToString(condStart, condEnd - condStart).Trim();
    //            condTrimmed = TrimOuterParens(condTrimmed);
    //            return (doPos >= 0) ? doPos : -1;
    //        }

    //        int FindMatchingEndFromDo(int fromDo)
    //        {
    //            int p = fromDo;
    //            int depthEnd = 1;
    //            bool sInStr = false; char sQ = '\0'; bool sEsc = false;
    //            bool sInLC = false; bool sInLongC = false;

    //            while (p < n)
    //            {
    //                char c = sb[p];

    //                if (sInStr)
    //                {
    //                    if (sEsc) { sEsc = false; p++; continue; }
    //                    if (c == '\\') { sEsc = true; p++; continue; }
    //                    if (c == sQ) { sInStr = false; p++; continue; }
    //                    p++; continue;
    //                }
    //                if (sInLC)
    //                {
    //                    if (c == '\n' || c == '\r') sInLC = false;
    //                    p++; continue;
    //                }
    //                if (sInLongC)
    //                {
    //                    if (c == ']' && p + 1 < n && sb[p + 1] == ']') { sInLongC = false; p += 2; continue; }
    //                    p++; continue;
    //                }

    //                if (c == '"' || c == '\'') { sInStr = true; sQ = c; p++; continue; }
    //                if (c == '-' && p + 1 < n && sb[p + 1] == '-')
    //                {
    //                    if (p + 3 < n && sb[p + 2] == '[' && sb[p + 3] == '[') { sInLongC = true; p += 4; continue; }
    //                    sInLC = true; p += 2; continue;
    //                }

    //                if (IsWordAt(p, "function")) { depthEnd++; p += "function".Length; continue; }
    //                if (IsWordAt(p, "do")) { depthEnd++; p += 2; continue; }
    //                if (IsWordAt(p, "then")) { depthEnd++; p += 4; continue; }

    //                if (IsWordAt(p, "repeat"))
    //                {
    //                    p += "repeat".Length;
    //                    // 跳到匹配的 until
    //                    int repDepth = 1;
    //                    while (p < n && repDepth > 0)
    //                    {
    //                        char c2 = sb[p];
    //                        if (sInStr)
    //                        {
    //                            if (sEsc) { sEsc = false; p++; continue; }
    //                            if (c2 == '\\') { sEsc = true; p++; continue; }
    //                            if (c2 == sQ) { sInStr = false; p++; continue; }
    //                            p++; continue;
    //                        }
    //                        if (sInLC)
    //                        {
    //                            if (c2 == '\n' || c2 == '\r') sInLC = false;
    //                            p++; continue;
    //                        }
    //                        if (sInLongC)
    //                        {
    //                            if (c2 == ']' && p + 1 < n && sb[p + 1] == ']') { sInLongC = false; p += 2; continue; }
    //                            p++; continue;
    //                        }

    //                        if (c2 == '"' || c2 == '\'') { sInStr = true; sQ = c2; p++; continue; }
    //                        if (c2 == '-' && p + 1 < n && sb[p + 1] == '-')
    //                        {
    //                            if (p + 3 < n && sb[p + 2] == '[' && sb[p + 3] == '[') { sInLongC = true; p += 4; continue; }
    //                            sInLC = true; p += 2; continue;
    //                        }

    //                        if (IsWordAt(p, "repeat")) { repDepth++; p += "repeat".Length; continue; }
    //                        if (IsWordAt(p, "until")) { repDepth--; p += "until".Length; continue; }
    //                        p++;
    //                    }
    //                    continue;
    //                }

    //                if (IsWordAt(p, "end"))
    //                {
    //                    depthEnd--;
    //                    p += 3;
    //                    if (depthEnd == 0) return p;
    //                    continue;
    //                }

    //                p++;
    //            }
    //            return -1;
    //        }

    //        int i = 0;
    //        while (i < n)
    //        {
    //            char c = sb[i];

    //            if (inStr)
    //            {
    //                if (esc) { esc = false; i++; continue; }
    //                if (c == '\\') { esc = true; i++; continue; }
    //                if (c == strQ) { inStr = false; i++; continue; }
    //                i++; continue;
    //            }
    //            if (inLineComment)
    //            {
    //                if (c == '\n' || c == '\r') inLineComment = false;
    //                i++; continue;
    //            }
    //            if (inLongComment)
    //            {
    //                if (c == ']' && i + 1 < n && sb[i + 1] == ']') { inLongComment = false; i += 2; continue; }
    //                i++; continue;
    //            }

    //            if (c == '"' || c == '\'') { inStr = true; strQ = c; i++; continue; }
    //            if (c == '-' && i + 1 < n && sb[i + 1] == '-')
    //            {
    //                if (i + 3 < n && sb[i + 2] == '[' && sb[i + 3] == '[') { inLongComment = true; i += 4; continue; }
    //                inLineComment = true; i += 2; continue;
    //            }

    //            if (IsWordAt(i, "while"))
    //            {
    //                int whileStart = i;
    //                i += "while".Length;

    //                // 条件起点
    //                i = SkipWs(i);
    //                int doPos;
    //                string condText;
    //                int posDo = ScanCondUntilDo(i, out doPos, out condText);

    //                if (doPos >= 0 && IsObviouslyTrue(condText))
    //                {
    //                    int endAfter = FindMatchingEndFromDo(doPos);
    //                    if (endAfter > 0)
    //                    {
    //                        list.Add(new Range { Start = whileStart, End = endAfter });
    //                        i = endAfter;
    //                        continue;
    //                    }
    //                }
    //                i++;
    //                continue;
    //            }

    //            i++;
    //        }

    //        return list;
    //    }
    //}


    //[HarmonyPatch]
    //[Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    //public static class Patch_ParseChannelInfo_AlwaysBypass
    //{
    //    // 如果工程能直接引用目标类型，用 typeof(CharacterInfoData) + nameof 也可以
    //    static MethodBase TargetMethod()
    //    {
    //        var t = AccessTools.TypeByName("CharacterInfoData");
    //        return AccessTools.Method(t, "ParseChannelInfo",
    //            new Type[] { typeof(string[]), typeof(int), typeof(int).MakeByRefType(), typeof(Vector4[]).MakeByRefType() });
    //    }

    //    // 直接给默认输出并跳过原函数
    //    static bool Prefix(string[] datas, int startID, ref int num, out Vector4[] channel)
    //    {
    //        num = 1; // 最少启用 1 个通道
    //        channel = new Vector4[3];               // 下游普遍假设长度=3更安全
    //        channel[0] = new Vector4(0.5f, 0.5f, 0.5f, 1f);
    //        channel[1] = new Vector4(0.5f, 0.5f, 0.5f, 1f);
    //        channel[2] = new Vector4(0.5f, 0.5f, 0.5f, 1f);
    //        return false; // 跳过原实现
    //    }
    //}

    //[HarmonyPatch]
    //[Obfuscation(
    //Exclude = true,                 // 排除本类型
    //ApplyToMembers = true,          // 并排除所有成员
    //Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    //StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    //)]
    //public static class Patch_UICreateCharacter_FinishBtn_Prefix
    //{
    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static MethodBase TargetMethod()
    //    {
    //        try
    //        {
    //            var asm = GetAsm("Assembly-CSharp");
    //            if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

    //            var t = asm.GetType("UICreateCharacter");
    //            if (t == null) { FileLogger.Log("PATCH", "Type UICreateCharacter not found"); return null; }

    //            var m = AccessTools.Method(t, "FinishBtn");
    //            if (m == null) FileLogger.Log("PATCH", "Method FinishBtn not found");
    //            return m;
    //        }
    //        catch (Exception e)
    //        {
    //            FileLogger.Log("PATCH", "TargetMethod(FinishBtn) error: " + e);
    //            return null;
    //        }
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static bool Prefix(UICreateCharacter __instance, GameObject button)
    //    {
    //        var page = Traverse.Create(__instance).Field("createCharacterPage").GetValue<int>();
    //        if (page == 0)
    //        {
    //            global::UIPlayerWin.roleId = Traverse.Create(__instance).Field("roleId").GetValue<string>();
    //            global::CreateCharacterState createCharacterState = global::ASSingleton<global::GameStateManager>.Instance.CurState as global::CreateCharacterState;
    //            if (createCharacterState != null)
    //            {
    //                global::MessageBox messageBox = global::MessageBox.getInstance();
    //                messageBox.createBox(global::MessageBox.BoxType.Blank, null, 0f);
    //                messageBox.setMessage("msgbox_common_num_1302".valueByThisKey(), 510);
    //                messageBox.show(true);
    //                createCharacterState.EnterLobby();
    //                return false;
    //            }
    //        }
    //        return true;
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static Assembly GetAsm(string name)
    //    {
    //        var asms = AppDomain.CurrentDomain.GetAssemblies();
    //        for (int i = 0; i < asms.Length; i++)
    //        {
    //            try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
    //        }
    //        return null;
    //    }
    //}

    //[HarmonyPatch]
    //[Obfuscation(
    //    Exclude = true,                 // 排除本类型
    //    ApplyToMembers = true,          // 并排除所有成员
    //    Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    //    StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    //    )]
    //public static class Patch_AvatarLuaParse_SkinInit_Prefix
    //{
    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static MethodBase TargetMethod()
    //    {
    //        try
    //        {
    //            var asm = GetAsm("Assembly-CSharp");
    //            if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

    //            var t = asm.GetType("AvatarLuaParse");
    //            if (t == null) { FileLogger.Log("PATCH", "Type AvatarLuaParse not found"); return null; }

    //            var m = AccessTools.Method(t, "SkinInit");
    //            if (m == null) FileLogger.Log("PATCH", "Method SkinInit not found");
    //            return m;
    //        }
    //        catch (Exception e)
    //        {
    //            FileLogger.Log("PATCH", "TargetMethod(SkinInit) error: " + e);
    //            return null;
    //        }
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static bool Prefix(AvatarLuaParse __instance, string[] datas, global::CharacterInfoData CharaterInfo)
    //    {
    //        foreach (var data in datas)
    //        {
    //            FileLogger.Log("皮肤数据", data);
    //        }
    //        //datas[1] = "10";

    //        return true;
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static Assembly GetAsm(string name)
    //    {
    //        var asms = AppDomain.CurrentDomain.GetAssemblies();
    //        for (int i = 0; i < asms.Length; i++)
    //        {
    //            try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
    //        }
    //        return null;
    //    }
    //}

    //[HarmonyPatch]
    //[Obfuscation(
    //Exclude = true,                 // 排除本类型
    //ApplyToMembers = true,          // 并排除所有成员
    //Feature = "-rename",            // 重点：不要重命名（有的混淆器也接受 "rename(false)" 或 "renaming")
    //StripAfterObfuscation = false   // 重要：不要在混淆后删除这些 Attribute
    //)]
    //public static class Patch_UIRankingListItem_ParseLuaString_Prefix
    //{
    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static MethodBase TargetMethod()
    //    {
    //        try
    //        {
    //            var asm = GetAsm("Assembly-CSharp");
    //            if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

    //            var t = asm.GetType("AvatarLuaParse");
    //            if (t == null) { FileLogger.Log("PATCH", "Type AvatarLuaParse not found"); return null; }

    //            var m = AccessTools.Method(t, "ParseLuaString");
    //            if (m == null) FileLogger.Log("PATCH", "Method ParseLuaString not found");
    //            return m;
    //        }
    //        catch (Exception e)
    //        {
    //            FileLogger.Log("PATCH", "TargetMethod(ParseLuaString) error: " + e);
    //            return null;
    //        }
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static bool Prefix(AvatarLuaParse __instance, string luaStr)
    //    {
    //        FileLogger.Log("",luaStr);

    //        return true;
    //    }

    //    [Obfuscation(Exclude = true, Feature = "-rename")]
    //    static Assembly GetAsm(string name)
    //    {
    //        var asms = AppDomain.CurrentDomain.GetAssemblies();
    //        for (int i = 0; i < asms.Length; i++)
    //        {
    //            try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
    //        }
    //        return null;
    //    }
    //}

    [HarmonyPatch]
    [Obfuscation(
        Exclude = true,
        ApplyToMembers = true,
        Feature = "-rename",
        StripAfterObfuscation = false
    )]
    public static class Patch_LoginState_Login_LocalRedirect
    {
        private const string RedirectHost = "127.0.0.1";
        private const int RedirectPort = 3100;

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            // [已禁用] 不再劫持登录到本地服务器，直接返回 null 跳过此补丁
            return null;
            /*
            try
            {
                var asm = GetAsm("Assembly-CSharp");
                if (asm == null) { FileLogger.Log("PATCH", "Assembly-CSharp not found"); return null; }

                var t = asm.GetType("LoginState");
                if (t == null) { FileLogger.Log("PATCH", "Type LoginState not found"); return null; }

                var m = AccessTools.Method(t, "Login", new Type[] { typeof(string), typeof(string) });
                if (m == null) FileLogger.Log("PATCH", "Method LoginState.Login not found");
                return m;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "TargetMethod(LoginState.Login) error: " + e);
                return null;
            }
            */
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(string name, string password)
        {
            try
            {
                if (global::GameApp.Instance == null)
                {
                    FileLogger.Log("PATCH", "[LoginRedirect] GameApp.Instance == null");
                    return true;
                }

                try
                {
                    if (global::GameApp.Instance.lobby_connection != null)
                    {
                        global::GameApp.Instance.lobby_connection.Disconnect();
                    }
                }
                catch (Exception disconnectEx)
                {
                    FileLogger.Log("PATCH", "[LoginRedirect] disconnect skipped: " + disconnectEx.Message);
                }

                global::StartConfig.platform = 0;
                global::GameApp.Instance.lobby_connection = new global::LobbyConnection();
                global::GameApp.Instance.lobby_connection.login_name = name ?? string.Empty;
                global::GameApp.Instance.lobby_connection.login_pass = password ?? string.Empty;
                global::GameApp.Instance.lobby_connection.real_login_ip = RedirectHost;

                FileLogger.Log("PATCH",
                    "[LoginRedirect] force connect " + RedirectHost + ":" + RedirectPort +
                    " user=" + (name ?? string.Empty));

                global::GameApp.Instance.lobby_connection.Connect(RedirectHost, RedirectPort);
                global::GameApp.Instance.error_message = "connect_failed";
                return false;
            }
            catch (Exception e)
            {
                FileLogger.Log("PATCH", "[LoginRedirect] prefix error: " + e);
                return true;
            }
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static Assembly GetAsm(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try { if (asms[i].GetName().Name == name) return asms[i]; } catch { }
            }
            return null;
        }
    }
}
