using System.Text;
using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter03
{
    /// <summary>
    /// 第 3 章演示：第一个 Arch 程序 —— 冒险者移动。
    /// 创建 5 个带 Position 和 Velocity 的实体，每帧通过 Query 更新位置。
    /// </summary>
    public class FirstArchDemo : IDemo
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
        private Entity[] _entities;
        private QueryDescription _query;

        public int Chapter => 3;
        public string Title => "第一个 Arch 程序";
        public string Description => "冒险者移动 —— 使用 Query 遍历实体并按速度更新位置";

        public void OnEnter()
        {
            _world = World.Create();
            _entities = new Entity[5];
            _query = new QueryDescription().WithAll<Position, Velocity>();

            // 创建 5 个带 Position 和 Velocity 的实体（随机速度）
            for (var i = 0; i < _entities.Length; i++)
            {
                var pos = new Position { X = i * 2f - 4f, Y = 0f };
                var vel = new Velocity
                {
                    Dx = Random.Range(-1.5f, 1.5f),
                    Dy = Random.Range(-1.5f, 1.5f)
                };
                _entities[i] = _world.Create<Position, Velocity>(pos, vel);
            }

            Debug.Log($"[Chapter03] 创建 {_world.Size} 个冒险者实体");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;

            // 用 world.Query(in query, (Entity e, ref Position p, ref Velocity v) => {...}) 更新位置
            _world.Query<Position, Velocity>(in _query, (Entity e, ref Position p, ref Velocity v) =>
            {
                p.X += v.Dx * deltaTime;
                p.Y += v.Dy * deltaTime;

                // 边界反弹，保证实体始终留在可视范围
                if (p.X > 5f || p.X < -5f) { v.Dx = -v.Dx; p.X = Mathf.Clamp(p.X, -5f, 5f); }
                if (p.Y > 3f || p.Y < -3f) { v.Dy = -v.Dy; p.Y = Mathf.Clamp(p.Y, -3f, 3f); }
            });
        }

        public void OnGUI()
        {
            if (_world == null || _entities == null) return;

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}，每帧 Query 更新位置");

            var sb = new StringBuilder();
            for (var i = 0; i < _entities.Length; i++)
            {
                if (!_entities[i].IsAlive()) continue;
                ref var p = ref _entities[i].Get<Position>();
                ref var v = ref _entities[i].Get<Velocity>();
                sb.Append($"#{i} pos=({p.X:F1},{p.Y:F1}) vel=({v.Dx:F1},{v.Dy:F1})  ");
            }
            DebugHUD.Instance.AddStatus(sb.ToString());
            DebugHUD.Instance.AddStatus("提示: 使用 ForEachWithEntity 委托 (Entity, ref T0, ref T1)");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
            _entities = null;
        }
    }
}
