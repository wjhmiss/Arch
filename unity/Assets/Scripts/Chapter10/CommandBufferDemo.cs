using System;
using Arch;
using Arch.Buffer;
using Arch.Core;
using UnityEngine;

namespace ArchUnityDemo.Chapter10
{
    /// <summary>
    /// 第 10 章演示：CommandBuffer 命令缓冲 —— 延迟执行结构变更。
    /// 录制 Add/Create 等结构变更命令，统一在 Playback 时一次性应用到 World，
    /// 避免在查询迭代过程中进行结构变更导致的迭代器失效问题。
    /// </summary>
    public class CommandBufferDemo : IDemo
    {
        /// <summary>位置组件</summary>
        public struct Position
        {
            public float X;
            public float Y;
        }

        /// <summary>速度组件</summary>
        public struct Velocity
        {
            public float Dx;
            public float Dy;
        }

        private World _world;

        /// <summary>触发间隔（秒）</summary>
        private const float TriggerInterval = 2.0f;

        /// <summary>累计时间</summary>
        private float _timer;

        /// <summary>已触发次数</summary>
        private int _triggerCount;

        /// <summary>最近一次 Playback 的实体数</summary>
        private int _lastEntityCount;

        /// <summary>最近一次 Playback 的 Velocity 实体数</summary>
        private int _lastVelocityCount;

        public int Chapter => 10;
        public string Title => "命令缓冲 CommandBuffer";
        public string Description => "延迟执行结构变更 —— 录制 Add/Create，统一 Playback";

        public void OnEnter()
        {
            _world = World.Create();
            _timer = 0f;
            _triggerCount = 0;
            _lastEntityCount = 0;
            _lastVelocityCount = 0;

            // 创建 5 个带 Position 的实体（暂无 Velocity）
            for (var i = 0; i < 5; i++)
            {
                _world.Create<Position>(new Position { X = i, Y = 0 });
            }

            Debug.Log($"[Chapter10] 创建 {_world.Size} 个带 Position 的实体");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;

            _timer += deltaTime;
            if (_timer < TriggerInterval) return;
            _timer = 0f;
            _triggerCount++;

            try
            {
                // 1) 创建 CommandBuffer
                var cb = new CommandBuffer();

                // 2) 查询所有“有 Position 但无 Velocity”的实体
                var query = new QueryDescription().WithAll<Position>().WithNone<Velocity>();
                var matchCount = _world.CountEntities(in query);
                if (matchCount > 0)
                {
                    var entities = new Entity[matchCount];
                    _world.GetEntities(in query, entities);

                    // 3) 用 cb 给每个匹配实体录制 Add<Velocity> 命令
                    foreach (var entity in entities)
                    {
                        cb.Add<Velocity>(in entity, new Velocity { Dx = 1, Dy = 0.5f });
                    }
                }

                // 4) 同时用 cb 录制一个新建实体的命令（演示 CommandBuffer.Create）
                cb.Create(new[] { typeof(Position) });

                // 5) 统一 Playback，一次性应用到 World
                cb.Playback(_world);

                // 更新统计
                _lastEntityCount = _world.Size;
                var velQuery = new QueryDescription().WithAll<Velocity>();
                _lastVelocityCount = _world.CountEntities(in velQuery);

                Debug.Log($"[Chapter10] 第 {_triggerCount} 次触发：Playback 完成，实体数={_lastEntityCount}，Velocity 实体数={_lastVelocityCount}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Chapter10] CommandBuffer 异常：{e}");
            }
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体总数: {_world.Size}");
            var velQuery = new QueryDescription().WithAll<Velocity>();
            DebugHUD.Instance.AddStatus($"带 Velocity 的实体数: {_world.CountEntities(in velQuery)}");
            DebugHUD.Instance.AddStatus($"触发次数: {_triggerCount}（每 {TriggerInterval:F1}s 一次）");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("流程：");
            DebugHUD.Instance.AddStatus("  1. new CommandBuffer()");
            DebugHUD.Instance.AddStatus("  2. cb.Add<Velocity>(entity, ...)");
            DebugHUD.Instance.AddStatus("  3. cb.Create([typeof(Position)])");
            DebugHUD.Instance.AddStatus("  4. cb.Playback(world)");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("CommandBuffer 适用于在");
            DebugHUD.Instance.AddStatus("查询迭代中收集结构变更，");
            DebugHUD.Instance.AddStatus("迭代结束后统一 Playback。");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
