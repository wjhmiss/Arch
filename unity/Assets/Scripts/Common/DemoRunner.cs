using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArchUnityDemo
{
    /// <summary>
    /// 演示调度器：管理所有章节示例的生命周期，处理键盘切换。
    /// 挂载到场景中的 GameManager 物体上即可。
    /// </summary>
    public class DemoRunner : MonoBehaviour
    {
        [SerializeField] private bool autoStart = true;

        /// <summary>所有已注册的章节示例（按章节号排序）</summary>
        private readonly SortedList<int, IDemo> _demos = new();

        /// <summary>当前激活的示例</summary>
        private IDemo _current;

        /// <summary>当前章节索引（在 _demos 中的位置）</summary>
        private int _currentIndex = 0;

        private DebugHUD _hud;

        private void Awake()
        {
            _hud = FindObjectOfType<DebugHUD>();
            if (_hud == null)
            {
                var hudObj = new GameObject("[DebugHUD]");
                _hud = hudObj.AddComponent<DebugHUD>();
            }
            RegisterAllDemos();
        }

        private void Start()
        {
            if (autoStart && _demos.Count > 0)
                EnterDemo(0);
        }

        private void Update()
        {
            _current?.OnUpdate(Time.deltaTime);
            HandleInput();
        }

        private void OnGUI()
        {
            _current?.OnGUI();
        }

        private void OnApplicationQuit()
        {
            _current?.OnExit();
        }

        /// <summary>注册所有章节示例。新增章节时在此追加。</summary>
        private void RegisterAllDemos()
        {
            Register(new Chapter01.InstallationDemo());
            Register(new Chapter03.FirstArchDemo());
            Register(new Chapter04.WorldDemo());
            Register(new Chapter05.EntityDemo());
            Register(new Chapter06.ComponentDemo());
            Register(new Chapter07.ArchetypeDemo());
            Register(new Chapter08.QueryDemo());
            Register(new Chapter09.EventDemo());
            Register(new Chapter10.CommandBufferDemo());
            Register(new Chapter11.MultithreadingDemo());
            Register(new Chapter12.BatchDemo());
            Register(new Chapter13.PerformanceDemo());
            Register(new Chapter14.SystemDemo());
            Register(new Chapter15.SourceGeneratorDemo());
            Register(new Chapter16.LowLevelDemo());
            Register(new Chapter17.RelationshipDemo());
            Register(new Chapter18.PersistenceDemo());
            Register(new Chapter19.EventBusDemo());
            Register(new Chapter20.UnityIntegrationDemo());
            Register(new Chapter21.BestPracticeDemo());
            Register(new Chapter22.DebugDemo());
        }

        private void Register(IDemo demo)
        {
            if (_demos.ContainsKey(demo.Chapter))
            {
                Debug.LogWarning($"[DemoRunner] 章节 {demo.Chapter} 已存在，跳过注册：{demo.GetType().Name}");
                return;
            }
            _demos.Add(demo.Chapter, demo);
        }

        private void HandleInput()
        {
            // F1: 切换 HUD
            if (Input.GetKeyDown(KeyCode.F1))
            {
                _hud.Visible = !_hud.Visible;
                return;
            }

            // 数字键 1-9：直接跳到对应章节（按 _demos 中的索引）
            for (var i = 0; i < 9 && i < _demos.Count; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                {
                    EnterDemo(i);
                    return;
                }
            }

            // Space / N：下一章
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.N))
            {
                EnterDemo((_currentIndex + 1) % _demos.Count);
                return;
            }

            // B / P：上一章
            if (Input.GetKeyDown(KeyCode.B) || Input.GetKeyDown(KeyCode.P))
            {
                EnterDemo((_currentIndex - 1 + _demos.Count) % _demos.Count);
                return;
            }

            // R：重启本章
            if (Input.GetKeyDown(KeyCode.R))
            {
                EnterDemo(_currentIndex);
                return;
            }
        }

        private void EnterDemo(int index)
        {
            if (index < 0 || index >= _demos.Count) return;

            // 退出当前
            try { _current?.OnExit(); }
            catch (Exception e) { Debug.LogError($"[DemoRunner] OnExit 异常：{e}"); }

            _currentIndex = index;
            var demo = _demos.Values[index];
            _current = demo;

            // 更新 HUD
            _hud.CurrentChapter = demo.Chapter;
            _hud.CurrentTitle = demo.Title;
            _hud.CurrentDescription = demo.Description;
            _hud.ClearStatus();

            // 进入新章节
            try
            {
                demo.OnEnter();
                Debug.Log($"[DemoRunner] 进入第 {demo.Chapter} 章：{demo.Title}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DemoRunner] OnEnter 异常：{e}");
                _hud.AddStatus($"<color=red>章节初始化失败：{e.Message}</color>");
            }
        }
    }
}
