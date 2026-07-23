# 第13章 PureECS 与性能优化

## 13.1 概述

前面十二章我们用的一直是 Arch 的**默认模式**：`Entity` 是 12 字节的 struct，含 `Id`、`WorldId`、`Version` 三个字段。`WorldId` 让我们可以从任意 Entity 反查它所属的 World，写出 `entity.Get<T>()` 这样简洁的扩展方法。

但这个便利是有代价的：

- 每个 Entity 多 4 字节，海量实体时缓存压力增加；
- `World._worlds` 是个静态数组，多 World 模式下访问需要查表；
- 扩展方法内部 `World.Worlds[entity.WorldId]` 多一次数组索引。

如果你追求**极致性能**——比如要做几十万实体的模拟、移动端要省每一字节——Arch 提供了 `PURE_ECS` 编译符号，去掉 `WorldId` 字段，把 Entity 压缩到 8 字节。

本章覆盖：

1. PURE_ECS 模式的启用与影响
2. Entity 结构对比（8 vs 12 字节）
3. 限制与编码模式调整
4. 性能优化技巧汇总（缓存、Span、ArrayPool、Chunk 配置等）
5. ChunkLayoutConfig 与 Chunk 大小调优
6. Dangerous Extensions 与基准测试

> 💡 PURE_ECS 不是"更好的模式"，而是"更激进的优化"。如果你不需要省那 4 字节，默认模式更易用。本章大部分优化技巧在两种模式下都适用。

## 13.2 PURE_ECS 模式

### 13.2.1 条件编译开关

