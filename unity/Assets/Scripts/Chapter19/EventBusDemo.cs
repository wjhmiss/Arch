using System.Collections.Generic;
using System.Text;
using Arch;
using Arch.Core;
using UnityEngine;

namespace ArchUnityDemo.Chapter19
{
    /// <summary>
    /// 第 19 章演示：用 C# 事件模拟事件总线。
    ///
    /// Arch.EventBus 扩展提供强类型、跨 World 的事件总线支持。
    /// 本 Demo 用普通 C# event/委托手写一个轻量事件总线，演示「发布-订阅」模式的核心思想：
    /// 发布方不关心谁监听，订阅方通过类型过滤自己感兴趣的事件。
    /// 按 E 键发布一个 DamageEvent。
    /// </summary>
    public class EventBusDemo : IDemo
    {
        /// <summary>游戏事件基类</summary>
        public abstract class GameEvent
        {
            public long TimestampMs;
            public abstract string EventType { get; }
        }

        /// <summary>伤害事件（演示用）</summary>
        public sealed class DamageEvent : GameEvent
        {
            public int TargetId;
            public int Amount;

            public override string EventType => "Damage";
        }

        /// <summary>轻量事件总线：用 C# 事件实现，按事件类型分发</summary>
        public sealed class EventBus
        {
            private readonly Dictionary<System.Type, System.Delegate> _handlers = new();

            /// <summary>订阅特定类型的事件</summary>
            public void Subscribe<T>(System.Action<T> handler) where T : GameEvent
            {
                var key = typeof(T);
                if (_handlers.TryGetValue(key, out var existing))
                {
                    _handlers[key] = System.Delegate.Combine(existing, handler);
                }
                else
                {
                    _handlers[key] = handler;
                }
            }

            /// <summary>发布事件，所有该类型的订阅者都会被调用</summary>
            public void Publish<T>(T evt) where T : GameEvent
            {
                if (_handlers.TryGetValue(typeof(T), out var existing) && existing is System.Action<T> typed)
                {
                    typed.Invoke(evt);
                }
            }

            /// <summary>清空所有订阅</summary>
            public void Clear()
            {
                _handlers.Clear();
            }
        }

        private World _world;
        private EventBus _bus;
        private readonly List<string> _history = new();
        private int _publishCount;

        public int Chapter => 19;
        public string Title => "事件总线 EventBus";
        public string Description => "用 C# 事件模拟事件总线 —— 按 E 发布 DamageEvent";

        public void OnEnter()
        {
            _world = World.Create();

            // 创建几个占位实体
            for (var i = 0; i < 3; i++)
            {
                _world.Create<int>(i);
            }

            _bus = new EventBus();
            _bus.Subscribe<DamageEvent>(OnDamage);

            _history.Clear();
            _publishCount = 0;

            Debug.Log($"[Chapter19] World 创建，EventBus 已订阅 DamageEvent");
        }

        private void OnDamage(DamageEvent evt)
        {
            _history.Add($"#{_publishCount} {evt.EventType} → TargetId={evt.TargetId}, Amount={evt.Amount}");
            while (_history.Count > 6) _history.RemoveAt(0);
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null || _bus == null) return;

            if (Input.GetKeyDown(KeyCode.E))
            {
                _publishCount++;
                var evt = new DamageEvent
                {
                    TimestampMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    TargetId = Random.Range(0, 3),
                    Amount = Random.Range(5, 30)
                };
                _bus.Publish(evt);
                Debug.Log($"[Chapter19] 发布 DamageEvent：TargetId={evt.TargetId}, Amount={evt.Amount}");
            }
        }

        public void OnGUI()
        {
            if (_world == null || _bus == null) return;

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}（占位）");
            DebugHUD.Instance.AddStatus($"EventBus 已发布事件数: {_publishCount}");
            DebugHUD.Instance.AddStatus("按 [E] 发布一个 DamageEvent");

            if (_history.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("事件历史:");
                foreach (var line in _history)
                {
                    sb.Append("  ").Append(line).Append('\n');
                }
                // 末尾换行处理
                var text = sb.ToString().TrimEnd('\n');
                DebugHUD.Instance.AddStatus(text);
            }
            else
            {
                DebugHUD.Instance.AddStatus("事件历史: (空，按 E 触发)");
            }

            DebugHUD.Instance.AddStatus("提示: Arch.EventBus 扩展提供跨 World 强类型事件总线");
        }

        public void OnExit()
        {
            _bus?.Clear();
            _bus = null;
            _world?.Dispose();
            _world = null;
            _history.Clear();
        }
    }
}
