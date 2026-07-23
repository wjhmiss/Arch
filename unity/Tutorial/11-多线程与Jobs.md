# 第11章 多线程与 Jobs

## 11.1 概述

ECS 模式天然适合并行——同质组件数据在 Chunk 内连续存储，多个 Chunk 之间互相独立，是最理想的"数据并行"场景。Arch 提供了基于 `JobScheduler` 的并行查询 API，能让你在几千甚至几十万实体上跑满多核 CPU。

本章将完整介绍：

- 何时使用多线程，何时反而该用单线程
- `World.SharedJobScheduler` 的初始化与生命周期
- `ParallelQuery` / `InlineParallelQuery<T>` 两套并行 API 的差异
- `IForEachJob<T>` 接口的设计意图
- 线程安全规则与 CommandBuffer 配合模式
- 批大小（`batchSize`）与 `RangePartitioner` 的调度原理

> 💡 真正的并行 ECS 不是"无脑多线程"，而是"在正确的粒度上切分工作"。读完本章你应能判断：什么样的查询值得并行，什么样的查询强行多线程反而更慢。

## 11.2 多线程使用场景

### 11.2.1 适合并行的场景

| 场景 | 实体量级 | 说明 |
|------|---------|------|
| AI 决策 | 数千~数万 | 每个实体独立计算行为树/状态机 |
| 粒子模拟 | 数万~数十万 | 位置积分、生命周期衰减 |
| 物理积分 | 数千~数万 | 速度→位置更新，无相互依赖 |
| 视锥剔除 | 数千~数万 | 每个实体的 AABB 独立判断 |
| 寻路请求 | 数百~数千 | 每个实体独立发起 A* |

这些场景的共同点：**每个实体的计算互不依赖**，且工作量大到足以抵消线程调度开销。

### 11.2.2 不适合并行的场景

- **实体数量极少**（< 1000）：线程切换、Job 调度的开销可能比单线程跑还慢。
- **计算极轻**（如只设置一个 bool）：单线程 SIMD 反而更快。
- **需要访问其他实体**（如碰撞检测、邻居查询）：存在数据依赖，并行化需要特殊设计。
- **结构性变更**（Create/Destroy/Add/Remove）：必须串行，通过 CommandBuffer 收集后再主线程统一执行。

### 11.2.3 Amdahl 定律的提醒

假设你的系统 90% 的工作可以并行，10% 必须串行，那么 8 核机器上理论加速上限只有 `1 / (0.1 + 0.9/8) ≈ 4.7x`。ECS 中那 10% 通常是结构变更——这正解释了为什么 CommandBuffer 是并行编程的关键工具。

## 11.3 JobScheduler 与 SharedJobScheduler

### 11.3.1 JobScheduler 简介

