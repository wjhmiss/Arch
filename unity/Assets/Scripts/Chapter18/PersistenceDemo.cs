using System.Collections.Generic;
using System.Text;
using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter18
{
    /// <summary>
    /// 第 18 章演示：手动序列化 World 状态到 JSON（用 Unity 的 JsonUtility）。
    ///
    /// Arch.Persistence 扩展提供完整的 World 持久化支持。
    /// 本 Demo 手动遍历实体，构造可序列化 DTO，使用 Unity 内置 JsonUtility 序列化为字符串。
    /// 按 S 键序列化当前 World，按 L 键从最近一次序列化的 JSON 反序列化并重建 World。
    /// </summary>
    public class PersistenceDemo : IDemo
    {
        /// <summary>位置组件</summary>
        public struct Position
        {
            public float X;
            public float Y;
        }

        /// <summary>生命组件</summary>
        public struct Health
        {
            public int Current;
            public int Max;
        }

        /// <summary>可序列化的位置 DTO（JsonUtility 不支持嵌套泛型/struct 字段直传）</summary>
        [System.Serializable]
        public class PositionDto
        {
            public float X;
            public float Y;
        }

        /// <summary>可序列化的生命 DTO</summary>
        [System.Serializable]
        public class HealthDto
        {
            public int Current;
            public int Max;
        }

        /// <summary>单个实体的可序列化包装</summary>
        [System.Serializable]
        public class EntityDto
        {
            public int Id;
            public PositionDto Position;
            public HealthDto Health;
        }

        /// <summary>World 快照（顶层包装）</summary>
        [System.Serializable]
        public class WorldSnapshot
        {
            public int EntityCount;
            public List<EntityDto> Entities = new();
        }

        private World _world;
        private Entity[] _entities;
        private string _lastJson;
        private string _lastLoadLog;

        public int Chapter => 18;
        public string Title => "持久化 Persistence";
        public string Description => "手动序列化 World 到 JSON（JsonUtility）—— 按 S 存档 / 按 L 读档";

        public void OnEnter()
        {
            _world = World.Create();
            _entities = new Entity[3];

            _entities[0] = _world.Create<Position, Health>(
                new Position { X = 1.5f, Y = 2.5f },
                new Health { Current = 100, Max = 100 });

            _entities[1] = _world.Create<Position, Health>(
                new Position { X = -3.0f, Y = 0.0f },
                new Health { Current = 60, Max = 100 });

            _entities[2] = _world.Create<Position, Health>(
                new Position { X = 0.0f, Y = -1.5f },
                new Health { Current = 30, Max = 80 });

            Debug.Log($"[Chapter18] 创建 {_world.Size} 个 Position+Health 实体，等待 S/L 按键");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;

            if (Input.GetKeyDown(KeyCode.S))
            {
                SerializeWorld();
            }
            else if (Input.GetKeyDown(KeyCode.L))
            {
                DeserializeWorld();
            }
        }

        /// <summary>把当前 World 中所有 Position+Health 实体序列化为 JSON 字符串</summary>
        private void SerializeWorld()
        {
            var snapshot = new WorldSnapshot();
            var query = new QueryDescription().WithAll<Position, Health>();
            var count = _world.CountEntities(in query);

            var buffer = new Entity[count];
            _world.GetEntities(in query, buffer);

            foreach (var e in buffer)
            {
                if (!e.IsAlive()) continue;
                ref var pos = ref e.Get<Position>();
                ref var hp = ref e.Get<Health>();
                snapshot.Entities.Add(new EntityDto
                {
                    Id = e.Id,
                    Position = new PositionDto { X = pos.X, Y = pos.Y },
                    Health = new HealthDto { Current = hp.Current, Max = hp.Max }
                });
            }
            snapshot.EntityCount = snapshot.Entities.Count;

            _lastJson = JsonUtility.ToJson(snapshot, true);
            Debug.Log($"[Chapter18] 序列化完成：{snapshot.EntityCount} 个实体，JSON 长度 {_lastJson.Length}");
        }

        /// <summary>从最近一次序列化的 JSON 反序列化，销毁原实体后重建</summary>
        private void DeserializeWorld()
        {
            if (string.IsNullOrEmpty(_lastJson))
            {
                _lastLoadLog = "无可加载的存档（先按 S 序列化）";
                Debug.LogWarning("[Chapter18] 无可加载的存档");
                return;
            }

            var snapshot = JsonUtility.FromJson<WorldSnapshot>(_lastJson);
            if (snapshot == null || snapshot.Entities == null)
            {
                _lastLoadLog = "反序列化失败";
                return;
            }

            // 销毁当前所有实体
            for (var i = 0; i < _entities.Length; i++)
            {
                if (_entities[i].IsAlive())
                {
                    _world.Destroy(_entities[i]);
                }
            }

            // 按快照重建实体
            _entities = new Entity[snapshot.Entities.Count];
            for (var i = 0; i < snapshot.Entities.Count; i++)
            {
                var dto = snapshot.Entities[i];
                _entities[i] = _world.Create<Position, Health>(
                    new Position { X = dto.Position.X, Y = dto.Position.Y },
                    new Health { Current = dto.Health.Current, Max = dto.Health.Max });
            }

            _lastLoadLog = $"已加载 {snapshot.EntityCount} 个实体（Id 已重新分配）";
            Debug.Log($"[Chapter18] 反序列化完成：重建 {snapshot.EntityCount} 个实体");
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}");
            DebugHUD.Instance.AddStatus("按 [S] 序列化当前 World 到 JSON");
            DebugHUD.Instance.AddStatus("按 [L] 从 JSON 反序列化重建实体");

            if (!string.IsNullOrEmpty(_lastLoadLog))
            {
                DebugHUD.Instance.AddStatus($"加载: {_lastLoadLog}");
            }

            if (!string.IsNullOrEmpty(_lastJson))
            {
                // 截断显示 JSON，避免溢出 HUD
                var preview = _lastJson.Length > 80 ? _lastJson.Substring(0, 80) + "..." : _lastJson;
                DebugHUD.Instance.AddStatus($"JSON({ _lastJson.Length } 字符): { preview }");
            }

            DebugHUD.Instance.AddStatus("提示: Arch.Persistence 扩展提供完整 World 持久化");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
            _entities = null;
            _lastJson = null;
            _lastLoadLog = null;
        }
    }
}
