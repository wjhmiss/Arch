using System;
using Arch;
using Arch.Core;
using Arch.Core.Events;
using UnityEngine;

namespace ArchUnityDemo.Chapter09
{
    /// <summary>
    /// 第 9 章演示：事件系统 Events —— 概念性演示。
    /// Arch 的事件系统需要编译时定义 EVENTS 符号才真正生效；
    /// 默认情况下 Subscribe* 方法存在但为空操作，回调不会被触发。
    /// 本 Demo 在运行时通过订阅回调并触发一次组件添加来检测事件是否启用。
    /// </summary>
    public class EventDemo : IDemo
    {
        /// <summary>生命组件</summary>
        public struct Health
        {
            public int Current;
            public int Max;
        }

        private World _world;

        /// <summary>运行时检测到事件系统是否启用</summary>
        private bool _eventsEnabled;

        /// <summary>检测过程中回调是否被触发</summary>
        private bool _callbackFired;

        public int Chapter => 9;
        public string Title => "事件系统 Events";
        public string Description => "概念性演示 —— 需启用 EVENTS 编译符号才会触发回调";

        public void OnEnter()
        {
            _world = World.Create();
            _eventsEnabled = false;
            _callbackFired = false;

            // 创建 2 个实体并添加 Health 组件
            // 若 EVENTS 启用，SubscribeComponentAdded<Health> 注册的回调会在添加 Health 时触发
            try
            {
                _world.SubscribeComponentAdded<Health>(OnComponentAdded);

                // 触发一次组件添加，用于检测事件是否真的生效
                _world.Create<Health>(new Health { Current = 80, Max = 100 });
                _world.Create<Health>(new Health { Current = 50, Max = 100 });

                // 若回调被触发过，说明 EVENTS 符号已启用
                _eventsEnabled = _callbackFired;
            }
            catch (Exception e)
            {
                // 未启用 EVENTS 时理论上 API 仍存在但为空操作；
                // 此处兜底捕获任何潜在异常（例如不同版本 API 差异）
                Debug.LogWarning($"[Chapter09] 事件订阅异常：{e.Message}");
                _eventsEnabled = false;
            }

            Debug.Log($"[Chapter09] 事件系统启用状态: {_eventsEnabled}");
        }

        public void OnUpdate(float deltaTime)
        {
            // 本章为概念性演示，OnUpdate 无操作
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体总数: {_world.Size}");
            DebugHUD.Instance.AddStatus(string.Empty);
            var status = _eventsEnabled
                ? "<color=green>已启用（回调正常触发）</color>"
                : "<color=red>未启用（回调未触发）</color>";
            DebugHUD.Instance.AddStatus($"事件系统状态: {status}");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("说明：");
            DebugHUD.Instance.AddStatus("  Arch 事件系统默认未启用。");
            DebugHUD.Instance.AddStatus("  需在编译时定义 EVENTS 符号");
            DebugHUD.Instance.AddStatus("  （在 .csproj 或 Unity 的");
            DebugHUD.Instance.AddStatus("   Scripting Define Symbols 中添加）。");
            DebugHUD.Instance.AddStatus("  启用后，SubscribeComponentAdded<T>");
            DebugHUD.Instance.AddStatus("  等回调才会在结构变更时触发。");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
        }

        /// <summary>组件添加回调 —— 仅在 EVENTS 符号启用时被触发</summary>
        private void OnComponentAdded(in Entity entity, ref Health comp)
        {
            _callbackFired = true;
        }
    }
}