打开 [Entity.cs#L1](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L1) 看第一行：

```csharp
#if !PURE_ECS
using Arch.Core.Extensions;
using Arch.Core.Utils;
#endif

namespace Arch.Core;

#if PURE_ECS
// 8 字节版本的 Entity
public readonly struct Entity : IEquatable<Entity>, IComparable<Entity>
{
    public readonly int Id = -1;
    public readonly int Version;
    // ... 无 WorldId
}
#else
// 12 字节版本的 Entity
public readonly struct Entity : IEquatable<Entity>, IComparable<Entity>
{
    public readonly int Id;
    public readonly int WorldId;     // ← 多出来的字段
    public readonly int Version;
}
#endif
```

整个文件用 `#if PURE_ECS / #else / #endif` 包裹，提供两套独立的 Entity 实现。World.cs 中也有几处类似条件编译（例如 `World.Create` 在 PURE_ECS 下不走 `WorldsLock` 静态数组维护）。

### 13.2.2 启用方法

#### 方式 1：.csproj 项目文件

如果你用源码集成 Arch 或自己 fork 编译：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <DefineConstants>$(DefineConstants);PURE_ECS</DefineConstants>
  </PropertyGroup>
</Project>
```

#### 方式 2：Unity Scripting Define Symbols

1. `Edit > Project Settings > Player > Other Settings`
2. 在 `Scripting Define Symbols` 中添加 `PURE_ECS`
3. 点击 Apply，等待 Unity 重新编译

⚠️ 如果用 DLL 方式集成，需要自己用 `dotnet build -c Release-PureEcs`（在 Arch 的 `.csproj` 中加一个 Release-PureEcs 配置）编译带 `PURE_ECS` 符号的 DLL，然后替换到 Unity。

#### 方式 3：Arch 自带的 Release-PureEcs 配置

Arch 仓库的 `Arch.csproj` 中已经预定义了多个构建配置，包括 `Release-PureEcs`。在仓库根目录执行：

```bash
dotnet build -c Release-PureEcs
```

即可生成带 PURE_ECS 的 DLL。

### 13.2.3 与 EVENTS 的关系

`PURE_ECS` 和 `EVENTS` 是两个**独立**的编译符号，可以任意组合：

| 组合 | Entity 大小 | 事件 | 典型场景 |
|------|------------|------|---------|
| 默认 | 12 字节 | 关闭 | 开发调试、单 World |
| 仅 EVENTS | 12 字节 | 开启 | 需要观察者逻辑的项目 |
| 仅 PURE_ECS | 8 字节 | 关闭 | 移动端、海量实体模拟 |
| PURE_ECS + EVENTS | 8 字节 | 开启 | 极端性能 + 事件需求 |

## 13.3 Entity 结构对比

### 13.3.1 字段布局

#### 默认模式（12 字节）

```csharp
public readonly struct Entity
{
    public readonly int Id;        // offset 0, 4 bytes
    public readonly int WorldId;   // offset 4, 4 bytes
    public readonly int Version;   // offset 8, 4 bytes
}
```

#### PURE_ECS 模式（8 字节）

```csharp
public readonly struct Entity
{
    public readonly int Id;        // offset 0, 4 bytes
    public readonly int Version;   // offset 4, 4 bytes
}
```

### 13.3.2 大小影响

`Entity` 是 `readonly struct`，按值传递。每次 `world.Query` 回调、每次 `ParallelQuery` 的 Job 拷贝、每次 `List<Entity>` 存储，都会按 sizeof(Entity) 拷贝。

| 实体数量 | 默认模式内存 | PURE_ECS 内存 | 节省 |
|---------|-------------|--------------|------|
| 10K | 120 KB | 80 KB | 40 KB |
| 100K | 1.2 MB | 800 KB | 400 KB |
| 1M | 12 MB | 8 MB | 4 MB |
| 10M | 120 MB | 80 MB | 40 MB |

10M 实体时省下 40MB 内存，同时每次实体拷贝减少 33% 的字节——CPU 缓存压力同步下降。

### 13.3.3 内部逻辑差异

`Equals` 方法对比：

```csharp
// 默认模式
public bool Equals(Entity other)
{
    return ((Id ^ other.Id) | (WorldId ^ other.WorldId) | (Version ^ other.Version)) == 0;
}

// PURE_ECS 模式
public bool Equals(Entity other)
{
    return ((Id ^ other.Id) | (Version ^ other.Version)) == 0;
}
```

少一个 XOR 和 OR，单次差异微乎其微，但在百万级查询循环里累积效果可观。

`CompareTo` 同样有差异（默认模式多一个 `WorldId << 16`）。

## 13.4 PURE_ECS 的限制

### 13.4.1 无法通过 Entity 直接访问 World

默认模式下，扩展方法 `entity.Get<T>()` 内部是：

```csharp
public static T Get<T>(this Entity entity) where T : struct
{
    var world = World.Worlds[entity.WorldId];  // ← PURE_ECS 下没有 WorldId
    return world.Get<T>(entity);
}
```

PURE_ECS 模式下没有 `WorldId`，这些扩展方法**根本不存在**——它们被 `#if !PURE_ECS` 排除在外。你必须显式持有 World 引用：

```csharp
// ❌ PURE_ECS 下不可用
var pos = entity.Get<Position>();
entity.Set<Position>(new Position { X = 1 });
world.Destroy(entity);  // 这个还能用，因为是 world 上的方法

// ✅ 正确写法
var pos = world.Get<Position>(entity);
world.Set<Position>(entity, new Position { X = 1 });
```

### 13.4.2 多 World 限制

PURE_ECS 下 `World._worlds` 静态数组仍存在，但 `Entity` 不携带 WorldId，**无法**通过 Entity 反查 World。意味着：

- 你不能在闭包里凭一个 Entity 找到它的 World；
- 多 World 场景下必须显式管理"Entity 属于哪个 World"的映射；
- 推荐只用单一 World。

### 13.4.3 编码模式调整

#### 不要再写 entity.Get<T>()

全文搜索你的代码，把所有 `entity.Get`、`entity.Set`、`entity.Has`、`entity.Add` 等扩展方法调用改成 `world.Get(entity)` 等。

#### 闭包中显式捕获 World

```csharp
// 默认模式的写法
_world.Query(in query, (Entity e) =>
{
    var pos = e.Get<Position>();  // 隐式反查 World
});

// PURE_ECS 的写法
var worldRef = _world;  // 显式捕获
_world.Query(in query, (Entity e) =>
{
    var pos = worldRef.Get<Position>(e);  // 显式调用
});
```

#### 不能用 EntityDebugView

默认模式下 Entity 有 `[DebuggerTypeProxy(typeof(EntityDebugView))]`，调试器会显示完整的 World 上下文。PURE_ECS 下这个特性被禁用。

## 13.5 性能优化技巧汇总

### 13.5.1 缓存 QueryDescription

`QueryDescription` 是 struct，但内部包含 `BitSet` 计算和组件类型校验。虽然构造不会分配堆内存，但热路径上重复构造仍有开销。

```csharp
// ❌ 每次 Update 都构造
void Update()
{
    var query = new QueryDescription(all: [typeof(Position), typeof(Velocity)]);
    _world.Query(in query, ...);
}

// ✅ 缓存为静态字段
private static readonly QueryDescription _moveQuery =
    new(all: [typeof(Position), typeof(Velocity)]);

void Update()
{
    _world.Query(in _moveQuery, ...);
}
```

### 13.5.2 使用 IForEach 结构体内联查询

委托版本 `world.Query(in query, (Entity e) => { ... })` 每次可能捕获闭包、产生 delegate 分配。`IForEach<T>` struct 版本零分配且 JIT 可内联：

```csharp
// ❌ 委托版，有闭包分配
_world.Query(in query, (Entity e, ref Position p, ref Velocity v) =>
{
    p.X += v.X * dt;  // dt 被闭包捕获
});

// ✅ struct 版，零分配
public struct MoveJob : IForEach<Position, Velocity>
{
    public float Dt;
    public void Update(ref Position p, ref Velocity v)
    {
        p.X += v.X * Dt;
    }
}

_world.InlineQuery<MoveJob, Position, Velocity>(in query,
    ref new MoveJob { Dt = dt });
```

详见第 11 章对 `InlineParallelQuery<T>` 的讨论。

### 13.5.3 使用 Span<T> 批量 API

`world.Create(Span<Entity>, ...)` 比 `world.Create(ComponentType[])` 更快，因为 `Span<T>` 不需要数组分配。`stackalloc` 在栈上分配最佳：

```csharp
// 适合少量实体
Span<Entity> entities = stackalloc Entity[64];
_world.Create(entities, in signature, 64);

// 大量实体用数组池
Entity[] entities = ArrayPool<Entity>.Shared.Rent(10000);
_world.Create(entities.AsSpan(0, 10000), in signature, 10000);
ArrayPool<Entity>.Shared.Return(entities);
```

### 13.5.4 避免在 Query 回调中分配内存

🔥 这是性能杀手。Query 回调会被调用成千上万次，每次分配 1KB 就会产生 MB 级 GC：

```csharp
// ❌ 每次回调 new 一个 list
_world.Query(in query, (Entity e, ref Inventory inv) =>
{
    var items = new List<Item>(inv.Count);  // ❌ 每次分配
    // ...
});

// ✅ 预分配，复用
var items = new List<Item>(256);  // 在 Query 外分配
_world.Query(in query, (Entity e, ref Inventory inv) =>
{
    items.Clear();
    for (int i = 0; i < inv.Count; i++) items.Add(default);  // ✅ 复用容量
    // ...
});
```

### 13.5.5 使用 ArrayPool

任何需要临时数组的地方都该用 `ArrayPool<T>`：

```csharp
// ❌ 每次 new
var temp = new float[1024];
// ... 用完
// GC 压力

// ✅ ArrayPool
var temp = ArrayPool<float>.Shared.Rent(1024);
try
{
    // ... 用完
}
finally
{
    ArrayPool<float>.Shared.Return(temp);
}
```

Arch 内部大量使用 `Pool<T>.Rent`（自定义池，类似 ArrayPool），见 `world.Create<T>` 的实现。

### 13.5.6 合理的 Chunk 大小

Chunk 是 Archetype 内部分配的内存块，大小直接影响：

- 缓存利用率：太小则元数据占比高，太大则单线程处理一个 Chunk 时间过长；
- 并行度：`ParallelQuery` 按 Chunk 切分 Job，Chunk 数过少则并行度低；
- 内存碎片：太大则 GC/分配器难找连续内存。

Arch 提供 `ChunkLayoutConfig` 调整，见下节。

### 13.5.7 减少 Archetype 碎片化

每个独特的组件组合都是一个 Archetype。如果你的游戏动态生成"具有任意 N 个组件"的实体，可能产生上千个 Archetype，每个只有几个实体——并行度极差。

💡 反碎片化策略：

- 用标记组件（`struct Tag1 {}`）代替布尔字段；
- 设计阶段规划组件组合，限制在几十种以内；
- 必要时合并相似组件为一个"大组件"。

### 13.5.8 用 [SkipLocalsInit]

C# 8+ 可以在方法或整个程序集加 `[SkipLocalsInit]`，跳过栈变量零初始化。Arch 源码随处可见：

```csharp
[SkipLocalsInit]
[StructuralChange]
public void Add<T0, T1>(Entity entity, in T0? t0Component = default, in T1? t1Component = default)
{
    Span<uint> stack = stackalloc uint[BitSet.RequiredLength(ComponentRegistry.Size)];
    // ...
}
```

自己写热路径方法时也可以加这个特性。在 `.csproj` 中加 `<SkipLocalsInit>true</SkipLocalsInit>` 启用整个程序集。

### 13.5.9 用 CommunityToolkit.HighPerformance

Arch 内部用 `CommunityToolkit.HighPerformance` 提供的 `Unsafe.As`、`DangerousGetReferenceAt` 等避开边界检查。你的代码也能用：

```csharp
using CommunityToolkit.HighPerformance;

float[] arr = new float[1024];
ref float first = ref arr.DangerousGetReferenceAt(0);  // 无边界检查
for (int i = 0; i < 1024; i++)
{
    Unsafe.Add(ref first, i) = i;  // 等价于 arr[i] = i，但快
}
```

⚠️ 必须确保索引合法，否则越界不报错直接内存损坏。

## 13.6 ChunkLayoutConfig

源码见 [Settings.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Settings.cs)：

```csharp
public enum ChunkLayoutMode
{
    FixedChunkSize,                // 固定字节数，例如 16KB
    FixedEntityCount,              // 固定实体数，例如 256 个
    MinChunkSizeAndMinEntities     // 软目标：min 16KB 且 min 64 实体
}

public struct ChunkLayoutConfig()
{
    public ChunkLayoutMode LayoutMode { get; set; } = ChunkLayoutMode.MinChunkSizeAndMinEntities;

    public int FixedChunkSize { get; set; } = 32 * 1024;   // 32 KB
    public int FixedEntityCount { get; set; } = 256;

    public int MinChunkSize { get; set; } = 16 * 1024;     // 16 KB
    public int MinEntityCount { get; set; } = 64;
}
```

### 13.6.1 三种布局模式

#### FixedChunkSize

每个 Chunk 固定 32KB（默认），实体数 = 32KB / 单实体组件总大小。优点是内存对齐友好，缺点是组件总和大的实体类型每 Chunk 只能放少量实体，并行度受限。

#### FixedEntityCount

每个 Chunk 固定 256 个实体（默认），字节数随实体大小变化。优点是并行度一致（无论组件多少，每 Chunk 都是 256 个实体），缺点是大组件会产生非常大的 Chunk。

#### MinChunkSizeAndMinEntities

默认模式，"软目标"——同时满足最小 16KB 和最小 64 实体。哪个先达到上限就以哪个为准。这种平衡适合大多数场景。

### 13.6.2 调优建议

| 场景 | 推荐模式 | 说明 |
|------|---------|------|
| 通用游戏 | 默认 (MinChunkSizeAndMinEntities) | 平衡 |
| 大量小实体（粒子） | FixedEntityCount = 512 | 提高并行度 |
| 少量大组件实体 | FixedChunkSize = 64KB | 减少元数据占比 |
| 移动端 | FixedChunkSize = 8KB | 避免大块分配 |
| 极端并行 | FixedEntityCount = 128 | 更多 Chunk，更细粒度并行 |

### 13.6.3 应用配置

`World.Create` 时传入参数（注意：当前版本 `ChunkLayoutConfig` 主要是配置结构体，实际生效路径以源码为准）：

```csharp
var world = World.Create(
    chunkSizeInBytes: 16_384,                  // 16 KB
    minimumAmountOfEntitiesPerChunk: 100,
    archetypeCapacity: 4,
    entityCapacity: 1024
);
```

📖 这四个参数的含义见 [World.cs#L119](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L119) 的 `World.Create` 签名。`entityCapacity` 是 `EntityInfo` 数组的初始大小，`archetypeCapacity` 是 Archetype 字典的初始大小。

## 13.7 Dangerous Extensions

源码见 [DangerousEntityExtensions.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Extensions/Dangerous/DangerousEntityExtensions.cs)：

```csharp
namespace Arch.Core.Extensions.Dangerous;

public static class DangerousEntityExtensions
{
    /// <summary>
    ///     Creates an Entity struct and returns it.
    ///     Does not create an Entity in the world, just the plain struct.
    /// </summary>
    public static Entity CreateEntityStruct(int id, int world, int version)
    {
#if PURE_ECS
        return new Entity(id, 0, version);
#else
        return new Entity(id, world, version);
#endif
    }
}
```

### 13.7.1 用途

`Entity` 的构造函数是 `internal` 的，外部代码不能直接 `new Entity(...)`。但某些高级场景需要构造一个"原始 Entity 值"而不通过 World：

- **序列化/反序列化**：从存档恢复 Entity 引用；
- **网络同步**：从网络包重建 Entity；
- **测试夹具**：单元测试中构造特定 Entity；
- **自定义迭代器**：手动遍历 Chunk 时构造 Entity 返回。

`DangerousEntityExtensions.CreateEntityStruct` 提供这个能力。

### 13.7.2 为什么叫 "Dangerous"

⚠️ 这个 API 不会校验：

- `id` 是否真实存在于 World 中；
- `version` 是否匹配当前 World 中的版本；
- `world`（非 PURE_ECS 模式）是否是有效 World。

如果你构造了一个 `new Entity(99999, 0, 1)`，但 World 中根本没有 id=99999 的实体，后续 `world.Get<T>(entity)` 会读到垃圾数据或崩溃。

### 13.7.3 配套 DangerousWorldExtensions

[DangerousWorldExtensions.cs#L18](file:///d:/Unity/Arch/Arch/src/Arch/Core/Extensions/Dangerous/DangerousWorldExtensions.cs#L18) 提供 `EnsureCapacity(this World, int)` 等扩展：

```csharp
public static void EnsureCapacity(this World world, int capacity)
{
    // 预留 EntityInfo 全局容量，跳过内部多次扩容
}
```

这些 API 命名带 "Dangerous" 是因为它们绕过了部分内部检查，需要调用方确保参数合法。

## 13.8 性能基准 Arch.Benchmarks

Arch 仓库自带 BenchmarkDotNet 基准测试项目，见 [Arch.Benchmarks](file:///d:/Unity/Arch/Arch/src/Arch.Benchmarks)。

### 13.8.1 项目结构

```
Arch.Benchmarks/
├── Arch.Benchmarks.csproj
├── AddRemoveBenchmark.cs             # 增删组件性能
├── ArchetypeIterationBenchmark.cs    # Archetype 迭代
├── ArchetypeIterationTechnqiquesBenchmark.cs  # 不同迭代技巧对比
├── EntityInfoStorageBenchmark.cs     # EntityInfo 存储性能
├── QueryBenchmark.cs                 # 查询性能
├── TryGetBenchmark.cs                # TryGet 性能
└── Utils/Structs.cs                  # 测试用组件
```

### 13.8.2 典型基准示例

参考 [QueryBenchmark.cs](file:///d:/Unity/Arch/Arch/src/Arch.Benchmarks/QueryBenchmark.cs)：

```csharp
[HtmlExporter]
[MemoryDiagnoser]
[HardwareCounters(HardwareCounter.CacheMisses)]
public class QueryBenchmark
{
    [Params(10000, 100000, 1000000)]
    public int Amount;

    private static readonly ComponentType[] _group = { typeof(Transform), typeof(Velocity) };
    private readonly QueryDescription _queryDescription = new(all: _group);
    private static World? _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = World.Create();
        _world.EnsureCapacity(_group, Amount);
        for (var index = 0; index < Amount; index++)
        {
            var entity = _world.Create(_group);
            _world.Set(entity, new Transform { X = 0, Y = 0 }, new Velocity { X = 1, Y = 1 });
        }
    }

    [Benchmark]
    public void WorldEntityQuery()
    {
        _world.Query(in _queryDescription, static (Entity entity) =>
        {
            var refs = _world.Get<Transform, Velocity>(entity);
            refs.t0.X += refs.t1.X;
            refs.t0.Y += refs.t1.Y;
        });
    }
}
```

### 13.8.3 运行基准

在 Arch 仓库根目录：

```bash
cd Arch/src/Arch.Benchmarks
dotnet run -c Release --filter "*QueryBenchmark*"
```

会输出类似：

```
| Method                | Amount  | Mean      | Error     | StdDev    | CacheMisses/Op | Allocated |
|---------------------- |-------- |----------:|----------:|----------:|---------------:|----------:|
| WorldEntityQuery      | 10000   |  0.123 ms | 0.0023 ms | 0.0021 ms |            142 |         - |
| WorldEntityQuery      | 100000  |  1.234 ms | 0.0123 ms | 0.0115 ms |           1420 |         - |
| WorldEntityQuery      | 1000000 | 12.456 ms | 0.1234 ms | 0.1154 ms |          14200 |         - |
```

### 13.8.4 自定义基准

建议为你自己的核心系统写基准测试：

```csharp
[MemoryDiagnoser]
public class MyGameBenchmark
{
    [Params(10000, 100000)] public int EnemyCount;
    private World _world;
    private QueryDescription _enemyQuery;

    [GlobalSetup]
    public void Setup()
    {
        _world = World.Create();
        _world.EnsureCapacity<Position, Velocity, Health>(EnemyCount);
        _world.Create<Position, Velocity, Health>(EnemyCount, default, default, default);
        _enemyQuery = new QueryDescription(all: [typeof(Position), typeof(Velocity), typeof(Health)]);
    }

    [Benchmark(Baseline = true)]
    public void DelegateQuery()
    {
        _world.Query(in _enemyQuery, (Entity e, ref Position p, ref Velocity v) =>
        {
            p.X += v.X;
            p.Y += v.Y;
        });
    }

    [Benchmark]
    public void InlineQuery()
    {
        var job = new IForEachJob<MoveJob> { ForEach = new MoveJob() };
        _world.InlineQuery(in _enemyQuery, in job);
    }

    public struct MoveJob : IForEach<Position, Velocity>
    {
        public void Update(ref Position p, ref Velocity v)
        {
            p.X += v.X;
            p.Y += v.Y;
        }
    }
}
```

🔥 持续基准测试是性能优化的基石。每次改动后跑一遍，确保"优化"没有变成"劣化"。

## 13.9 完整示例

完整示例见 `Assets/Scripts/Chapter13/PerformanceDemo.cs`，演示了 PURE_ECS 模式下的典型优化写法：

```csharp
using System.Buffers;
using System.Diagnostics;
using Arch.Core;
using Arch.Core.Extensions.Dangerous;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class PerformanceDemo : MonoBehaviour
{
    // 组件定义
    private struct Position { public float X, Y; }
    private struct Velocity { public float X, Y; }
    private struct Health { public int Value; }

    // IForEach 结构体 Job —— 零分配
    public struct MoveJob : IForEach<Position, Velocity>
    {
        public float DeltaTime;
        public void Update(ref Position p, ref Velocity v)
        {
            p.X += v.X * DeltaTime;
            p.Y += v.Y * DeltaTime;
        }
    }

    // 缓存的 QueryDescription —— 避免重复构造
    private static readonly QueryDescription _moveQuery =
        new(all: [typeof(Position), typeof(Velocity)]);

    private World _world;
    private const int EntityCount = 100_000;

    private void Start()
    {
        // 1. 创建 World，显式指定容量参数
        _world = World.Create(
            chunkSizeInBytes: 16_384,
            minimumAmountOfEntitiesPerChunk: 100,
            archetypeCapacity: 4,
            entityCapacity: EntityCount
        );

        // 2. 预留 EntityInfo 全局容量
        _world.EnsureCapacity(EntityCount);

        // 3. 预留 Archetype 容量 + 批量创建
        var sw = Stopwatch.StartNew();
        _world.EnsureCapacity<Position, Velocity, Health>(EntityCount);
        _world.Create<Position, Velocity, Health>(
            amount: EntityCount,
            new Position { X = 0, Y = 0 },
            new Velocity { X = Random.Range(-1f, 1f), Y = Random.Range(-1f, 1f) },
            new Health { Value = 100 }
        );
        Debug.Log($"[创建 {EntityCount} 实体] {sw.Elapsed.TotalMilliseconds:F2} ms");

        // 4. 使用 ArrayPool 临时分配
        sw.Restart();
        Entity[] entityBuffer = ArrayPool<Entity>.Shared.Rent(1024);
        try
        {
            // 模拟批量获取部分实体做处理
            // ... 用 entityBuffer
        }
        finally
        {
            ArrayPool<Entity>.Shared.Return(entityBuffer);
        }

        // 5. 用 IForEach 内联查询（每帧调用）
        sw.Restart();
        var job = new IForEachJob<MoveJob>
        {
            ForEach = new MoveJob { DeltaTime = Time.deltaTime }
        };
        _world.InlineQuery(in _moveQuery, in job);
        Debug.Log($"[内联查询 {EntityCount} 实体] {sw.Elapsed.TotalMilliseconds:F2} ms");
    }

    private void Update()
    {
        // 持续运行的内联查询（热路径）
        var job = new IForEachJob<MoveJob>
        {
            ForEach = new MoveJob { DeltaTime = Time.deltaTime }
        };
        _world.InlineQuery(in _moveQuery, in job);
    }

    private void OnDestroy()
    {
        if (_world != null) World.Destroy(_world);
    }
}
```

### 13.9.1 优化清单回顾

| # | 优化点 | 示例中体现 |
|---|-------|----------|
| 1 | 缓存 QueryDescription | `_moveQuery` 静态字段 |
| 2 | 用 IForEach 结构体替代委托 | `MoveJob : IForEach<...>` |
| 3 | 用 ArrayPool 临时分配 | `ArrayPool<Entity>.Shared.Rent` |
| 4 | 预留 Archetype 容量 | `EnsureCapacity<...>` |
| 5 | 预留 EntityInfo 容量 | `_world.EnsureCapacity(EntityCount)` |
| 6 | 批量创建实体 | `world.Create<...>(amount, ...)` |
| 7 | 合理的 World 参数 | `chunkSizeInBytes`、`entityCapacity` |
| 8 | 内联查询无分配 | `InlineQuery` + `IForEachJob<T>` |

## 13.10 进阶优化方向

### 13.10.1 SOA（结构数组）布局

如果你的组件有多个独立的"子字段"，考虑拆分成多个组件：

```csharp
// ❌ AOS 布局：每次只需 X 时也加载 Y、Z
struct Transform { public float X, Y, Z, Scale, Rotation; }

// ✅ SOA 布局：只用 X 时只加载 X
struct PositionX { public float Value; }
struct PositionY { public float Value; }
struct PositionZ { public float Value; }
```

但拆得太碎也会增加 Archetype 数量。权衡取舍。

### 13.10.2 SIMD 向量化

`Span<T>` 提供给你的就是连续内存，配合 `System.Numerics.Vector<T>` 或 `Vector256<float>` 可以做 SIMD：

```csharp
_world.Query(in query, (ref Chunk chunk) =>
{
    var positions = chunk.GetSpan<Position>();  // 假设 Chunk 提供 GetSpan
    var velocities = chunk.GetSpan<Velocity>();
    
    // 用 Vector256<float> 一次处理 8 个 float
    for (int i = 0; i < positions.Length; i += 8)
    {
        // SIMD 加速
    }
});
```

📖 Arch 的 `Chunk` 内部就是数组形式，你可以通过 `chunk.GetFirst<T>()` 拿到首元素引用，再用 `Unsafe.Add` 索引访问，配合 `Span<T>` 包装。

### 13.10.3 对象池化外部资源

ECS 实体本身不持有 Unity 对象（如 GameObject、Material），但你的系统层可能需要为每个实体分配外部资源。用对象池避免运行时分配：

```csharp
var meshPool = new ObjectPool<Mesh>(() => new Mesh(), mesh => mesh.Clear());

_world.Query(in query, (Entity e, ref NeedsMesh n) =>
{
    var mesh = meshPool.Get();
    // ... 用 mesh
    meshPool.Return(mesh);
});
```

## 13.11 本章小结

| 主题 | 关键点 |
|------|--------|
| **PURE_ECS 符号** | 条件编译，去掉 `Entity.WorldId`，省 4 字节 |
| **启用方式** | `.csproj` DefineConstants / Unity Scripting Define Symbols |
| **Entity 大小** | 默认 12 字节，PURE_ECS 8 字节 |
| **PURE_ECS 限制** | 无法用 `entity.Get<T>()` 扩展，必须显式持有 World |
| **缓存 QueryDescription** | 避免重复构造，热路径必备 |
| **IForEach 结构体** | 零分配、可内联，热路径替代委托 |
| **Span<T> 批量 API** | `world.Create(Span<Entity>, ...)` 避免 GC |
| **ArrayPool** | 临时数组用池，`Rent` + `Return` |
| **避免回调内分配** | 不要在 Query 回调里 new 任何东西 |
| **[SkipLocalsInit]** | 跳过栈变量零初始化，热路径加成 |
| **ChunkLayoutConfig** | 三种模式：FixedChunkSize / FixedEntityCount / MinChunkSizeAndMinEntities |
| **World.Create 参数** | `chunkSizeInBytes`、`minimumAmountOfEntitiesPerChunk`、`entityCapacity` |
| **Dangerous Extensions** | `CreateEntityStruct` 构造原始 Entity，绕过内部检查 |
| **Arch.Benchmarks** | BenchmarkDotNet 项目，自带查询/迭代/增删基准 |
| **持续基准** | 每次优化后跑基准，确认收益而非劣化 |
| **SIMD 友好** | Chunk 内连续内存，可配合 `Vector<T>` 进一步加速 |
| **Archetype 碎片化** | 控制组件组合数，避免产生过多空 Archetype |

至此，Unity Arch ECS 框架新手教程的核心部分已经完结。下一章（第 14 章）将介绍 `Arch.System` 系统框架——它把 World 上的查询、批量操作、CommandBuffer 等能力封装成"系统类"，让你用类似 Unity DOTS 的 `SystemBase` 风格组织游戏逻辑。
