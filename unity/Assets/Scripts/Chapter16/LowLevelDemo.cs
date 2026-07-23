using System.Buffers;
using System.Text;
using Arch;
using Arch.Core;
using UnityEngine;

namespace ArchUnityDemo.Chapter16
{
    /// <summary>
    /// 第 16 章演示：低级集合的概念（用 Unity 原生数组模拟）。
    ///
    /// Arch.LowLevel 扩展提供了 UnsafeArray / UnsafeList / UnsafeHashMap 等无 GC 的低级集合，
    /// 用于在热路径中避免托管数组分配。本 Demo 用 System.Buffers.ArrayPool 演示相同的「池化复用」思想：
    /// 频繁申请/释放大块内存时，复用池化数组而非每次 new，能显著降低 GC 压力。
    /// </summary>
    public class LowLevelDemo : IDemo
    {
        private World _world;

        /// <summary>上一次 ArrayPool 租用/归还的记录，仅用于在 OnGUI 上展示</summary>
        private string _lastPoolTrace;

        /// <summary>累加的池化操作次数</summary>
        private int _poolOps;

        public int Chapter => 16;
        public string Title => "低级集合 LowLevel";
        public string Description => "用 ArrayPool 演示池化复用思想 —— Arch.LowLevel 提供 UnsafeArray/UnsafeList";

        public void OnEnter()
        {
            _world = World.Create();

            // 创建几个实体（仅为占位，本章聚焦低级集合）
            for (var i = 0; i < 3; i++)
            {
                _world.Create<int>(i);
            }

            // 演示 ArrayPool 租用与归还
            DemonstrateArrayPool();
        }

        public void OnUpdate(float deltaTime)
        {
            // 本章无每帧逻辑
        }

        /// <summary>
        /// 演示 ArrayPool&lt;T&gt;.Shared 的使用：
        /// 1) 租用一个数组
        /// 2) 写入数据
        /// 3) 归还（复用）
        /// </summary>
        private void DemonstrateArrayPool()
        {
            const int length = 1024;
            var pool = ArrayPool<int>.Shared;
            var buffer = pool.Rent(length);

            try
            {
                // 写入一些数据
                for (var i = 0; i < length; i++)
                {
                    buffer[i] = i * 2;
                }

                var sb = new StringBuilder();
                sb.AppendLine("ArrayPool<int>.Shared.Rent(1024) 成功");
                sb.Append("前 5 个值: ");
                for (var i = 0; i < 5; i++)
                {
                    sb.Append(buffer[i]);
                    if (i < 4) sb.Append(", ");
                }
                _lastPoolTrace = sb.ToString();
                _poolOps++;
            }
            finally
            {
                // 归还时指定实际使用的长度，便于池内部记录
                pool.Return(buffer, clearArray: true);
            }
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体数: {_world.Size}（占位）");
            DebugHUD.Instance.AddStatus("Arch.LowLevel 扩展提供 UnsafeArray / UnsafeList / UnsafeHashMap");
            DebugHUD.Instance.AddStatus("这些集合在热路径中避免托管数组分配，零 GC 开销");
            DebugHUD.Instance.AddStatus($"ArrayPool 演示已执行: {_poolOps} 次");

            if (!string.IsNullOrEmpty(_lastPoolTrace))
            {
                var sb = new StringBuilder();
                sb.Append("池化结果: ");
                sb.Append(_lastPoolTrace);
                DebugHUD.Instance.AddStatus(sb.ToString());
            }

            DebugHUD.Instance.AddStatus("提示: Pool.Rent → 用 → Pool.Return 复用，避免 new T[N] 的 GC");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
            _lastPoolTrace = null;
        }
    }
}
