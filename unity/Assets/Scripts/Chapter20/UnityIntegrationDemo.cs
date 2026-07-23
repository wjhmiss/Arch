using System.Collections.Generic;
using System.Text;
using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using ArchUnityDemo;
using UnityEngine;

namespace ArchUnityDemo.Chapter20
{
    /// <summary>
    /// 第 20 章演示：MonoBehaviour 与 Arch 的桥接。
    ///
    /// 真实生产环境推荐使用 Arch.Unity 扩展，它提供 GameObjectEntityBridge 等组件自动同步。
    /// 本 Demo 用一个 Dictionary 维护 Entity → GameObject 的映射，
    /// 演示手动桥接的核心思路：
    ///   - 实体作为数据源（Position）
    ///   - GameObject 作为表现层（Cube）
    ///   - 每帧通过 Query 把 Entity 的 Position 同步到 GameObject.transform.position
    /// </summary>
    public class UnityIntegrationDemo : IDemo
    {
        /// <summary>位置组件</summary>
        public struct Position
        {
            public float X;
            public float Y;
            public float Z;
        }

        /// <summary>Unity 可视化引用（GameObject 实例 id）</summary>
        public struct Visual
        {
            public int InstanceId;
        }

        /// <summary>
        /// 演示用 MonoBehaviour 桥接器（不会真正挂载，仅作代码示范）。
        /// 真实场景下，此类会在 Awake 时创建 World、订阅 update 同步实体到 GameObject。
        /// </summary>
        public class GameMainBehaviour : MonoBehaviour
        {
            public World World;
            public QueryDescription MoveQuery;

            private void Awake()
            {
                World = World.Create();
                MoveQuery = new QueryDescription().WithAll<Position>();
            }

            private void Update()
            {
                if (World == null) return;
                // 真实桥接逻辑会在此同步实体 → GameObject
            }

            private void OnDestroy()
            {
                World?.Dispose();
                World = null;
            }
        }

        private World _world;
        private readonly Dictionary<Entity, GameObject> _views = new();
        private QueryDescription _query;
        private Entity[] _entityBuffer;

        public int Chapter => 20;
        public string Title => "Unity 集成桥接";
        public string Description => "MonoBehaviour + Arch 手动桥接 —— 实体驱动 GameObject";

        public void OnEnter()
        {
            _world = World.Create();
            _query = new QueryDescription().WithAll<Position, Visual>();

            // 创建 4 个实体并对应 GameObject
            _entityBuffer = new Entity[4];
            var colors = new[]
            {
                new Color(1f, 0.4f, 0.4f),
                new Color(0.4f, 1f, 0.4f),
                new Color(0.4f, 0.6f, 1f),
                new Color(1f, 1f, 0.4f)
            };

            for (var i = 0; i < _entityBuffer.Length; i++)
            {
                var pos = new Position
                {
                    X = (i - 1.5f) * 1.5f,
                    Y = 0f,
                    Z = 0f
                };

                var entity = _world.Create<Position, Visual>(pos, new Visual());
                _entityBuffer[i] = entity;

                // 用 VisualFactory 创建对应的 GameObject（同时挂载到视图集合）
                var go = VisualFactory.CreateCube(
                    new Vector3(pos.X, pos.Y, pos.Z),
                    colors[i],
                    name: $"Entity_{entity.Id}");

                // 记录 GameObject 实例 id 到 Visual 组件，便于反查
                ref var visual = ref entity.Get<Visual>();
                visual.InstanceId = go.GetInstanceID();

                _views[entity] = go;
            }

            Debug.Log($"[Chapter20] 创建 {_world.Size} 个实体并桥接到 GameObject");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;

            // 通过 Query 遍历所有 Position+Visual 实体，把 Position 同步到对应 GameObject
            _world.Query<Position, Visual>(in _query, (Entity e, ref Position pos, ref Visual vis) =>
            {
                // 简单的来回往复动画：基于时间偏移
                pos.Y = Mathf.Sin(Time.time * 2f + e.Id) * 0.5f;
                pos.X += Mathf.Cos(Time.time * 0.5f + e.Id) * deltaTime * 0.2f;

                // 把位置写回到对应 GameObject
                if (_views.TryGetValue(e, out var go) && go != null)
                {
                    go.transform.position = new Vector3(pos.X, pos.Y, pos.Z);
                }
            });
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}，桥接 GameObject: {_views.Count}");

            var sb = new StringBuilder();
            var i = 0;
            foreach (var kv in _views)
            {
                if (!kv.Key.IsAlive()) continue;
                ref var pos = ref kv.Key.Get<Position>();
                ref var vis = ref kv.Key.Get<Visual>();
                sb.Append($"#{i} E={kv.Key.Id} pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) vis={vis.InstanceId}  ");
                i++;
            }
            if (sb.Length > 0)
            {
                DebugHUD.Instance.AddStatus(sb.ToString());
            }

            DebugHUD.Instance.AddStatus($"GameMainBehaviour 定义于脚本（演示用，未挂载到场景）");
            DebugHUD.Instance.AddStatus("提示: Arch.Unity 扩展提供生产级 GameObjectEntityBridge");
        }

        public void OnExit()
        {
            // 销毁所有 GameObject
            foreach (var kv in _views)
            {
                if (kv.Value != null)
                {
                    Object.Destroy(kv.Value);
                }
            }
            _views.Clear();

            _world?.Dispose();
            _world = null;
            _entityBuffer = null;
        }
    }
}
