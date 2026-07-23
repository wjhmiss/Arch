using System.Text;
using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter22
{
    /// <summary>
    /// 第 22 章演示：调试技巧。
    ///
    /// Arch 提供多种调试入口：
    ///   - World.ToString() 输出 Id/Capacity/Size
    ///   - World.Archetypes.AsSpan() 遍历所有 Archetype，查看每个原型的组件签名与实体数
    ///   - entity.IsAlive() / entity.Get&lt;T&gt;() 单实体诊断
    ///   - world.CountEntities(in query) 快速统计匹配实体数
    /// 按 D 键将当前 World 的状态完整转储到 Debug.Log，可在 Console 中查看。
    /// </summary>
    public class DebugDemo : IDemo
    {
        public struct Position
        {
            public float X;
            public float Y;
        }

        public struct Velocity
        {
            public float Dx;
            public float Dy;
        }

        public struct Health
        {
            public int Current;
            public int Max;
        }

        public struct Name
        {
            public string Value;
        }

        private World _world;
        private Entity[] _entities;
        private int _dumpCount;

        public int Chapter => 22;
        public string Title => "调试技巧 Debug";
        public string Description => "World 状态转储 —— 按 D 键将 World 完整 Dump 到 Debug.Log";

        public void OnEnter()
        {
            _world = World.Create();
            _entities = new Entity[4];

            _entities[0] = _world.Create<Position, Name>(
                new Position { X = 0f, Y = 0f },
                new Name { Value = "Hero" });

            _entities[1] = _world.Create<Position, Velocity, Name>(
                new Position { X = 1f, Y = 2f },
                new Velocity { Dx = 0.5f, Dy = 0f },
                new Name { Value = "Enemy" });

            _entities[2] = _world.Create<Position, Health, Name>(
                new Position { X = -1f, Y = 1f },
                new Health { Current = 50, Max = 100 },
                new Name { Value = "NPC" });

            _entities[3] = _world.Create<Position, Velocity, Health, Name>(
                new Position { X = 2f, Y = -1f },
                new Velocity { Dx = -0.5f, Dy = 0.3f },
                new Health { Current = 80, Max = 100 },
                new Name { Value = "Boss" });

            Debug.Log($"[Chapter22] 创建 {_world.Size} 个实体（分布在多个 Archetype 中）");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;

            if (Input.GetKeyDown(KeyCode.D))
            {
                DumpWorld();
            }
        }

        /// <summary>把 World 状态完整转储到 Debug.Log，便于在 Unity Console 查看</summary>
        private void DumpWorld()
        {
            _dumpCount++;
            var sb = new StringBuilder();
            sb.AppendLine("=========== World Dump ===========");
            sb.AppendLine(_world.ToString()); // World { Id=..., Capacity=..., Size=... }
            sb.AppendLine($"WorldSize(全局): {World.WorldSize}");
            sb.AppendLine($"ComponentRegistry.Size: {ComponentRegistry.Size}");
            sb.AppendLine($"Archetype 数: {_world.Archetypes.Count}");
            sb.AppendLine("----------------------------------");

            // 遍历所有 Archetype，输出每个原型的组件签名与实体数
            var archetypes = _world.Archetypes;
            var span = archetypes.AsSpan();
            for (var i = 0; i < span.Length; i++)
            {
                var arch = span[i];
                if (arch == null) continue;
                sb.Append($"  Archetype[{i}] 实体数={arch.EntityCount} 组件=[");
                var components = arch.Signature.Components;
                for (var c = 0; c < components.Length; c++)
                {
                    if (c > 0) sb.Append(", ");
                    sb.Append(components[c].Type.Name);
                }
                sb.AppendLine("]");
            }

            sb.AppendLine("----------------------------------");
            sb.AppendLine("实体列表（每行一个）：");
            foreach (var e in _entities)
            {
                if (!e.IsAlive())
                {
                    sb.AppendLine($"  {e} [DEAD]");
                    continue;
                }
                sb.Append($"  {e}");
                var all = e.GetAllComponents();
                for (var i = 0; i < all.Length; i++)
                {
                    sb.Append($"  {all[i]}");
                }
                sb.AppendLine();
            }
            sb.AppendLine("==================================");

            Debug.Log(sb.ToString());
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}，已 Dump 次数: {_dumpCount}");
            DebugHUD.Instance.AddStatus("按 [D] 键将 World 完整状态转储到 Debug.Log");

            if (_entities != null && _entities.Length > 0 && _entities[0].IsAlive())
            {
                ref var n = ref _entities[0].Get<Name>();
                ref var p = ref _entities[0].Get<Position>();
                var sb = new StringBuilder();
                sb.Append($"首实体 #{_entities[0].Id} Name={n.Value} pos=({p.X:F1},{p.Y:F1})");
                DebugHUD.Instance.AddStatus(sb.ToString());
            }

            DebugHUD.Instance.AddStatus("调试入口:");
            DebugHUD.Instance.AddStatus("  World.ToString() / World.Archetypes / entity.IsAlive()");
            DebugHUD.Instance.AddStatus("  world.CountEntities(in query) / entity.GetAllComponents()");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
            _entities = null;
            _dumpCount = 0;
        }
    }
}
