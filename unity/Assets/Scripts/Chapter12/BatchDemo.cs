using System;
using Arch;
using Arch.Core;
using UnityEngine;

namespace ArchUnityDemo.Chapter12
{
    /// <summary>
    /// 第 12 章演示：批量与批量操作 —— 用一次性 API 大幅降低结构变更开销。
    /// world.Create(Span&lt;Entity&gt;, Signature, int) 一次性创建一批实体，
    /// world.Add&lt;T&gt;(in QueryDescription) 一次性给查询匹配的实体添加组件。
    /// </summary>
    public class BatchDemo : IDemo
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

        /// <summary>每批次创建的实体数量</summary>
        private const int BatchCount = 1000;

        /// <summary>已执行的批次数</summary>
        private int _batchCount;

        public int Chapter => 12;
        public string Title => "批量操作 Batch";
        public string Description => "按 Space 批量创建 1000 实体并批量添加 Velocity 组件";

        public void OnEnter()
        {
            _world = World.Create();
            _batchCount = 0;
            Debug.Log("[Chapter12] 按 Space 触发批量创建 + 批量添加组件");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;
            if (!Input.GetKeyDown(KeyCode.Space)) return;

            try
            {
                // 1) 批量创建 1000 个仅带 Position 的实体
                //    使用 world.Create(Span<Entity>, in Signature, int amount)
                var entities = new Entity[BatchCount];
                var signature = new Signature(typeof(Position));
                _world.Create(entities, in signature, BatchCount);

                // 2) 批量添加 Velocity 组件到所有"有 Position 但无 Velocity"的实体
                //    使用 world.Add<T>(in QueryDescription, in T?)
                var query = new QueryDescription().WithAll<Position>().WithNone<Velocity>();
                _world.Add<Velocity>(in query, new Velocity { Dx = 1f, Dy = 0f });

                _batchCount++;
                Debug.Log($"[Chapter12] 第 {_batchCount} 批：创建 {BatchCount} 实体并批量添加 Velocity，当前实体数={_world.Size}，Archetype 数={_world.Archetypes.Count}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Chapter12] 批量操作异常：{e}");
            }
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体总数: {_world.Size}");
            DebugHUD.Instance.AddStatus($"Archetype 数: {_world.Archetypes.Count}");
            DebugHUD.Instance.AddStatus($"已执行批次数: {_batchCount}");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("按 [Space] 触发一批：");
            DebugHUD.Instance.AddStatus($"  - Create(Span<Entity>, Signature, {BatchCount})");
            DebugHUD.Instance.AddStatus("  - Add<Velocity>(in QueryDescription)");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("批量 API 一次性移动整个");
            DebugHUD.Instance.AddStatus("Archetype，避免逐实体复制，");
            DebugHUD.Instance.AddStatus("结构变更开销大幅降低。");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
