using System.Diagnostics;
using System.Text;
using Arch;
using Arch.Core;
using Arch.Core.Extensions;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArchUnityDemo.Chapter21
{
    /// <summary>
    /// 第 21 章演示：最佳实践 vs 反例。
    ///
    /// goodWorld 演示推荐做法：
    ///   - 使用 struct 组件（避免引用类型组件导致的 GC 与 boxing）
    ///   - 在系统构造时缓存 QueryDescription（不每帧 new）
    ///   - 用 world.Create(Span, in Signature, amount) 批量创建，减少重复结构变更
    ///
    /// badWorld 仅演示反例代码（不实际执行 class 组件创建，因为 Arch 默认面向 struct），
    /// 但会演示「每帧 new QueryDescription」这一可执行的反例，用于直观对比性能差异。
    /// </summary>
    public class BestPracticeDemo : IDemo
    {
        // ===== goodWorld 使用的 struct 组件 =====
        public struct GoodPosition
        {
            public float X;
            public float Y;
        }

        public struct GoodVelocity
        {
            public float Dx;
            public float Dy;
        }

        // ===== badWorld 演示用的 struct 组件（保持与 good 同结构以便公平对比 QueryDescription 开销） =====
        // 注：Arch 推荐 struct 组件。class 组件会破坏内存连续性与缓存友好性，
        // 因此本 Demo 不实际用 class 创建实体，仅在注释中说明该反例。
        public struct BadPosition
        {
            public float X;
            public float Y;
        }

        public struct BadVelocity
        {
            public float Dx;
            public float Dy;
        }

        private World _goodWorld;
        private World _badWorld;

        // goodWorld 缓存的 QueryDescription（仅构造一次）
        private QueryDescription _goodQuery;

        // 累计耗时（毫秒），用于 OnGUI 对比展示
        private long _goodTotalMs;
        private long _badTotalMs;
        private long _goodUpdateFrames;
        private long _badUpdateFrames;

        private const int GoodBatch = 100;
        private const int BadBatch = 100;

        public int Chapter => 21;
        public string Title => "最佳实践";
        public string Description => "struct + 缓存 QueryDescription + 批量创建 vs 每帧 new QueryDescription";

        public void OnEnter()
        {
            // ===== goodWorld：最佳实践 =====
            _goodWorld = World.Create();
            _goodQuery = new QueryDescription().WithAll<GoodPosition, GoodVelocity>();

            // 批量创建：用 world.Create(Span, in Signature, amount) 一次完成
            var goodEntities = new Entity[GoodBatch];
            _goodWorld.Create(goodEntities, new Signature(typeof(GoodPosition), typeof(GoodVelocity)), GoodBatch);
            // 初始化组件数据
            foreach (var e in goodEntities)
            {
                e.Set(new GoodPosition { X = Random.Range(-3f, 3f), Y = 0f });
                e.Set(new GoodVelocity { Dx = Random.Range(-1f, 1f), Dy = Random.Range(-1f, 1f) });
            }

            // ===== badWorld：反例 =====
            _badWorld = World.Create();

            // 反例注释（不实际执行 class 组件）：
            //   public class BadPositionClass { public float X; public float Y; }
            //   public class BadVelocityClass { public float Dx; public float Dy; }
            //   _badWorld.Create<BadPositionClass, BadVelocityClass>(...);
            // 这样会导致组件存储为引用类型，破坏 Archetype 内存连续性，引发 GC 与缓存失效。

            // badWorld 仍使用 struct 组件（演示「每帧 new QueryDescription」反例）
            for (var i = 0; i < BadBatch; i++)
            {
                _badWorld.Create<BadPosition, BadVelocity>(
                    new BadPosition { X = Random.Range(-3f, 3f), Y = 0f },
                    new BadVelocity { Dx = Random.Range(-1f, 1f), Dy = Random.Range(-1f, 1f) });
            }

            Debug.Log($"[Chapter21] goodWorld: {GoodBatch} 实体（批量创建+缓存Query）；badWorld: {BadBatch} 实体（每帧 new Query）");
        }

        public void OnUpdate(float deltaTime)
        {
            if (_goodWorld != null)
            {
                var sw = Stopwatch.StartNew();

                // 用缓存的 _goodQuery 遍历，零分配
                _goodWorld.Query<GoodPosition, GoodVelocity>(in _goodQuery, (ref GoodPosition p, ref GoodVelocity v) =>
                {
                    p.X += v.Dx * deltaTime;
                    p.Y += v.Dy * deltaTime;
                });

                sw.Stop();
                _goodTotalMs += sw.ElapsedTicks * 1000L / Stopwatch.Frequency;
                _goodUpdateFrames++;
            }

            if (_badWorld != null)
            {
                var sw = Stopwatch.StartNew();

                // 反例：每帧 new QueryDescription（产生分配 + 内部 hash 计算）
                var badQuery = new QueryDescription().WithAll<BadPosition, BadVelocity>();
                _badWorld.Query<BadPosition, BadVelocity>(in badQuery, (ref BadPosition p, ref BadVelocity v) =>
                {
                    p.X += v.Dx * deltaTime;
                    p.Y += v.Dy * deltaTime;
                });

                sw.Stop();
                _badTotalMs += sw.ElapsedTicks * 1000L / Stopwatch.Frequency;
                _badUpdateFrames++;
            }
        }

        public void OnGUI()
        {
            if (_goodWorld == null || _badWorld == null) return;

            DebugHUD.Instance.AddStatus("== goodWorld（推荐做法）==");
            DebugHUD.Instance.AddStatus($"实体数: {_goodWorld.Size}，缓存 QueryDescription");
            DebugHUD.Instance.AddStatus($"更新累计: {_goodTotalMs} μs / {_goodUpdateFrames} 帧");
            DebugHUD.Instance.AddStatus("struct 组件 + 批量创建 + 缓存 QueryDescription");

            DebugHUD.Instance.AddStatus("== badWorld（反例）==");
            DebugHUD.Instance.AddStatus($"实体数: {_badWorld.Size}，每帧 new QueryDescription");
            DebugHUD.Instance.AddStatus($"更新累计: {_badTotalMs} μs / {_badUpdateFrames} 帧");
            DebugHUD.Instance.AddStatus("// class 组件会破坏 Archetype 内存连续性，引发 GC 与缓存失效");
            DebugHUD.Instance.AddStatus("// 每帧 new QueryDescription 会产生分配 + 内部 hash 计算");

            var ratio = _badTotalMs == 0 ? 0 : (double)_badTotalMs / System.Math.Max(1L, _goodTotalMs);
            var sb = new StringBuilder();
            sb.Append($"反例/推荐 倍率: {ratio:F2}x（μs 比值，仅供参考）");
            DebugHUD.Instance.AddStatus(sb.ToString());
        }

        public void OnExit()
        {
            _goodWorld?.Dispose();
            _badWorld?.Dispose();
            _goodWorld = null;
            _badWorld = null;
            _goodQuery = default;
            _goodTotalMs = 0;
            _badTotalMs = 0;
            _goodUpdateFrames = 0;
            _badUpdateFrames = 0;
        }
    }
}
