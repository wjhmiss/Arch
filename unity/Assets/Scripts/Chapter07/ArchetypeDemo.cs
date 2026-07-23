using System.Text;
using Arch;
using Arch.Core;
using UnityEngine;

namespace ArchUnityDemo.Chapter07
{
    /// <summary>
    /// 第 7 章演示：Archetype 概念 —— 相同组件组合的实体归入同一原型。
    /// 创建多组不同组件组合的实体，遍历 World 中所有 Archetype 展示其组件签名与实体数量。
    /// </summary>
    public class ArchetypeDemo : IDemo
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

        /// <summary>生命组件</summary>
        public struct Health
        {
            public int Current;
            public int Max;
        }

        private World _world;

        public int Chapter => 7;
        public string Title => "原型 Archetype";
        public string Description => "相同组件组合的实体归入同一 Archetype —— 遍历 World 的所有原型";

        public void OnEnter()
        {
            _world = World.Create();

            // 3 个只有 Position 的实体
            for (var i = 0; i < 3; i++)
            {
                _world.Create<Position>(new Position { X = i, Y = 0 });
            }

            // 2 个有 Position + Velocity 的实体
            for (var i = 0; i < 2; i++)
            {
                _world.Create<Position, Velocity>(
                    new Position { X = i, Y = 1 },
                    new Velocity { Dx = 1, Dy = 0 });
            }

            // 1 个有 Position + Velocity + Health 的实体
            _world.Create<Position, Velocity, Health>(
                new Position { X = 0, Y = 2 },
                new Velocity { Dx = 0, Dy = 1 },
                new Health { Current = 100, Max = 100 });

            Debug.Log($"[Chapter07] 创建 {_world.Size} 个实体，分布在 {_world.Archetypes.Count} 个 Archetype 中");
        }

        public void OnUpdate(float deltaTime)
        {
            // 本章聚焦 Archetype 静态结构，无每帧逻辑
        }

        public void OnGUI()
        {
            if (_world == null) return;

            var archetypes = _world.Archetypes;
            DebugHUD.Instance.AddStatus($"World 实体总数: {_world.Size}，Archetype 数: {archetypes.Count}");

            var span = archetypes.AsSpan();
            for (var i = 0; i < span.Length; i++)
            {
                var arch = span[i];
                if (arch == null || arch.EntityCount == 0) continue;

                var sb = new StringBuilder();
                sb.Append($"  [{i}] 实体数={arch.EntityCount}  组件=[");
                var components = arch.Signature.Components;
                for (var c = 0; c < components.Length; c++)
                {
                    if (c > 0) sb.Append(", ");
                    sb.Append(components[c].Type.Name);
                }
                sb.Append("]");

                DebugHUD.Instance.AddStatus(sb.ToString());
            }
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
