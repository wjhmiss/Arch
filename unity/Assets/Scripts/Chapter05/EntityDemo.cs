using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;

namespace ArchUnityDemo.Chapter05
{
    /// <summary>
    /// 第 5 章演示：Entity 的 Id、Version 与相等性比较。
    /// 销毁实体后再创建新实体，会复用 Id 但 Version 递增，从而区分新旧实体。
    /// </summary>
    public class EntityDemo : IDemo
    {
        /// <summary>占位组件（让实体有数据承载）</summary>
        public struct Tag
        {
            public string Label;
        }

        private World _world;
        private Entity _e1;
        private Entity _e2;
        private Entity _e3;
        private Entity _e4;

        public int Chapter => 5;
        public string Title => "Entity 与版本号";
        public string Description => "Entity = Id + Version —— 销毁后复用 Id 但 Version 递增，保证相等性正确";

        public void OnEnter()
        {
            _world = World.Create();

            // 创建 3 个实体
            _e1 = _world.Create<Tag>(new Tag { Label = "e1" });
            _e2 = _world.Create<Tag>(new Tag { Label = "e2" });
            _e3 = _world.Create<Tag>(new Tag { Label = "e3" });

            Debug.Log($"[Chapter05] 创建 e1(Id={_e1.Id},V={_e1.Version}) e2(Id={_e2.Id},V={_e2.Version}) e3(Id={_e3.Id},V={_e3.Version})");

            // 销毁 e2，其 Id 会被回收
            _world.Destroy(_e2);

            // 创建 e4：复用 e2 的 Id，但 Version 递增
            _e4 = _world.Create<Tag>(new Tag { Label = "e4" });

            Debug.Log($"[Chapter05] 销毁 e2 后创建 e4(Id={_e4.Id},V={_e4.Version}) —— Id 复用，Version 不同");
        }

        public void OnUpdate(float deltaTime)
        {
            // 本章聚焦 Entity 结构本身，无每帧逻辑
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"e1: Id={_e1.Id}, Version={_e1.Version}, Alive={_e1.IsAlive()}");
            DebugHUD.Instance.AddStatus($"e2(已销毁): Id={_e2.Id}, Version={_e2.Version}");
            DebugHUD.Instance.AddStatus($"e4: Id={_e4.Id}, Version={_e4.Version} (Id 与 e2 相同，Version 不同)");
            DebugHUD.Instance.AddStatus($"e1 == e3 ?  {_e1 == _e3}   (不同 Id 的实体不相等)");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
