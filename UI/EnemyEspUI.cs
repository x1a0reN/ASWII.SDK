using System;
using System.Collections.Generic;
using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Cheats.SurvivalBot;
using UnityEngine;

namespace ASWDEBUG.UI
{
    public static class EnemyEspUI
    {
        private static readonly Color VisibleColor = new Color(0.20f, 1f, 0.35f, 1f);
        private static readonly Color BlockedColor = new Color(1f, 0.25f, 0.18f, 1f);
        private static readonly Color HiddenColor = new Color(0.78f, 0.35f, 1f, 1f);
        private static Texture2D _pixel;
        private static GUIStyle _labelStyle;

        public static void Display(Camera camera)
        {
            if (!SurvivalBotSettings.EnemyEspEnabled || camera == null) return;
            Event current = Event.current;
            if (current != null && current.type != EventType.Repaint) return;

            GameApp app = GameApp.Instance;
            bool localLevel33Test = SurvivalBotManager.Level33TestEnabled && MapBakeSceneLoader.DirectSceneActive;
            if (!localLevel33Test && (app == null || app.channel_connection == null ||
                app.channel_connection.state != ChannelConnection.State.kInGame)) return;

            Level level;
            Character player;
            try
            {
                level = ASSingleton<Level>.Instance;
                player = level == null ? null : level.GetPlayer();
            }
            catch
            {
                return;
            }
            if (level == null || player == null) return;

            EnsureResources();
            List<Character> characters;
            try { characters = level.GetCharacters(); }
            catch { return; }
            if (characters == null) return;

            for (int i = 0; i < characters.Count; i++)
            {
                Character target = characters[i];
                if (!IsLivingEnemy(level, player, target)) continue;
                DrawEnemy(camera, player, target);
            }
        }

        private static void DrawEnemy(Camera camera, Character player, Character target)
        {
            Vector3 feetWorld = target.transform.position + Vector3.up * 0.08f;
            Vector3 headWorld = target.transform.position + Vector3.up * 1.72f;
            Vector3 feet = camera.WorldToScreenPoint(feetWorld);
            Vector3 head = camera.WorldToScreenPoint(headWorld);
            bool hidden = IsHidden(target);
            bool visible = !hidden && SurvivalCombatAdapter.SurvivalHasStrictFireLine(player, target, camera);
            Color color = hidden ? HiddenColor : visible ? VisibleColor : BlockedColor;
            float distance = Vector3.Distance(player.transform.position, target.transform.position);
            string state = hidden ? "隐身" : visible ? "可见" : "隔墙";
            string label = "#" + target.uid + "  " + distance.ToString("0.0") + "m  " + state;

            if (feet.z <= 0f || head.z <= 0f ||
                head.x < 0f || head.x > Screen.width || head.y < 0f || head.y > Screen.height)
            {
                DrawEdgeMarker(head, label, color);
                return;
            }

            float top = Screen.height - head.y;
            float bottom = Screen.height - feet.y;
            if (bottom < top)
            {
                float swap = top;
                top = bottom;
                bottom = swap;
            }
            float height = Mathf.Clamp(bottom - top, 24f, Screen.height * 0.85f);
            float width = Mathf.Clamp(height * 0.46f, 14f, 180f);
            float centerX = (head.x + feet.x) * 0.5f;
            Rect box = new Rect(centerX - width * 0.5f, top, width, height);

            DrawOutline(box, 3f, new Color(0f, 0f, 0f, 0.85f));
            DrawOutline(box, 1.4f, color);
            DrawLabel(new Rect(box.x - 58f, Mathf.Max(2f, box.y - 19f), box.width + 116f, 18f), label, color);
        }

        private static void DrawEdgeMarker(Vector3 screenPoint, string label, Color color)
        {
            float x = screenPoint.x;
            float y = Screen.height - screenPoint.y;
            if (screenPoint.z <= 0f)
            {
                x = Screen.width - x;
                y = Screen.height - y;
            }
            x = Mathf.Clamp(x, 34f, Screen.width - 34f);
            y = Mathf.Clamp(y, 28f, Screen.height - 28f);
            DrawFilled(new Rect(x - 4f, y - 4f, 8f, 8f), color);
            DrawLabel(new Rect(x - 72f, y + 5f, 144f, 18f), label, color);
        }

        private static bool IsLivingEnemy(Level level, Character player, Character target)
        {
            if (target == null || target == player || target.IsDied || target.Is_Viewer) return false;
            try
            {
                if (level.game_type == RoomInfo.GameType.kGameTypeChiji) return true;
                int playerTeam = player.GetTeam();
                int targetTeam = target.GetTeam();
                return playerTeam < 0 || targetTeam < 0 || playerTeam != targetTeam;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsHidden(Character target)
        {
            try { return target.GetHidden(); }
            catch { return false; }
        }

        private static void DrawOutline(Rect rect, float thickness, Color color)
        {
            DrawFilled(new Rect(rect.x, rect.y, rect.width, thickness), color);
            DrawFilled(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            DrawFilled(new Rect(rect.x, rect.y, thickness, rect.height), color);
            DrawFilled(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private static void DrawLabel(Rect rect, string text, Color color)
        {
            DrawFilled(rect, new Color(0f, 0f, 0f, 0.72f));
            Color old = _labelStyle.normal.textColor;
            _labelStyle.normal.textColor = color;
            GUI.Label(rect, text, _labelStyle);
            _labelStyle.normal.textColor = old;
        }

        private static void DrawFilled(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _pixel);
            GUI.color = old;
        }

        private static void EnsureResources()
        {
            if (_pixel == null)
            {
                _pixel = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _pixel.hideFlags = HideFlags.HideAndDontSave;
                _pixel.SetPixel(0, 0, Color.white);
                _pixel.Apply();
            }
            if (_labelStyle != null) return;
            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.alignment = TextAnchor.MiddleCenter;
            _labelStyle.fontSize = 12;
            _labelStyle.fontStyle = FontStyle.Bold;
            _labelStyle.clipping = TextClipping.Clip;
        }
    }
}
