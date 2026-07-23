using System.Text;
using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter14
{
    /// <summary>
    /// 第 14 章演示：手动实现系统模式（不依赖 Arch.System 扩展）。
    /// 通过定义一个简单的 MovementSystem 类，封装 Query 逻辑，演示「系统」概念的本质：
    /// 「系统 = 对 World 中符合某种组件签名的实体集合进行批量处理的逻辑单元」。
    /// </summary>
    public class SystemDemo : IDemo
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
        /// 简单的移动系统：手动实现「系统」概念，不依赖 Arch.System 扩展。
        /// 一个系统通常包含：目标 World、QueryDescription（缓存）、Update 方法。
        /// </summary>
        public sealed class MovementSystem
        {
            private readonly World _world;
            // 缓存 QueryDescription，避免每帧重复构造
            private readonly QueryDescription _query;

            public MovementSystem(World world)
            {
                _world = world;
                _query = new QueryDescription().WithAll<Position, Velocity>();
            }

            /// <summary>每帧调用：遍历所有 Position+Velocity 实体，按速度更新位置。</summary>
            public void Update(World world, float dt)
            {
                // 使用 world.Query 委托式 API 批量更新
                world.Query<Position, Velocity>(in _query, (ref Position pos, ref Velocity vel) =>
                {
                    pos.X += vel.Dx * dt;
                    pos.Y += vel.Dy * dt;

                    // 简单边界反弹，保证实体始终留在可视范围
                    if (pos.X > 5f || pos.X < -5f) { vel.Dx = -vel.Dx; pos.X = Mathf.Clamp(pos.X, -5f, 5f); }
                    if (pos.Y > 3f || pos.Y < -3f) { vel.Dy = -vel.Dy; pos.Y = Mathf.Clamp(pos.Y, -3f, 3f); }
                });
            }

            public int CountEntities()
            {
                return _world.CountEntities(in _query);
            }
        }

        private World _world;
        private MovementSystem _movementSystem;
        private Entity[] _entities;

        public int Chapter => 14;
        public string Title => "系统模式 System";
        public string Description => "手动实现系统模式 —— 封装 Query 与更新逻辑，不依赖 Arch.System 扩展";

        public void OnEnter()
        {
            _world = World.Create();
            _movementSystem = new MovementSystem(_world);
            _entities = new Entity[10];

            for (var i = 0; i < _entities.Length; i++)
            {
                var pos = new Position { X = (i - 5) * 0.8f, Y = 0f };
                var vel = new Velocity
                {
                    Dx = Random.Range(-1.5f, 1.5f),
                    Dy = Random.Range(-1.5f, 1.5f)
                };
                _entities[i] = _world.Create<Position, Velocity>(pos, vel);
            }

            Debug.Log($"[Chapter14] 创建 {_world.Size} 个实体，实例化 MovementSystem（手动实现，非 Arch.System 扩展）");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null || _movementSystem == null) return;

            // 调用系统的 Update：所有系统逻辑都被封装在系统类中
            _movementSystem.Update(_world, deltaTime);
        }

        public void OnGUI()
        {
            if (_world == null || _movementSystem == null) return;

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}，系统覆盖: {_movementSystem.CountEntities()}");
            DebugHUD.Instance.AddStatus("系统结构: MovementSystem(World, QueryDescription, Update)");

            if (_entities != null && _entities.Length > 0 && _entities[0].IsAlive())
            {
                ref var p = ref _entities[0].Get<Position>();
                ref var v = ref _entities[0].Get<Velocity>();
                var sb = new StringBuilder();
                sb.Append($"实体#0 pos=({p.X:F2},{p.Y:F2}) vel=({v.Dx:F2},{v.Dy:F2})");
                DebugHUD.Instance.AddStatus(sb.ToString());
            }

            DebugHUD.Instance.AddStatus("提示: Arch.System 扩展提供基类，但系统本质=「World+Query+Update」");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
            _movementSystem = null;
            _entities = null;
        }
    }
}
