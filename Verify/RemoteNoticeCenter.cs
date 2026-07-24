using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Verify
{
    internal sealed class RemoteNoticeCenter : MonoBehaviour
    {
        private sealed class Notice
        {
            internal string Title;
            internal string Content;
            internal string Severity;
            internal string Version;
            internal string DownloadUrl;
            internal bool Mandatory;
        }

        private static RemoteNoticeCenter _instance;
        private readonly Queue<Notice> _queue = new Queue<Notice>();
        private Notice _current;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _metaStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _primaryButtonStyle;

        internal static void ShowAnnouncement(string payload)
        {
            Notice notice = new Notice
            {
                Title = ReadString(payload, "title") ?? "软件公告",
                Content = ReadString(payload, "content") ?? string.Empty,
                Severity = ReadString(payload, "severity") ?? "info"
            };
            Show(notice);
        }

        internal static void ShowUpdate(string payload)
        {
            string version = ReadString(payload, "version") ?? string.Empty;
            Notice notice = new Notice
            {
                Title = ReadString(payload, "title") ?? "发现新版本",
                Content = ReadString(payload, "release_notes") ??
                    "新版本已经发布，请根据提示完成更新。",
                Severity = ReadBoolean(payload, "mandatory") ? "critical" : "info",
                Version = version,
                DownloadUrl = SafeDownloadUrl(ReadString(payload, "download_url")),
                Mandatory = ReadBoolean(payload, "mandatory")
            };
            Show(notice);
        }

        private static void Show(Notice notice)
        {
            if (_instance == null)
            {
                GameObject host = new GameObject("VeriGateRemoteNotice");
                DontDestroyOnLoad(host);
                _instance = host.AddComponent<RemoteNoticeCenter>();
            }
            _instance._queue.Enqueue(notice);
            if (_instance._current == null)
                _instance._current = _instance._queue.Dequeue();
        }

        private void OnGUI()
        {
            if (_current == null) return;
            EnsureStyles();
            GUI.depth = -20000;

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.64f);
            GUI.DrawTexture(
                new Rect(0f, 0f, Screen.width, Screen.height),
                Texture2D.whiteTexture);
            GUI.color = previous;

            float width = Mathf.Min(620f, Screen.width - 32f);
            float height = Mathf.Min(440f, Screen.height - 32f);
            Rect panel = new Rect(
                (Screen.width - width) * 0.5f,
                (Screen.height - height) * 0.5f,
                width,
                height);

            GUI.color = new Color(0.055f, 0.06f, 0.075f, 0.99f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = previous;

            Color accent = Accent(_current.Severity);
            GUI.color = accent;
            GUI.DrawTexture(
                new Rect(panel.x, panel.y, 5f, panel.height),
                Texture2D.whiteTexture);
            GUI.color = previous;

            Rect content = new Rect(
                panel.x + 34f,
                panel.y + 28f,
                panel.width - 68f,
                panel.height - 56f);
            string category = _current.Version.Length > 0
                ? (_current.Mandatory ? "必须更新" : "版本更新")
                : SeverityLabel(_current.Severity);
            GUI.Label(
                new Rect(content.x, content.y, content.width, 22f),
                category.ToUpperInvariant(),
                _metaStyle);
            GUI.Label(
                new Rect(content.x, content.y + 30f, content.width, 42f),
                _current.Title,
                _titleStyle);

            string meta = _current.Version.Length == 0
                ? "来自 VeriGate 管理中心"
                : "版本 " + _current.Version +
                    (_current.Mandatory ? " · 更新后才能继续使用" : string.Empty);
            GUI.Label(
                new Rect(content.x, content.y + 76f, content.width, 22f),
                meta,
                _metaStyle);

            Rect body = new Rect(
                content.x,
                content.y + 112f,
                content.width,
                content.height - 168f);
            GUI.Label(body, _current.Content, _bodyStyle);

            float buttonY = panel.yMax - 58f;
            if (!string.IsNullOrEmpty(_current.DownloadUrl))
            {
                if (GUI.Button(
                    new Rect(panel.xMax - 300f, buttonY, 132f, 34f),
                    "打开下载页面",
                    _primaryButtonStyle))
                {
                    Application.OpenURL(_current.DownloadUrl);
                }
            }
            if (GUI.Button(
                new Rect(panel.xMax - 156f, buttonY, 122f, 34f),
                _current.Mandatory ? "我知道了" : "关闭",
                _buttonStyle))
            {
                _current = _queue.Count > 0 ? _queue.Dequeue() : null;
            }
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.94f, 0.95f, 0.98f) },
                wordWrap = true
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = new Color(0.75f, 0.78f, 0.84f) },
                wordWrap = true,
                richText = false
            };
            _metaStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.48f, 0.52f, 0.61f) }
            };
            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.91f, 0.94f) },
                hover = { textColor = Color.white }
            };
            _primaryButtonStyle = new GUIStyle(_buttonStyle)
            {
                normal = { textColor = Color.white },
                hover = { textColor = Color.white }
            };
        }

        private static Color Accent(string severity)
        {
            if (string.Equals(severity, "critical", StringComparison.Ordinal))
                return new Color(0.94f, 0.27f, 0.3f);
            if (string.Equals(severity, "warning", StringComparison.Ordinal))
                return new Color(0.95f, 0.64f, 0.2f);
            return new Color(0.35f, 0.55f, 1f);
        }

        private static string SeverityLabel(string severity)
        {
            if (string.Equals(severity, "critical", StringComparison.Ordinal))
                return "紧急公告";
            if (string.Equals(severity, "warning", StringComparison.Ordinal))
                return "重要公告";
            return "软件公告";
        }

        private static string SafeDownloadUrl(string value)
        {
            Uri parsed;
            return Uri.TryCreate(value, UriKind.Absolute, out parsed) &&
                string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? parsed.AbsoluteUri
                : null;
        }

        private static bool ReadBoolean(string json, string key)
        {
            int index = FindValue(json, key);
            if (index < 0) return false;
            return json.Substring(index).StartsWith(
                "true",
                StringComparison.Ordinal);
        }

        private static string ReadString(string json, string key)
        {
            int index = FindValue(json, key);
            if (index < 0 || index >= json.Length || json[index] != '"')
                return null;
            StringBuilder result = new StringBuilder();
            bool escaped = false;
            for (int i = index + 1; i < json.Length; i++)
            {
                char current = json[i];
                if (!escaped)
                {
                    if (current == '\\') escaped = true;
                    else if (current == '"') return result.ToString();
                    else result.Append(current);
                    continue;
                }
                switch (current)
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (i + 4 >= json.Length) return null;
                        int code;
                        if (!int.TryParse(
                            json.Substring(i + 1, 4),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out code))
                            return null;
                        result.Append((char)code);
                        i += 4;
                        break;
                    default: return null;
                }
                escaped = false;
            }
            return null;
        }

        private static int FindValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return -1;
            string marker = "\"" + key + "\"";
            int index = json.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return -1;
            index += marker.Length;
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            if (index >= json.Length || json[index] != ':') return -1;
            index++;
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            return index;
        }
    }
}
