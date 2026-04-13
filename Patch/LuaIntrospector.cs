using Harmony;
using System;
using System.Collections.Generic;
using UniLua;

public static class LuaIntrospector
{
    // 返回诸如 "print()", "math.sin()", "api.set_color()" 这样的签名列表
    public static List<string> ListAvailableApis(ILuaState L, int maxDepth = 1, int maxPerTable = 400)
    {
        var result = new List<string>();
        int top = L.GetTop();

        try
        {
            // 1) 全局表 _G
            L.PushGlobalTable(); // 栈顶 = _G
            WalkTable(L, L.GetTop(), "", 0, maxDepth, maxPerTable, result);
            L.Pop(1);

            // 2) 常见入口：package.loaded（若存在）
            L.GetGlobal("package");
            if (L.IsTable(-1))
            {
                L.GetField(-1, "loaded");
                if (L.IsTable(-1))
                {
                    WalkTable(L, L.GetTop(), "package.loaded.", 0, 1, maxPerTable, result);
                    L.Pop(1);
                }
                L.Pop(1);
            }
            else L.Pop(1);

            // 3) 你工程里可能存在的模块（按需添加）
            foreach (var mod in new[] { "api", "unity", "engine", "game" })
            {
                L.GetGlobal(mod);
                if (L.IsTable(-1))
                {
                    WalkTable(L, L.GetTop(), mod + ".", 0, 1, maxPerTable, result);
                }
                L.Pop(1);
            }
        }
        finally
        {
            L.SetTop(top); // 恢复栈
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void WalkTable(ILuaState L, int index, string prefix,
                                  int depth, int maxDepth, int maxPerTable,
                                  List<string> acc)
    {
        var API = Traverse.Create(L).Field("API").GetValue<ILuaAPI>();
        int abs = API.AbsIndex(index);
        int count = 0;

        L.PushNil(); // 第一次 key 为 nil
        while (API.Next(abs))
        {
            // stack: ... key at -2, value at -1
            string key = L.IsString(-2) ? L.ToString(-2) : $"[{L.Type(-2)}]";
            var t = L.Type(-1);

            if (t == LuaType.LUA_TFUNCTION)
            {
                acc.Add($"{prefix}{key}()");
            }
            else if (t == LuaType.LUA_TTABLE && depth < maxDepth)
            {
                // 只下探一层：常见的是模块表里的函数
                WalkTable(L, L.GetTop(), $"{prefix}{key}.", depth + 1, maxDepth, maxPerTable, acc);
            }

            L.Pop(1); // 弹出 value，保留 key 供 Next 继续
            count++;
            if (count >= maxPerTable) break; // 每张表限额，防止超大表卡死
        }
    }
}
