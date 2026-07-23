using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter04
{
    /// <summary>
    /// 第 4 章演示：World 的生命周期与多 World 支持。
    /// 创建两个独立的 World（worldA、worldB），演示它们各自维护独立的实体集合。
    /// </summary>
    public class WorldDemo : IDemo
    {
        /// <summary>位置组件（用于演示实体数据）</summary>
        public struct Position
        {
            public float X;
            public float Y;
        }

        private World _worldA;
        private World _worldB;

        public int Chapter => 4;
        public string Title => "World 与多 World";
        public string Description => "World 是实体的容器 —— 演示多 World 独立维护各自的实体集合";

        public void OnEnter()
        {
            _worldA = World.Create();
            _worldB = World.Create();

            // worldA 创建 2 个实体
            _worldA.Create<Position>(new Position { X = 1f, Y = 1f });
            _worldA.Create<Position>(new Position { X = 2f, Y = 2f });

            // worldB 创建 2 个实体（与 worldA 完全独立）
            _worldB.Create<Position>(new Position { X = 10f, Y = 10f });
            _worldB.Create<Position>(new Position { X = 20f, Y = 20f });

            Debug.Log($"[Chapter04] worldA.Id={_worldA.Id} 实体数={_worldA.Size}；worldB.Id={_worldB.Id} 实体数={_worldB.Size}");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_worldA == null || _worldB == null) return;

            // 演示 worldA 中的实体可访问：查询 worldA 中所有 Position 实体
            var queryA = new QueryDescription().WithAll<Position>();
            var countA = _worldA.CountEntities(in queryA);

            // worldB 独立：同样查询但互不影响
            var queryB = new QueryDescription().WithAll<Position>();
            var countB = _worldB.CountEntities(in queryB);

            // 用日志体现独立性（仅偶发输出避免刷屏）
            if (Time.frameCount % 300 == 0)
            {
                Debug.Log($"[Chapter04] worldA 可访问实体 {countA} 个，worldB 独立持有 {countB} 个");
            }
        }

        public void OnGUI()
        {
            if (_worldA == null || _worldB == null) return;

            DebugHUD.Instance.AddStatus($"World.WorldSize(全局): {World.WorldSize}");
            DebugHUD.Instance.AddStatus($"worldA.Id={_worldA.Id}  实体数={_worldA.Size}");
            DebugHUD.Instance.AddStatus($"worldB.Id={_worldB.Id}  实体数={_worldB.Size}");
            DebugHUD.Instance.AddStatus("两个 World 实体集合相互独立、互不影响");
        }

        public void OnExit()
        {
            _worldA?.Dispose();
            _worldB?.Dispose();
            _worldA = null;
            _worldB = null;
        }
    }
}
