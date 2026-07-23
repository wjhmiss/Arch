using System;
using System.Diagnostics;
using Arch;
using Arch.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArchUnityDemo.Chapter13
{
    /// <summary>
    /// 第 13 章演示：性能对比 —— 单个创建 vs 批量创建。
    /// 同样创建 10000 个带 Position 的实体，对比循环 Create&lt;T&gt; 与批量 Create&lt;T&gt;(int amount)
    /// 两种方式在结构变更上的耗时差异。
    /// </summary>
    public class PerformanceDemo : IDemo
    {
        /// <summary>位置组件</summary>
        public struct Position
        {
            public float X;
            public float Y;
        }

        private World _world;

        /// <summary>每次测试创建的实体数量</summary>
        private const int TestCount = 10000;

        /// <summary>测试次数</summary>
        private int _testRunCount;

        /// <summary>最近一次“单个创建”耗时（毫秒）</summary>
        private double _singleCreateMs;

        /// <summary>最近一次“批量创建”耗时（毫秒）</summary>
        private double _batchCreateMs;

        public int Chapter => 13;
        public string Title => "性能优化 Performance";
        public string Description => "按 Space 对比：循环单个创建 vs 批量创建 10000 实体";

        public void OnEnter()
        {
            _world = World.Create();
            _testRunCount = 0;
            _singleCreateMs = 0;
            _batchCreateMs = 0;
            Debug.Log("[Chapter13] 按 Space 触发性能对比测试");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;
            if (!Input.GetKeyDown(KeyCode.Space)) return;

            try
            {
                _testRunCount++;

                // 测试 1：循环单个创建 TestCount 个实体
                var pos = new Position { X = 0, Y = 0 };
                var sw1 = Stopwatch.StartNew();
                for (var i = 0; i < TestCount; i++)
                {
                    _world.Create<Position>(in pos);
                }
                sw1.Stop();
                _singleCreateMs = sw1.Elapsed.TotalMilliseconds;

                // 清场：销毁 world 重建，避免 Archetype 状态影响第二轮
                _world.Dispose();
                _world = World.Create();

                // 测试 2：批量创建 TestCount 个实体
                var sw2 = Stopwatch.StartNew();
                _world.Create<Position>(TestCount, in pos);
                sw2.Stop();
                _batchCreateMs = sw2.Elapsed.TotalMilliseconds;

                Debug.Log($"[Chapter13] 第 {_testRunCount} 次测试：单个={_singleCreateMs:F4}ms，批量={_batchCreateMs:F4}ms，加速比={(_singleCreateMs / Math.Max(_batchCreateMs, 0.0001)):F2}x");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Chapter13] 性能测试异常：{e}");
            }
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体总数: {_world.Size}");
            DebugHUD.Instance.AddStatus($"测试次数: {_testRunCount}");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus($"<b>测试 1：循环单个创建 {TestCount} 实体</b>");
            DebugHUD.Instance.AddStatus($"  耗时: <color=yellow>{_singleCreateMs:F4} ms</color>");
            DebugHUD.Instance.AddStatus($"<b>测试 2：批量创建 {TestCount} 实体</b>");
            DebugHUD.Instance.AddStatus($"  耗时: <color=green>{_batchCreateMs:F4} ms</color>");
            DebugHUD.Instance.AddStatus(string.Empty);

            if (_batchCreateMs > 0 && _singleCreateMs > 0)
            {
                var speedup = _singleCreateMs / _batchCreateMs;
                DebugHUD.Instance.AddStatus($"加速比: <color=cyan>{speedup:F2}x</color>");
            }

            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("按 [Space] 重新测试");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("批量创建走");
            DebugHUD.Instance.AddStatus("Create<T>(int amount) 路径，");
            DebugHUD.Instance.AddStatus("一次性分配 Chunk 并写入，");
            DebugHUD.Instance.AddStatus("避免逐实体的 Archetype 查找。");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
