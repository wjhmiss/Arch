using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter06
{
    /// <summary>
    /// 第 6 章演示：组件的添加、获取、设置、移除与查询。
    /// 每秒减少 Health.Current 1 点以演示 Set；同时展示 ComponentRegistry 注册的组件总数。
    /// </summary>
    public class ComponentDemo : IDemo
    {
        /// <summary>生命组件</summary>
        public struct Health
        {
            public int Current;
            public int Max;
        }

        /// <summary>攻击组件</summary>
        public struct Attack
        {
            public int Damage;
        }

        private World _world;
        private Entity _eHealthOnly;
        private Entity _eHealthAndAttack;
        private float _timer;

        public int Chapter => 6;
        public string Title => "组件 Component";
        public string Description => "组件的 Add/Get/Set/Remove/Has —— 每秒扣血演示 Set";

        public void OnEnter()
        {
            _world = World.Create();
            _timer = 0f;

            // 实体 1：只有 Health
            _eHealthOnly = _world.Create<Health>(new Health { Current = 100, Max = 100 });

            // 实体 2：有 Health 和 Attack
            _eHealthAndAttack = _world.Create<Health, Attack>(
                new Health { Current = 80, Max = 100 },
                new Attack { Damage = 15 });

            // 演示 Has / Get / Add / Remove
            Debug.Log($"[Chapter06] e2.Has<Attack>={_eHealthAndAttack.Has<Attack>()}, e1.Has<Attack>={_eHealthOnly.Has<Attack>()}");

            // 给 e1 临时 Add 一个 Attack，再 Remove，演示结构变化
            _eHealthOnly.Add(new Attack { Damage = 5 });
            Debug.Log($"[Chapter06] Add 后 e1.Has<Attack>={_eHealthOnly.Has<Attack>()}");
            _eHealthOnly.Remove<Attack>();
            Debug.Log($"[Chapter06] Remove 后 e1.Has<Attack>={_eHealthOnly.Has<Attack>()}");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;

            // 每秒减少 Health.Current 1 点（演示 Set）
            _timer += deltaTime;
            if (_timer < 1f) return;
            _timer -= 1f;

            DecreaseHealth(_eHealthOnly);
            DecreaseHealth(_eHealthAndAttack);
        }

        private void DecreaseHealth(Entity e)
        {
            if (!e.IsAlive() || !e.Has<Health>()) return;
            ref var h = ref e.Get<Health>();
            var updated = h;
            updated.Current = Mathf.Max(0, updated.Current - 1);
            // 用 Set 显式写回组件，演示 Set API
            e.Set(updated);
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"ComponentRegistry.Size: {ComponentRegistry.Size} (全局已注册组件数)");

            if (_eHealthOnly.IsAlive())
            {
                ref var h = ref _eHealthOnly.Get<Health>();
                DebugHUD.Instance.AddStatus($"e1(仅Health): {h.Current}/{h.Max}");
            }

            if (_eHealthAndAttack.IsAlive())
            {
                ref var h = ref _eHealthAndAttack.Get<Health>();
                ref var a = ref _eHealthAndAttack.Get<Attack>();
                DebugHUD.Instance.AddStatus($"e2(Health+Attack): {h.Current}/{h.Max}, Damage={a.Damage}");
            }
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
