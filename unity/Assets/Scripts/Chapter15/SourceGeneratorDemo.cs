using System.Text;
using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter15
{
    /// <summary>
    /// 第 15 章演示：模拟源生成器的效果（手动编写源生成器会生成的代码）。
    ///
    /// Arch.System.SourceGenerator 扩展会根据系统类自动生成强类型的 Query 方法，
    /// 避免手写委托和反射查询。本 Demo 手动实现一个「等价于生成器输出」的查询方法，
    /// 让读者直观理解源生成器做了什么。
    /// </summary>
    public class SourceGeneratorDemo : IDemo
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

        /// <summary>
        /// 模拟源生成器为系统生成的代码。
        ///
        /// 真实场景下，[Arch.System.SourceGenerator] 会扫描带 Query 特性的方法，
        /// 自动生成形如下面的 GetEntities / Update 方法：
        ///     - 缓存 QueryDescription
        ///     - 内联遍历 Chunk
        ///     - 直接通过 chunk.GetFirst&lt;T&gt;() + Unsafe.Add 取组件引用
        /// 从而避免委托调用开销，实现接近手写 for 循环的性能。
        ///
        /// 本类手动实现 Query 方法，等价于源生成器的输出。
        /// </summary>
        public sealed class MovementSystemGenerated
        {
            private readonly World _world;
            // 源生成器会自动生成此字段并缓存查询描述
            private QueryDescription _cachedQuery;

            public MovementSystemGenerated(World world)
            {
                _world = world;
                // 源生成器会根据方法上的 [Query] 特性自动构造此 QueryDescription
                _cachedQuery = new QueryDescription().WithAll<Position, Velocity>();
            }

            /// <summary>
            /// 模拟源生成器会生成的查询方法。
            /// 注意：此处仍调用 world.Query 委托 API 以保持代码简洁。
            /// 真正的源生成器会生成「chunk 内联遍历 + Unsafe.Add 取引用」的代码，
            /// 完全省去委托调用，性能接近手写 for 循环。
            /// </summary>
            public void Update(float dt)
            {
                _world.Query<Position, Velocity>(in _cachedQuery, (ref Position pos, ref Velocity vel) =>
                {
                    pos.X += vel.Dx * dt;
                    pos.Y += vel.Dy * dt;

                    if (pos.X > 5f || pos.X < -5f) { vel.Dx = -vel.Dx; pos.X = Mathf.Clamp(pos.X, -5f, 5f); }
                    if (pos.Y > 3f || pos.Y < -3f) { vel.Dy = -vel.Dy; pos.Y = Mathf.Clamp(pos.Y, -3f, 3f); }
                });
            }

            /// <summary>源生成器会生成此类辅助方法用于统计匹配实体数。</summary>
            public int Count()
            {
                return _world.CountEntities(in _cachedQuery);
            }
        }

        private World _world;
        private MovementSystemGenerated _system;
        private Entity[] _entities;

        public int Chapter => 15;
        public string Title => "源生成器 SourceGenerator";
        public string Description => "模拟源生成器会生成的代码 —— 手写等价的强类型 Query 方法";

        public void OnEnter()
        {
            _world = World.Create();
            _system = new MovementSystemGenerated(_world);
            _entities = new Entity[5];

            for (var i = 0; i < _entities.Length; i++)
            {
                var pos = new Position { X = (i - 2) * 1.5f, Y = 0f };
                var vel = new Velocity
                {
                    Dx = Random.Range(-1.0f, 1.0f),
                    Dy = Random.Range(-1.0f, 1.0f)
                };
                _entities[i] = _world.Create<Position, Velocity>(pos, vel);
            }

            Debug.Log($"[Chapter15] 创建 {_world.Size} 个实体，MovementSystemGenerated 模拟源生成器输出");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null || _system == null) return;

            // 调用「源生成器会生成」的查询方法
            _system.Update(deltaTime);
        }

        public void OnGUI()
        {
            if (_world == null || _system == null) return;

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}，生成方法覆盖: {_system.Count()}");

            // 注释：源生成器会根据 [Query] 特性自动生成此类强类型查询方法，
            // 无需手写 MovementSystemGenerated 类，编译期即可获得最优性能。
            DebugHUD.Instance.AddStatus("// 源生成器输出：缓存 QueryDescription + 内联 Chunk 遍历");

            if (_entities != null)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < _entities.Length; i++)
                {
                    if (!_entities[i].IsAlive()) continue;
                    ref var p = ref _entities[i].Get<Position>();
                    sb.Append($"#{i}=({p.X:F1},{p.Y:F1}) ");
                }
                DebugHUD.Instance.AddStatus(sb.ToString());
            }

            DebugHUD.Instance.AddStatus("提示: Arch.System.SourceGenerator 在编译期生成强类型查询");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
            _system = null;
            _entities = null;
        }
    }
}
