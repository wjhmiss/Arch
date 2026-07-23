using System;

namespace ArchUnityDemo
{
    /// <summary>
    /// 所有章节演示的统一接口。
    /// 每个章节示例脚本需要实现此接口，由 DemoRunner 统一调度。
    /// </summary>
    public interface IDemo
    {
        /// <summary>章节编号（如 1, 2, 3...）</summary>
        int Chapter { get; }

        /// <summary>章节标题</summary>
        string Title { get; }

        /// <summary>章节简介（显示在 HUD 上）</summary>
        string Description { get; }

        /// <summary>进入章节时调用（可在此创建 World、生成实体等）</summary>
        void OnEnter();

        /// <summary>每帧更新（可在此调用 world.Query）</summary>
        void OnUpdate(float deltaTime);

        /// <summary>OnGUI 绘制（绘制章节专属调试信息）</summary>
        void OnGUI();

        /// <summary>退出章节时调用（必须在此 Dispose World）</summary>
        void OnExit();
    }
}
