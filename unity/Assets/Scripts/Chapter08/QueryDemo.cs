using Arch;
using Arch.Core;
using UnityEngine;

namespace ArchUnityDemo.Chapter08
{
    /// <summary>
    /// 第 8 章演示：Query 查询系统 —— 通过 WithAll / WithNone / WithAny 等过滤条件匹配实体。
    /// 创建 10 个不同组件组合的实体，演示 4 种查询方式各自匹配的实体数量。
    /// </summary>
    public class QueryDemo : IDemo
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
        }

        /// <summary>敌人标记组件</summary>
        public struct Enemy
        {
            public bool IsBoss;
        }

        private World _world;

        // 4 种查询的匹配数量（由 OnUpdate 计算，OnGUI 显示）
        private int _countAllPositionVelocity;
        private int _countPositionWithoutVelocity;
        private int _countPositionWithEnemyOrVelocity;
        private int _countAllPositionVelocityEnemy;

        public int Chapter => 8;
        public string Title => "查询 Query";
        public string Description => "WithAll / WithNone / WithAny —— 用不同过滤条件匹配实体";

        public void OnEnter()
        {
            _world = World.Create();

            // 5 个 Position + Velocity
            for (var i = 0; i < 5; i++)
            {
                _world.Create<Position, Velocity>(
                    new Position { X = i, Y = 0 },
                    new Velocity { Dx = 1, Dy = 0 });
            }

            // 3 个 Position + Health（无 Velocity）
            for (var i = 0; i < 3; i++)
            {
                _world.Create<Position, Health>(
                    new Position { X = i, Y = 1 },
                    new Health { Current = 100 });
            }

            // 2 个 Position + Velocity + Enemy
            for (var i = 0; i < 2; i++)
            {
                _world.Create<Position, Velocity, Enemy>(
                    new Position { X = i, Y = 2 },
                    new Velocity { Dx = 0, Dy = 1 },
                    new Enemy { IsBoss = i == 0 });
            }

            Debug.Log($"[Chapter08] 创建 {_world.Size} 个实体，分布 {_world.Archetypes.Count} 个 Archetype");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;

            // 查询 1：WithAll<Position, Velocity> —— 必须同时拥有 Position 和 Velocity
            var q1 = new QueryDescription().WithAll<Position, Velocity>();
            _countAllPositionVelocity = _world.CountEntities(in q1);

            // 查询 2：WithAll<Position>.WithNone<Velocity> —— 有 Position 但无 Velocity
            var q2 = new QueryDescription().WithAll<Position>().WithNone<Velocity>();
            _countPositionWithoutVelocity = _world.CountEntities(in q2);

            // 查询 3：WithAll<Position>.WithAny<Enemy, Velocity> —— 有 Position 且至少有 Enemy 或 Velocity 之一
            // WithAny<T>() 单次只设置一个 T，要同时指定多个 Any 用构造器形式
            var q3 = new QueryDescription(
                all: new Signature(typeof(Position)),
                any: new Signature(typeof(Enemy), typeof(Velocity))
            );
            _countPositionWithEnemyOrVelocity = _world.CountEntities(in q3);

            // 查询 4：WithAll<Position, Velocity, Enemy> —— 三者全有
            var q4 = new QueryDescription().WithAll<Position, Velocity, Enemy>();
            _countAllPositionVelocityEnemy = _world.CountEntities(in q4);
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体总数: {_world.Size}");
            DebugHUD.Instance.AddStatus($"Archetype 数: {_world.Archetypes.Count}");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("查询结果：");
            DebugHUD.Instance.AddStatus($"  1) WithAll<Position, Velocity>            = {_countAllPositionVelocity}");
            DebugHUD.Instance.AddStatus($"  2) WithAll<Position>.WithNone<Velocity>   = {_countPositionWithoutVelocity}");
            DebugHUD.Instance.AddStatus($"  3) WithAll<Position>.WithAny<Enemy, Vel>  = {_countPositionWithEnemyOrVelocity}");
            DebugHUD.Instance.AddStatus($"  4) WithAll<Position, Velocity, Enemy>     = {_countAllPositionVelocityEnemy}");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("预期: 7 / 3 / 7 / 2");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
