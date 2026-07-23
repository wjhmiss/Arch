using System.Collections.Generic;
using UnityEngine;

namespace ArchUnityDemo
{
    /// <summary>
    /// 全局调试 HUD：在屏幕左上角显示当前章节、按键提示和实时状态。
    /// 通过按 F1 切换显示。
    /// </summary>
    public class DebugHUD : MonoBehaviour
    {
        public static DebugHUD Instance { get; private set; }

        /// <summary>是否显示 HUD</summary>
        public bool Visible { get; set; } = true;

        /// <summary>当前章节编号</summary>
        public int CurrentChapter { get; set; } = 1;

        /// <summary>当前章节标题</summary>
        public string CurrentTitle { get; set; } = "";

        /// <summary>当前章节描述</summary>
        public string CurrentDescription { get; set; } = "";

        /// <summary>实时状态行（每帧由 Demo 更新）</summary>
        public readonly List<string> StatusLines = new();

        /// <summary>日志缓冲（最近 N 条）</summary>
        private readonly List<string> _logBuffer = new();
        private const int MaxLogLines = 12;

        private void Awake()
        {
            Instance = this;
            Application.logMessageReceived += OnLogMessage;
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLogMessage;
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            var prefix = type switch
            {
                LogType.Error => "[ERR]",
                LogType.Warning => "[WARN]",
                LogType.Log => "[LOG]",
                _ => "[?]"
            };
            _logBuffer.Add($"{prefix} {condition}");
            while (_logBuffer.Count > MaxLogLines)
                _logBuffer.RemoveAt(0);
        }

        public void ClearStatus()
        {
            StatusLines.Clear();
        }

        public void AddStatus(string line)
        {
            StatusLines.Add(line);
        }

        private void OnGUI()
        {
            if (!Visible) return;

            // 左上角：章节信息 + 状态
            var rect = new Rect(10, 10, 480, 320);
            GUI.Box(rect, "");

            GUILayout.BeginArea(rect);
            GUILayout.Label($"<size=18><b>第 {CurrentChapter} 章：{CurrentTitle}</b></size>", RichTextStyle());
            GUILayout.Label($"<size=11>{CurrentDescription}</size>", RichTextStyle());
            GUILayout.Space(6);

            GUILayout.Label("<b>状态：</b>", RichTextStyle());
            foreach (var line in StatusLines)
                GUILayout.Label($"  {line}", RichTextStyle());

            GUILayout.Space(6);
            GUILayout.Label("<b>日志：</b>", RichTextStyle());
            var start = Mathf.Max(0, _logBuffer.Count - 6);
            for (var i = start; i < _logBuffer.Count; i++)
                GUILayout.Label($"<size=10>{_logBuffer[i]}</size>", RichTextStyle());
            GUILayout.EndArea();

            // 右下角：按键提示
            var hintRect = new Rect(Screen.width - 320, Screen.height - 180, 310, 170);
            GUI.Box(hintRect, "");
            GUILayout.BeginArea(hintRect);
            GUILayout.Label("<b>快捷键</b>", RichTextStyle());
            GUILayout.Label("  Space / N : 下一章", RichTextStyle());
            GUILayout.Label("  B / P     : 上一章", RichTextStyle());
            GUILayout.Label("  R         : 重启本章", RichTextStyle());
            GUILayout.Label("  1 - 9     : 跳到对应章节", RichTextStyle());
            GUILayout.Label("  F1        : 切换 HUD 显示", RichTextStyle());
            GUILayout.EndArea();
        }

        private static GUIStyle RichTextStyle()
        {
            var style = new GUIStyle(GUI.skin.label);
            style.richText = true;
            return style;
        }
    }
}