Arch 的多线程不直接用 `System.Threading.Tasks.Parallel`，而是依赖 [`ZeroAllocJobScheduler`](https://www.nuget.org/packages/ZeroAllocJobScheduler) 包提供的 `JobScheduler`（命名空间 `Schedulers`）。它是一个轻量级任务调度器，特点：

- 基于工作窃取（work-stealing）线程池
- 支持 `IJob` 接口的依赖图（`JobHandle` 表达依赖关系）
- 池化 `IJob` 实例，减少 GC 压力
- 与 Unity 主线程兼容（不依赖 `UnityEngine.JobHandle`）

### 11.3.2 SharedJobScheduler 静态字段

打开 [World.cs#L107](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L107)，你会看到这个静态属性：

```csharp
/// <summary>
///     The shared static <see cref="JobScheduler"/> used for Multithreading.
/// </summary>
public static JobScheduler? SharedJobScheduler { get; set; }
```

`SharedJobScheduler` 是一个**全局单例**——所有 World 共享同一个调度器。原因：

1. 调度器内部维护线程池，创建多个会浪费线程资源；
2. 跨 World 的并行查询可以共享同一组工作线程；
3. 简化 API：用户无需在每次 `ParallelQuery` 时传入 scheduler。

⚠️ **必须在第一次调用 `ParallelQuery` 之前初始化它**，否则会抛出异常。看 [World.Jobs.cs#L97](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/World.Jobs.cs#L97)：

```csharp
if (SharedJobScheduler is null)
{
    throw new Exception($"SharedJobScheduler is missing, assign an instance to " +
                        $"{nameof(World)}.{nameof(SharedJobScheduler)}. " +
                        "This singleton used for parallel iterations.");
}
```

### 11.3.3 初始化与释放

```csharp
using Schedulers;

// 应用启动时（如 MonoBehaviour 的 Awake）
var scheduler = new JobScheduler(new JobScheduler.Config
{
    ThreadPrefixName = "Arch.Worker",   // 调试可见的线程名
    ThreadCount = 0,                     // 0 = 默认等于 Environment.ProcessorCount
    MaxExpectedConcurrentJobs = 64,      // 最大并发 Job 数
    StrictAllocationMode = false         // 严格模式下不池化，便于调试
});
World.SharedJobScheduler = scheduler;

// 应用退出时（如 MonoBehaviour 的 OnDestroy）
scheduler.Dispose();
World.SharedJobScheduler = null;
```

💡 `ThreadCount = 0` 让调度器自动按逻辑处理器数决定线程数。对于游戏来说，留一个核给主线程/渲染线程通常更稳，可手动设为 `Environment.ProcessorCount - 1`。

📖 更多配置项与内部实现请参考 [ZeroAllocJobScheduler 仓库](https://github.com/genaray/ZeroAllocJobScheduler)。

## 11.4 并行查询 API

源码位于 [World.Jobs.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/World.Jobs.cs) 与 [Jobs.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/Jobs.cs)。

### 11.4.1 ParallelQuery —— 委托版

最简单的并行查询，接收一个 `ForEach` 委托：

```csharp
public void ParallelQuery(in QueryDescription queryDescription, ForEach forEntity)
```

`ForEach` 是 Arch 内置的委托类型，支持多种签名（`Entity`、`ref T0`、`ref T0, ref T1` 等，由源生成器展开）。例如：

```csharp
var query = new QueryDescription(all: [typeof(Position), typeof(Velocity)]);

_world.ParallelQuery(in query, (Entity e, ref Position p, ref Velocity v) =>
{
    p.X += v.X * Time.deltaTime;
    p.Y += v.Y * Time.deltaTime;
});
```

实现（[World.Jobs.cs#L35](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/World.Jobs.cs#L35)）非常薄：

```csharp
public void ParallelQuery(in QueryDescription queryDescription, ForEach forEntity)
{
    var foreachJob = new ForEachJob { ForEach = forEntity };
    InlineParallelChunkQuery(in queryDescription, foreachJob);
}
```

它内部创建一个 `ForEachJob` 结构体（实现 `IChunkJob`），然后转发到 `InlineParallelChunkQuery`。

### 11.4.2 InlineParallelQuery<T> —— 结构体内联版

如果你追求极致性能，应该用这个 API：

```csharp
// 形式 1：通过泛型参数指定 IForEach 实现
public void InlineParallelQuery<T>(in QueryDescription queryDescription)
    where T : struct, IForEach;

// 形式 2：传入 IForEachJob<T> 实例
public void InlineParallelQuery<T>(in QueryDescription queryDescription, in IForEachJob<T> iForEach)
    where T : struct, IForEach;
```

调用方式：

```csharp
// 1. 定义一个 struct 实现 IForEach<T>
public struct VelocityIntegrationJob : IForEach<Position, Velocity>
{
    public float DeltaTime;
    
    public void Update(ref Position p, ref Velocity v)
    {
        p.X += v.X * DeltaTime;
        p.Y += v.Y * DeltaTime;
    }
}

// 2. 调用（编译时实例化，无委托分配）
_world.InlineParallelQuery<VelocityIntegrationJob>(in query);
```

🔥 与委托版的差异：

| 维度 | `ParallelQuery` | `InlineParallelQuery<T>` |
|------|----------------|--------------------------|
| 调用方式 | 传 lambda/委托 | 传 struct 泛型参数 |
| 分配 | 每次调用捕获闭包可能分配 | 零分配 |
| 内联 | 委托调用无法内联 | JIT 可内联 `Update` |
| 灵活性 | 高（可捕获外部变量） | 低（struct 字段传参） |
| 推荐场景 | 原型/调试 | 生产热路径 |

> 💡 注意形式 1 的 `InlineParallelQuery<T>` **不接受外部参数**——`T` 必须有默认构造。如果需要传 DeltaTime 等参数，必须用形式 2 显式传入 `IForEachJob<T>`：

```csharp
var job = new IForEachJob<VelocityIntegrationJob>
{
    ForEach = new VelocityIntegrationJob { DeltaTime = Time.deltaTime }
};
_world.InlineParallelQuery(in query, in job);
```

### 11.4.3 ScheduleInlineParallelChunkQuery —— 异步版

上面两个 API 都是**同步阻塞**的：内部 `handle.Complete()` 等待所有 Job 完成才返回。如果你希望并行查询与主线程其他工作重叠：

```csharp
public JobHandle ScheduleInlineParallelChunkQuery<T>(
    in QueryDescription queryDescription,
    in T innerJob) where T : struct, IChunkJob;
```

返回的 `JobHandle` 可以与其他 Job 组合依赖，稍后统一 `Complete()`。但注意：

- 调用方必须自己确保 `JobHandle` 完成前**不修改**查询涉及的数据；
- 该方法内部不池化 `ChunkIterationJob<T>`，每次 new，会有少量 GC。

## 11.5 IForEachJob 与 IChunkJob 接口

### 11.5.1 接口层级

```
IForEach              ← 用户实现的业务接口（Update(Entity) 或 Update(ref T0, ...)）
   ▲
   │
IForEachJob<T>        ← Arch 内部的 IChunkJob 包装器（struct，存一个 T 实例）
   ▲
   │
IChunkJob             ← 最低层接口，直接处理 Chunk
```

### 11.5.2 IChunkJob —— 直接操作 Chunk

如果你不想按实体粒度迭代，而是想直接操作整个 Chunk（例如批量 SIMD、调用 `chunk.GetSpan<T>()`），可以实现 `IChunkJob`：

```csharp
public interface IChunkJob
{
    public void Execute(ref Chunk chunk);
}
```

参考 [Jobs.cs#L103](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/Jobs.cs#L103) 中 `ForEachJob` 的实现：

```csharp
public struct ForEachJob : IChunkJob
{
    public ForEach ForEach;

    public readonly void Execute(ref Chunk chunk)
    {
        ref var entityFirstElement = ref chunk.Entity(0);
        foreach (var entityIndex in chunk)
        {
            var entity = Unsafe.Add(ref entityFirstElement, entityIndex);
            ForEach(entity);
        }
    }
}
```

可以看到 `chunk` 提供了实体数组的首元素引用，通过 `Unsafe.Add` 索引访问——无边界检查，性能极佳。

### 11.5.3 IForEachJob<T> —— 自动按实体展开

`IForEachJob<T>`（[Jobs.cs#L142](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/Jobs.cs#L142)）是 Arch 提供的便利包装器：

```csharp
public struct IForEachJob<T> : IChunkJob where T : IForEach
{
    public T ForEach;

    public void Execute(ref Chunk chunk)
    {
        ref var entityFirstElement = ref chunk.Entity(0);
        foreach (var entityIndex in chunk)
        {
            var entity = Unsafe.Add(ref entityFirstElement, entityIndex);
            ForEach.Update(entity);
        }
    }
}
```

它实现了 `IChunkJob`，在 `Execute` 里展开每个实体调用 `ForEach.Update(entity)`。`InlineParallelQuery<T>` 内部就用了这个。

💡 还有一个泛型版本 `IForEachJob<T0, T1, ...>` 支持 1~24 个组件参数，由源生成器展开。你实现的 `IForEach<Position, Velocity>` 会被自动适配成对应元数的 Job。

## 11.6 内部调度流程

### 11.6.1 InlineParallelChunkQuery 源码解读

打开 [World.Jobs.cs#L94](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/World.Jobs.cs#L94)，核心逻辑只有 30 行：

```csharp
public void InlineParallelChunkQuery<T>(in QueryDescription queryDescription, in T innerJob)
    where T : struct, IChunkJob
{
    var pool = JobMeta<ChunkIterationJob<T>>.Pool;       // ① 池化 Job 实例
    var query = Query(in queryDescription);

    foreach (var archetype in query.GetArchetypeIterator())
    {
        var archetypeSize = archetype.ChunkCount;
        var part = new RangePartitioner(Environment.ProcessorCount, archetypeSize);

        foreach (var range in part)                       // ② 按 Chunk 切分
        {
            var job = pool.Get();
            job.Start = range.Start;
            job.Size = range.Length;
            job.Chunks = archetype.Chunks;
            job.Instance = innerJob;

            var jobHandle = SharedJobScheduler.Schedule(job);
            JobsCache.Add(job);
            JobHandles.Add(jobHandle);
        }

        var handle = SharedJobScheduler.CombineDependencies(JobHandles.AsSpan());
        SharedJobScheduler.Flush();                       // ③ 提交调度
        handle.Complete();                                // ④ 等待本 Archetype 完成

        for (var index = 0; index < JobsCache.Count; index++)  // ⑤ 归还池
        {
            var job = Unsafe.As<ChunkIterationJob<T>>(JobsCache[index]);
            pool.Return(job);
        }

        JobHandles.Clear();
        JobsCache.Clear();
    }
}
```

关键点：

1. **池化 Job 实例**：通过 `JobMeta<T>.Pool`（[Jobs.cs#L15](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/Jobs.cs#L15)）避免每次 `new ChunkIterationJob<T>`。
2. **按 Archetype 分批**：外层循环遍历匹配的 Archetype，每个 Archetype 内部独立并行。
3. **按 Chunk 切分**：`RangePartitioner` 把 `chunkCount` 切成 `ProcessorCount` 段，每段一个 Job。
4. **同步等待**：每个 Archetype 处理完才进入下一个——保证同一 Archetype 内所有 Job 完成后再继续。
5. **归还池**：Job 完成后归还到 `DefaultObjectPool`，下次复用。

### 11.6.2 RangePartitioner —— 任务切分

`RangePartitioner`（[Enumerators.cs#L515](file:///d:/Unity/Arch/Arch/src/Arch/Core/Enumerators.cs#L515)）是一个 `ref struct`，配合 `foreach` 把 `size` 个元素切成 `threads` 段：

```csharp
public RangePartitioner(int threads, int size)
{
    _threads = threads;
    _size = size;
}
```

例如 16 个 Chunk、4 线程，会切成 `[0..4]`, `[4..8]`, `[8..12]`, `[12..16]` 四段。

> ⚠️ 注意：**Arch 当前没有公开的 `batchSize` 参数**让用户手动指定批大小。`InlineParallelChunkQuery` 内部固定使用 `Environment.ProcessorCount` 作为切分段数。如果你需要更细的批控制，可以自行实现 `IChunkJob` 并通过 `ScheduleInlineParallelChunkQuery` 调度。

### 11.6.3 ChunkIterationJob —— 实际 Job

[Jobs.cs#L172](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/Jobs.cs#L172) 定义了实际被调度的 `IJob`：

```csharp
public sealed class ChunkIterationJob<T> : IJob where T : IChunkJob
{
    public Chunk[] Chunks { get; set; }
    public T? Instance { get; set; }
    public int Size { get; set; }
    public int Start;

    public void Execute()
    {
        ref var chunk = ref Chunks.DangerousGetReferenceAt(Start);
        for (var chunkIndex = 0; chunkIndex < Size; chunkIndex++)
        {
            ref var currentChunk = ref Unsafe.Add(ref chunk, chunkIndex);
            Instance?.Execute(ref currentChunk);
        }
    }
}
```

它持有 Chunk 数组的引用、起始索引、长度，以及一个 `T` 实例。`Execute` 在工作线程上被调度器调用，循环把每个 Chunk 喂给 `Instance.Execute`。

## 11.7 线程安全规则

### 11.7.1 三条铁律

🔥 在 ParallelQuery 回调中：

1. **只读自己 Chunk 内的组件**：通过 `ref T` 拿到的引用只能写自己的，不能写别人的；
2. **绝对不能调用 World 的结构变更 API**：`Create`/`Destroy`/`Add`/`Remove` 全部禁止；
3. **不能在回调中调用其他 Query**：会导致迭代器嵌套，破坏内部状态。

### 11.7.2 只读查询 vs 读写查询

| 操作 | 是否可并行 | 说明 |
|------|-----------|------|
| 读其他实体的组件 | ✅ 安全 | 只要不写 |
| 写自己被查询的组件 | ✅ 安全 | 每个实体只被一个线程处理 |
| 写其他 Archetype 的组件 | ⚠️ 慎重 | 跨 Archetype 时要确保不会撞上同一 Chunk |
| 调用 `world.Get<T>(otherEntity)` | ⚠️ 慎重 | 跨 Chunk 读取，有锁竞争 |
| 调用 `world.Add<T>(e)` | ❌ 禁止 | 结构变更，必须用 CommandBuffer |
| 调用 `world.Destroy(e)` | ❌ 禁止 | 同上 |
| 创建新实体 | ❌ 禁止 | 同上 |

### 11.7.3 使用 CommandBuffer 收集并行修改

回顾第 10 章，ParallelQuery 中需要修改结构时，正确模式是：

```csharp
var cb = new CommandBuffer();

_world.ParallelQuery(in query, (Entity e, ref Health h) =>
{
    if (h.Value <= 0)
    {
        cb.Add<Dead>(e);           // ✅ 线程安全
        cb.Destroy(e);              // ✅ 也可以直接销毁
    }
});

cb.Playback(_world);  // 主线程回放
cb.Dispose();
```

CommandBuffer 的每个公共方法都用 `lock (this)` 保护，多个工作线程可以同时调用。

⚠️ 但**锁本身有竞争**：如果所有线程都频繁写同一个 cb，性能会退化。更好的做法是每个工作线程一个 cb，Playback 时顺序回放。不过 `ParallelQuery` 的委托版不暴露工作线程标识，要做到这点需要实现自定义 `IChunkJob` 并使用 `ScheduleInlineParallelChunkQuery`。

### 11.7.4 计数器与归约

并行累加计数器是常见需求，必须用 `Interlocked`：

```csharp
int totalDead = 0;
_world.ParallelQuery(in query, (Entity e, ref Health h) =>
{
    if (h.Value <= 0)
    {
        Interlocked.Increment(ref totalDead);
    }
});
Debug.Log($"Dead: {totalDead}");
```

💡 更高效的做法是用 `IForEach<T>` struct，每个 Job 内部累加局部计数器，最后归约：

```csharp
public struct DeadCounter : IForEach<Health>
{
    public int Count;  // 局部计数（每个 Job 实例独立）
    
    public void Update(ref Health h)
    {
        if (h.Value <= 0) Count++;
    }
}

var job = new IForEachJob<DeadCounter> { ForEach = new DeadCounter() };
_world.InlineParallelQuery(in query, in job);
// 注意：因为 Job 被池化且每次调度多个实例，归约需要额外设计
```

📖 真实生产中通常每个 Chunk 局部求和，最后主线程归约。这需要自定义 `IChunkJob`，超出本章范围。

## 11.8 批大小与性能调优

### 11.8.1 切分粒度的权衡

Arch 内部固定按 `ProcessorCount` 切分。理论上每个 Job 处理 `chunkCount / processorCount` 个 Chunk。这个选择背后的考量：

| 切分粒度 | 优点 | 缺点 |
|---------|------|------|
| 粗（=CPU核数） | 调度开销小，无工作窃取 | 负载不均，长尾 Job 拖慢整体 |
| 细（每 Chunk 一个 Job） | 负载均衡，工作窃取充分 | 调度开销大，Job 池压力大 |

Arch 选择前者，因为 ECS 中同 Archetype 的 Chunk 大小通常一致，负载天然均衡。但当某个 Archetype 只有 1~2 个 Chunk 时，并行度退化为 1~2，无法跑满多核。

### 11.8.2 多 Archetype 的并行度

由于 `InlineParallelChunkQuery` 是按 Archetype 顺序处理的（外层 `foreach`），**不同 Archetype 之间不会并行**。如果你的查询匹配 5 个 Archetype，每个 1000 Chunk，实际并行度是"1000 Chunk / N 核"，不是 "5000 Chunk / N 核"。

💡 优化策略：

- 减少组件组合碎片化（避免动态生成太多 Archetype）；
- 把高频组件组合"扁平化"（用标记组件代替拆分组件）；
- 必要时拆成多个 ParallelQuery 串联调用。

### 11.8.3 实测建议

🔥 用 BenchmarkDotNet 实测你的实际场景：

```csharp
[Benchmark]
public void Parallel() => _world.ParallelQuery(in _query, (ref Position p, ref Velocity v) =>
{
    p.X += v.X;
    p.Y += v.Y;
});

[Benchmark]
public void SingleThread() => _world.Query(in _query, (ref Position p, ref Velocity v) =>
{
    p.X += v.X;
    p.Y += v.Y;
});
```

经验值：

- 1K 实体：单线程通常更快（调度开销 > 计算节省）
- 10K 实体：并行 2~3x 加速
- 100K+ 实体：并行 4~7x 加速（8 核机器）

### 11.8.4 缓存 QueryDescription

每次 `new QueryDescription(...)` 不会触发分配（它是 struct），但内部会做 `BitSet` 计算和 Hash。热路径应缓存：

```csharp
private static readonly QueryDescription _moveQuery = new(all: [typeof(Position), typeof(Velocity)]);

void Update()
{
    _world.ParallelQuery(in _moveQuery, ...);  // ✅ 复用
}
```

## 11.9 完整示例

完整示例见 `Assets/Scripts/Chapter11/MultithreadingDemo.cs`：

```csharp
using Arch.Buffer;
using Arch.Core;
using Schedulers;
using UnityEngine;

public class MultithreadingDemo : MonoBehaviour
{
    private JobScheduler _scheduler;
    private World _world;

    private struct Position { public float X, Y; }
    private struct Velocity { public float X, Y; }
    private struct Lifetime { public float Value; }
    private struct Dead { }  // 标记组件

    // IForEach 实现：纯数据计算，零分配
    public struct MoveJob : IForEach<Position, Velocity>
    {
        public float DeltaTime;
        
        public void Update(ref Position p, ref Velocity v)
        {
            p.X += v.X * DeltaTime;
            p.Y += v.Y * DeltaTime;
        }
    }

    private void Start()
    {
        // 1. 初始化调度器
        _scheduler = new JobScheduler(new JobScheduler.Config
        {
            ThreadPrefixName = "Arch.Demo",
            ThreadCount = 0,
            MaxExpectedConcurrentJobs = 128,
            StrictAllocationMode = false
        });
        World.SharedJobScheduler = _scheduler;

        _world = World.Create();
        for (int i = 0; i < 50000; i++)
        {
            _world.Create(
                new Position { X = 0, Y = 0 },
                new Velocity { X = Random.Range(-1f, 1f), Y = Random.Range(-1f, 1f) },
                new Lifetime { Value = Random.Range(1f, 5f) }
            );
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        // 2. 并行移动 —— 用 InlineParallelQuery 零分配
        var moveQuery = new QueryDescription(all: [typeof(Position), typeof(Velocity)]);
        var moveJob = new IForEachJob<MoveJob>
        {
            ForEach = new MoveJob { DeltaTime = dt }
        };
        _world.InlineParallelQuery(in moveQuery, in moveJob);

        // 3. 并行衰减生命值，记录死亡实体到 CommandBuffer
        var cb = new CommandBuffer(initialCapacity: 1024);
        var lifetimeQuery = new QueryDescription(all: [typeof(Lifetime)]);

        _world.ParallelQuery(in lifetimeQuery, (Entity e, ref Lifetime l) =>
        {
            l.Value -= dt;
            if (l.Value <= 0f)
            {
                cb.Add<Dead>(e);  // 线程安全
            }
        });

        // 4. 主线程回放结构变更
        cb.Playback(_world);
        cb.Dispose();

        // 5. 单线程清理死亡实体（结构变更不能并行）
        var deadQuery = new QueryDescription(all: [typeof(Dead)]);
        _world.Destroy(deadQuery);  // 批量销毁 API，第 12 章详解
    }

    private void OnDestroy()
    {
        _scheduler?.Dispose();
        if (_world != null) World.Destroy(_world);
    }
}
```

这个例子完整展示了并行 ECS 的典型工作流：

1. **纯数据计算**用 `InlineParallelQuery<T>` 零分配并行；
2. **需要结构变更的部分**通过 CommandBuffer 收集，主线程统一 Playback；
3. **批量结构变更**（如销毁所有 Dead 实体）用 World 的批量 API。

## 11.10 本章小结

| 主题 | 关键点 |
|------|--------|
| **使用场景** | 1K+ 实体的独立计算；Amdahl 上限受串行部分限制 |
| **SharedJobScheduler** | 静态单例，所有 World 共享；必须在 ParallelQuery 前初始化 |
| **初始化** | `new JobScheduler(config)` + `World.SharedJobScheduler = ...` |
| **ParallelQuery** | 委托版，灵活但有闭包分配 |
| **InlineParallelQuery<T>** | struct 泛型版，零分配，热路径首选 |
| **ScheduleInlineParallelChunkQuery** | 异步版，返回 JobHandle 可组合依赖 |
| **IForEach** | 用户实现的业务接口，按实体粒度 |
| **IChunkJob** | 直接操作 Chunk 的低层接口，可批量 SIMD |
| **IForEachJob<T>** | Arch 内部的 IChunkJob 适配器 |
| **RangePartitioner** | 按 `Environment.ProcessorCount` 切分 Chunk |
| **结构变更** | 工作线程中**严禁**直接调用，必须走 CommandBuffer |
| **只读安全** | 同 Archetype 内每个实体只被一个线程处理，写自己安全 |
| **计数器** | 用 `Interlocked.Increment` 或局部求和+归约 |
| **Archetype 串行** | 不同 Archetype 之间不并行，外层 foreach 串行处理 |
| **性能实测** | 1K 单线程更快；10K+ 才有并行收益 |

下一章我们将学习批量操作 API——`world.Add<T>(in QueryDescription, in T)` 这类一次操作整批实体的高效方法。
