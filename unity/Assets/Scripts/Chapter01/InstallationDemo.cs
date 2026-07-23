using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter01
{
    /// <summary>
    /// 第 1 章演示：Arch 安装与第一个 ECS 程序。
    /// 演示 World 创建、实体创建、组件添加、查询、销毁的完整生命周期。
    /// </summary>
    public class InstallationDemo : IDemo
    {
        /// <summary>位置组件</summary>
        public struct Position
        {
            public float X;
            public float Y;
            public float Z;
        }

        /// <summary>名称组件</summary>
        public struct Name
        {
            public string Value;
        }

        private World _world;
        private Entity[] _entities;

        public int Chapter => 1;
        public string Title => "安装与入门";
        public string Description => "创建 World、实体、添加组件、查询、销毁 —— 完整的 ECS 生命周期";

        public void OnEnter()
        {
            _world = World.Create();
            _entities = new Entity[3];

            // 创建 3 个带 Position 和 Name 的实体
            _entities[0] = _world.Create<Position, Name>(
                new Position { X = 0f, Y = 0f, Z = 0f },
                new Name { Value = "玩家" });

            _entities[1] = _world.Create<Position, Name>(
                new Position { X = 1f, Y = 2f, Z = 0f },
                new Name { Value = "敌人" });

            _entities[2] = _world.Create<Position, Name>(
                new Position { X = -1f, Y = 0f, Z = 0f },
                new Name { Value = "NPC" });

            // 查询：统计带 Position 的实体数量
            var query = new QueryDescription().WithAll<Position>();
            var count = _world.CountEntities(in query);
            Debug.Log($"[Chapter01] 创建 {_world.Size} 个实体，查询到 {count} 个带 Position 的实体");
        }

        public void OnUpdate(float deltaTime)
        {
            // 仅展示创建，无需每帧更新
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World Id: {_world.Id}，实体数量: {_world.Size}");

            if (_entities != null && _entities.Length > 0 && _entities[0].IsAlive())
            {
                ref var name = ref _entities[0].Get<Name>();
                ref var pos = ref _entities[0].Get<Position>();
                DebugHUD.Instance.AddStatus($"第一个实体名称: {name.Value}");
                DebugHUD.Instance.AddStatus($"第一个实体位置: ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
            }
        }

        public void OnExit()
        {
            // 销毁 World（内部会清理所有实体）
            _world?.Dispose();
            _world = null;
            _entities = null;
        }
    }
}
