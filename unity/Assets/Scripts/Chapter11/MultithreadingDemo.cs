using System;
using System.Diagnostics;
using System.Threading;
using Arch;
using Arch.Core;
using Schedulers;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArchUnityDemo.Chapter11
{
    /// <summary>
    /// 第 11 章演示：多线程与 Jobs —— 使用 ParallelQuery 并行处理实体。
    /// 设置 World.SharedJobScheduler 后，world.ParallelQuery 会将查询匹配的实体
    /// 分发到多个 Chunk 上并行执行 ForEach 回调。
    /// </summary>
    public class MultithreadingDemo : IDemo
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
        private JobScheduler _jobScheduler;

        /// <summary>查询描述：所有拥有 Position + Velocity 的实体</summary>
        private QueryDescription _query;

        /// <summary>最近一次并行更新耗时（毫秒）</summary>
        private double _lastElapsedMs;

        /// <summary>最近一次并行更新处理的实体数</summary>
        private int _lastProcessedCount;

        /// <summary>JobScheduler 是否成功初始化</summary>
        private bool _schedulerReady;

        public int Chapter => 11;
        public string Title => "多线程与 Jobs";
        public string Description => "ParallelQuery —— 设置 SharedJobScheduler 后并行更新实体";

        public void OnEnter()
        {
            _world = World.Create();
            _query = new QueryDescription().WithAll<Position, Velocity>();
            _lastElapsedMs = 0;
            _lastProcessedCount = 0;
            _schedulerReady = false;

            // 创建 1000 个随机 Position + Velocity 的实体
            for (var i = 0; i < 1000; i++)
            {
                _world.Create<Position, Velocity>(
                    new Position { X = UnityEngine.Random.Range(-50f, 50f), Y = UnityEngine.Random.Range(-50f, 50f) },
                    new Velocity { Dx = UnityEngine.Random.Range(-1f, 1f), Dy = UnityEngine.Random.Range(-1f, 1f) });
            }

            // 设置 SharedJobScheduler（需 using Schedulers;）
            // JobScheduler 在某些环境可能不可用，用 try-catch 包裹
            try
            {
                _jobScheduler = new JobScheduler(new JobScheduler.Config
                {
                    ThreadPrefixName = "Arch.Demo.Ch11",
                    ThreadCount = 0, // 0 = 自动按处理器数
                    MaxExpectedConcurrentJobs = 64,
                    StrictAllocationMode = false,
                });
                World.SharedJobScheduler = _jobScheduler;
                _schedulerReady = true;
                Debug.Log($"[Chapter11] JobScheduler 初始化成功，创建 {_world.Size} 个实体");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Chapter11] JobScheduler 不可用：{e.Message}");
                _schedulerReady = false;
            }
        }

        public void OnUpdate(float deltaTime)
        {
            if (_world == null) return;
            if (!_schedulerReady) return;

            var processed = 0;
            var sw = Stopwatch.StartNew();

            try
            {
                // ParallelQuery<T0, T1> 会并行调用 ForEach 回调
                // 注意：回调在多线程执行，共享状态需用 Interlocked
                _world.ParallelQuery<Position, Velocity>(in _query,
                    (ref Position pos, ref Velocity vel) =>
                    {
                        pos.X += vel.Dx * deltaTime;
                        pos.Y += vel.Dy * deltaTime;
                        Interlocked.Increment(ref processed);
                    });

                sw.Stop();
                _lastElapsedMs = sw.Elapsed.TotalMilliseconds;
                _lastProcessedCount = processed;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Chapter11] ParallelQuery 异常：{e.Message}");
                _lastElapsedMs = -1;
                _lastProcessedCount = 0;
            }
        }

        public void OnGUI()
        {
            if (_world == null) return;

            DebugHUD.Instance.AddStatus($"World 实体总数: {_world.Size}");
            DebugHUD.Instance.AddStatus($"JobScheduler: {(_schedulerReady ? "<color=green>就绪</color>" : "<color=red>不可用</color>")}");
            DebugHUD.Instance.AddStatus($"最近一次并行更新处理: {_lastProcessedCount} 个实体");
            DebugHUD.Instance.AddStatus($"最近一次并行更新耗时: {_lastElapsedMs:F4} ms");
            DebugHUD.Instance.AddStatus(string.Empty);
            DebugHUD.Instance.AddStatus("说明：");
            DebugHUD.Instance.AddStatus("  World.SharedJobScheduler");
            DebugHUD.Instance.AddStatus("  需在 ParallelQuery 前设置。");
            DebugHUD.Instance.AddStatus("  ParallelQuery 将 Chunk 分发");
            DebugHUD.Instance.AddStatus("  到多个线程并行处理。");
        }

        public void OnExit()
        {
            _world?.Dispose();
            _world = null;

            try
            {
                _jobScheduler?.Dispose();
            }
            catch
            {
                // 忽略 Dispose 异常
            }
            _jobScheduler = null;

            // 清理静态调度器，避免影响其他章节
            World.SharedJobScheduler = null;
        }
    }
}
