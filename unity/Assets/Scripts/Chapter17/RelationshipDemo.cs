using System.Text;
using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter17
{
    /// <summary>
    /// 第 17 章演示：手动实现父子关系（不依赖 Arch.Relationships 扩展）。
    ///
    /// 通过两个组件表达关系：
    ///   - Parent : 记录父实体的 Id（-1 表示无父）
    ///   - ChildCount : 记录子实体数量（冗余缓存，避免每次遍历统计）
    ///
    /// Arch.Relationships 扩展会自动管理这些，本 Demo 用手写方式让读者理解关系数据的本质。
    /// </summary>
    public class RelationshipDemo : IDemo
    {
        /// <summary>父引用组件：ParentId = -1 表示该实体没有父</summary>
        public struct Parent
        {
            public int ParentId;
        }

        /// <summary>子数量缓存组件（父实体上挂载）</summary>
        public struct ChildCount
        {
            public int Count;
        }

        /// <summary>名称组件，仅用于在 HUD 上可读</summary>
        public struct Name
        {
            public string Value;
        }

        private World _world;
        private Entity _parent;
        private Entity[] _children;

        public int Chapter => 17;
        public string Title => "父子关系 Relationship";
        public string Description => "手动实现 Parent/ChildCount 组件 —— 不依赖 Arch.Relationships 扩展";

        public void OnEnter()
        {
            _world = World.Create();

            // 创建 1 个父实体（持有 ChildCount 用于缓存子数量，ParentId = -1 表示无父）
            _parent = _world.Create<Parent, ChildCount, Name>(
                new Parent { ParentId = -1 },
                new ChildCount { Count = 3 },
                new Name { Value = "父节点" });

            // 创建 3 个子实体，每个子实体的 Parent.ParentId = 父实体 Id
            _children = new Entity[3];
            for (var i = 0; i < _children.Length; i++)
            {
                _children[i] = _world.Create<Parent, Name>(
                    new Parent { ParentId = _parent.Id },
                    new Name { Value = $"子节点 #{i}" });
            }

            Debug.Log($"[Chapter17] 父实体 Id={_parent.Id}，子实体数={_children.Length}（手写关系）");
        }

        public void OnUpdate(float deltaTime)
        {
            // 本章聚焦关系数据结构，无每帧逻辑
        }

        public void OnGUI()
        {
            if (_world == null || !_parent.IsAlive()) return;

            ref var pName = ref _parent.Get<Name>();
            ref var pChildCount = ref _parent.Get<ChildCount>();
            ref var pParent = ref _parent.Get<Parent>();

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}");
            DebugHUD.Instance.AddStatus($"父实体: Id={_parent.Id}, Name={pName.Value}, ParentId={pParent.ParentId}, ChildCount={pChildCount.Count}");

            if (_children != null)
            {
                var sb = new StringBuilder();
                sb.Append("子实体: ");
                for (var i = 0; i < _children.Length; i++)
                {
                    if (!_children[i].IsAlive()) continue;
                    ref var cName = ref _children[i].Get<Name>();
                    ref var cParent = ref _children[i].Get<Parent>();
                    sb.Append($"[#{i} Id={_children[i].Id} Name={cName.Value} PId={cParent.ParentId}] ");
                }
                DebugHUD.Instance.AddStatus(sb.ToString());
            }

            DebugHUD.Instance.AddStatus("提示: Arch.Relationships 扩展会自动维护这类组件");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
            _parent = default;
            _children = null;
        }
    }
}
