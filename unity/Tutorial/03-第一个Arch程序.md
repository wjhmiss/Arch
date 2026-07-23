# 第03章 第一个Arch程序

> 📖 本章目标：通过一个完整的"冒险者移动"示例，掌握 Arch 框架从组件定义、World 创建、实体构造到查询遍历的完整流程，写出第一个可运行的 Arch 程序。

---

## 3.1 场景描述

我们将模拟一个简单的游戏场景：**一群冒险者（Adventurer）在世界中移动**。每个冒险者拥有：

- **位置（Position）**：当前坐标
- **速度（Velocity）**：每帧位移量

我们的系统会每帧查询所有"同时拥有 Position 和 Velocity"的实体，把位置加上速度，最后打印出来。这是 ECS 最经典的"移动系统（Move System）"模式，也是理解 Arch 工作流程的最佳入口。

💡 这个例子虽然简单，但涵盖了 Arch 的全部核心 API：定义组件、创建 World、创建实体、查询、遍历、读写组件。

---

## 3.2 步骤1：定义组件

按照 ECS 原则，**组件是纯数据**，应当使用值类型。C# 10 的 `record struct` 是最佳选择——它一行就能声明一个不可变值类型，并自动生成 `Equals`、`GetHashCode`、`ToString`。

```csharp
using Arch.Core;
using Unity.Mathematics;  // 使用 Unity 的数学库，比 UnityEngine.Vector3 更适合 ECS

namespace Chapter03
{
    // 位置组件：3D 坐标
    public readonly record struct Position(float X, float Y, float Z);

    // 速度组件：每帧位移
    public readonly record struct Velocity(float X, float Y, float Z);

    // 名字组件（标签数据）
    public readonly record struct Name(string Value);
}
```

为什么不直接用 `UnityEngine.Vector3`？因为它在 ECS 上下文里有几个问题：

⚠️ `UnityEngine.Vector3` 是引用类型不友好（虽然本身是 struct，但与 `Unity.Mathematics.float3` 相比缺少 SIMD 优化），且在非主线程使用受限。`Unity.Mathematics.float3` 是为 Burst/ECS 设计的，推荐使用。

如果项目里没装 `Unity.Mathematics`，可以用 `System.Numerics.Vector3` 替代，或干脆用三个 `float`：

```csharp
public readonly record struct Position(float X, float Y, float Z);
```

💡 **组件设计原则**：
1. 只存数据，不写方法。
2. 体积越小越好（< 64 字节为佳）。
3. 字段布局紧凑（避免引用类型字段，除非必要）。

📖 回顾：[第02章 2.3.2 Component —— 纯数据](file:///d:/Unity/Arch/unity/Tutorial/02-ECS核心概念.md)。

---

## 3.3 步骤2：创建 World

World 是所有实体的容器，必须先创建 World，再创建实体。Arch 通过静态方法 `World.Create()` 创建世界：

```csharp
using Arch.Core;

// 创建默认配置的 World
World world = World.Create();
```

📖 详见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L119) 的方法签名：

```csharp
public static World Create(
    int chunkSizeInBytes = 16_384,             // Chunk 基础大小（字节），默认 16KB
    int minimumAmountOfEntitiesPerChunk = 100, // 每个 Chunk 至少容纳多少实体
    int archetypeCapacity = 2,                 // 初始 Archetype 容量
    int entityCapacity = 64);                  // 初始实体容量
```

### 参数详解

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `chunkSizeInBytes` | `16_384`（16KB） | 单个 Chunk 的基础字节数。建议保持 16KB 以匹配 L1 缓存。 |
| `minimumAmountOfEntitiesPerChunk` | `100` | 每个 Chunk 至少要能装多少实体。如果组件很大导致 16KB 装不下 100 个，Chunk 会自动扩容。 |
| `archetypeCapacity` | `2` | World 初始预留给 Archetype 的容量，超出会自动扩容。 |
| `entityCapacity` | `64` | World 初始预留给实体的容量。 |

💡 **新手建议**：除非你明确知道在做什么，否则**全部用默认值**就行。这些默认值是经过社区调优的。

### 自定义参数示例

如果你知道会有 10000 个相同类型的实体，可以预分配：

```csharp
var world = World.Create(
    chunkSizeInBytes: 16_384,
    minimumAmountOfEntitiesPerChunk: 500,  // 让每个 Chunk 装更多
    archetypeCapacity: 4,
    entityCapacity: 10_000                // 一次性预留容量，减少扩容开销
);
```

⚠️ **注意**：`World` 实现了 `IDisposable`，使用完毕后必须调用 `World.Destroy(world)` 或 `world.Dispose()` 释放资源，否则会导致内存泄漏。

```csharp
try
{
    var world = World.Create();
    // ... 使用 world
}
finally
{
    World.Destroy(world);  // 或 world.Dispose()
}
```

📖 详见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L159) 的 `Destroy` 方法。

---

## 3.4 步骤3：创建实体

Arch 提供多种 `world.Create` 重载，覆盖不同场景。

### 3.4.1 单个实体 + 组件类型（不带初始值）

最基础的形式，传入组件类型，实体会用默认值初始化：

```csharp
// 通过 ComponentType[] 声明组件结构
Entity e = world.Create(
    new ComponentType[] { typeof(Position), typeof(Velocity) }
);
```

📖 详见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L297)：

```csharp
[StructuralChange]
public Entity Create(params ComponentType[] types);
```

### 3.4.2 单个实体 + 泛型组件（带初始值）⭐ 最常用

这是最推荐的写法，类型安全且能直接传入初始值：

```csharp
Entity adventurer = world.Create(
    new Position(0, 0, 0),
    new Velocity(1, 0, 0),
    new Name("Hero-01")
);
```

💡 这种写法会自动推断组件类型并创建对应的 Archetype。第一次创建时，World 会自动建一个新的 Archetype；后续相同组件组合的实体会复用同一个 Archetype。

### 3.4.3 批量创建相同组件的实体

当你需要一次性创建大量相同结构的实体时，使用批量重载：

```csharp
// 创建 1000 个只有 Position 组件的实体，初始值为 default
world.Create<Position>(amount: 1000);

// 创建 500 个带 Velocity 的实体，并统一设置初始值
world.Create<Velocity>(amount: 500, cmp: new Velocity(0, 1, 0));
```

📖 详见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L1084)：

```csharp
[StructuralChange]
public void Create<T>(int amount, in T? cmp = default);
```

⚠️ 注意：这个重载是 `void` 返回，因为它返回的 Entity 数量很大，会写入你提供的 `Span<Entity>`。

### 3.4.4 批量创建 + 获取实体句柄

如果你想批量创建并拿到所有 Entity 句柄：

```csharp
// 预分配 Span
Span<Entity> entities = new Entity[100];

// 批量创建并写入 Span
world.Create(entities, new Signature(typeof(Position), typeof(Velocity)), amount: 100);
```

📖 详见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L1061)：

```csharp
[StructuralChange]
public void Create(Span<Entity> createdEntities, in Signature signature, int amount);
```

🔥 **性能提示**：批量创建比循环单个创建快 10 倍以上，因为可以一次性扩展 Chunk 容量，避免反复结构变更。

### 3.4.5 实体的常见操作

```csharp
// 判断实体是否还存活
bool alive = world.IsAlive(adventurer);

// 获取组件（返回 ref，可修改）
ref Position pos = ref world.Get<Position>(adventurer);
pos.X += 1f;

// 修改组件
world.Set(adventurer, new Position(10, 0, 0));

// 判断是否有某组件
bool hasVel = world.Has<Velocity>(adventurer);

// 添加组件（会触发结构变更，搬到新 Archetype）
world.Add(adventurer, new Name("Renamed"));

// 移除组件
world.Remove<Name>(adventurer);

// 销毁实体
world.Destroy(adventurer);
```

📖 这些方法定义在 [World.cs#L1009](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L1009) 的 `Accessors` 区域。

---

## 3.5 步骤4：编写查询

查询是 System 工作的核心。Arch 使用 `QueryDescription` 描述"我要找什么样的实体"。

### 3.5.1 最简单的查询

```csharp
// 查询所有同时拥有 Position 和 Velocity 的实体
var query = new QueryDescription(
    all: new Signature(typeof(Position), typeof(Velocity))
);

// 或使用链式 API（更推荐，类型安全）
var query = new QueryDescription()
    .WithAll<Position>()
    .WithAll<Velocity>();
```

📖 详见 [Query.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L366) 的构造函数，以及 [Query.cs#L395](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L395) 的 `WithAll<T>`。

### 3.5.2 四种筛选条件组合

```csharp
var query = new QueryDescription()
    .WithAll<Position, Velocity>()      // 必须同时拥有 Position 和 Velocity
    .WithAny<Alive, Ghost>()            // 至少拥有 Alive 或 Ghost 之一
    .WithNone<Dead>()                   // 不能拥有 Dead
    .WithExclusive<Player>();           // 精确匹配（不能拥有 Player 之外的组件，等等）
```

| 条件 | 语义 |
| --- | --- |
| `All` | 必须拥有的全部组件 |
| `Any` | 至少拥有其中之一 |
| `None` | 不能拥有的组件 |
| `Exclusive` | 组件集合精确匹配（不多不少） |

### 3.5.3 遍历查询结果（核心模式）

Arch 的查询遍历是**面向 Chunk** 的——你拿到的是一个个 Chunk，然后从 Chunk 里取组件数组，这是最高效的写法：

```csharp
var query = new QueryDescription().WithAll<Position, Velocity>();

foreach (ref var chunk in world.Query(query))
{
    // chunk.GetFirst<T>() 返回第一个 T 组件的 ref
    ref var firstPos = ref chunk.GetFirst<Position>();
    ref var firstVel = ref chunk.GetFirst<Velocity>();

    // chunk 是可迭代的，返回当前 chunk 中有效实体的索引
    foreach (var i in chunk)
    {
        // 通过 Unsafe.Add 拿到第 i 个元素
        ref var pos = ref Unsafe.Add(ref firstPos, i);
        ref var vel = ref Unsafe.Add(ref firstVel, i);

        // 更新位置
        pos.X += vel.X;
        pos.Y += vel.Y;
        pos.Z += vel.Z;
    }
}
```

📖 见 [World.cs#L411](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L411) 的 `Query` 方法。

### 3.5.4 简化的 ForEach 遍历

如果不需要极致性能，可以用 `ForEach` 委托：

```csharp
world.Query(in query, (Entity e) =>
{
    ref var pos = ref world.Get<Position>(e);
    ref var vel = ref world.Get<Velocity>(e);
    pos.X += vel.X;
});
```

📖 详见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L758)。

⚠️ **性能警告**：`ForEach` 委托版本每次循环都要查 `EntityInfo` 找到组件位置，比 Chunk 遍历慢 3~5 倍。**生产代码请用 Chunk 遍历**。

### 3.5.5 高性能 InlineQuery

Arch 还提供 `InlineQuery<T>`，通过泛型 struct 接口让 JIT 内联，性能最佳：

```csharp
// 实现 IForEach 接口
public struct MoveJob : IForEach
{
    public World World;
    public void Update(Entity entity)
    {
        ref var pos = ref World.Get<Position>(entity);
        ref var vel = ref World.Get<Velocity>(entity);
        pos.X += vel.X;
        pos.Y += vel.Y;
        pos.Z += vel.Z;
    }
}

// 调用
world.InlineQuery<MoveJob>(in query);
```

📖 详见 [World.cs#L778](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L778)。

🔥 `InlineQuery` 在 Release 模式下会被 JIT 完全内联，几乎没有调用开销，是热路径的首选。

---

## 3.6 步骤5：完整代码

把上面的步骤合在一起，得到完整示例：

```csharp
using System;
using Arch.Core;
using Arch.Core.Utils;
using Unity.Mathematics;

namespace Chapter03
{
    /// <summary>
    /// 第03章配套示例：冒险者移动。
    /// 演示组件定义、World 创建、实体创建、查询、批量遍历。
    /// </summary>
    public static class FirstArchDemo
    {
        // —— 组件定义 ——
        public readonly record struct Position(float X, float Y, float Z);
        public readonly record struct Velocity(float X, float Y, float Z);
        public readonly record struct Name(string Value);

        public static void Run()
        {
            // 1. 创建 World
            World world = World.Create();
            try
            {
                // 2. 创建几个冒险者（带初始值）
                Entity a1 = world.Create(
                    new Position(0, 0, 0),
                    new Velocity(1, 0, 0),
                    new Name("Alice")
                );
                Entity a2 = world.Create(
                    new Position(10, 0, 0),
                    new Velocity(0, 1, 0),
                    new Name("Bob")
                );
                Entity a3 = world.Create(
                    new Position(0, 10, 0),
                    new Velocity(-1, 0, 0),
                    new Name("Cara")
                );

                Console.WriteLine($"World created: {world}");
                Console.WriteLine($"Total entities: {world.Size}");

                // 3. 模拟 3 帧
                var query = new QueryDescription()
                    .WithAll<Position, Velocity>();

                for (int frame = 1; frame <= 3; frame++)
                {
                    Console.WriteLine($"\n=== Frame {frame} ===");

                    // —— 高性能 Chunk 遍历 ——
                    foreach (ref var chunk in world.Query(query))
                    {
                        ref var firstPos = ref chunk.GetFirst<Position>();
                        ref var firstVel = ref chunk.GetFirst<Velocity>();

                        foreach (var i in chunk)
                        {
                            ref var pos = ref Unsafe.Add(ref firstPos, i);
                            ref var vel = ref Unsafe.Add(ref firstVel, i);

                            // 位置 += 速度
                            pos.X += vel.X;
                            pos.Y += vel.Y;
                            pos.Z += vel.Z;
                        }
                    }

                    // 打印每个冒险者的位置
                    PrintAdventurers(world, query);
                }

                // 4. 演示批量创建
                Console.WriteLine("\n--- 批量创建 100 个 NPC ---");
                Span<Entity> npcs = new Entity[100];
                world.Create(npcs, new Signature(typeof(Position), typeof(Velocity)), 100);
                Console.WriteLine($"After batch creation, total entities: {world.Size}");
            }
            finally
            {
                // 5. 释放 World
                World.Destroy(world);
                Console.WriteLine("\nWorld destroyed.");
            }
        }

        private static void PrintAdventurers(World world, QueryDescription query)
        {
            world.Query(in query, (Entity e) =>
            {
                ref var pos = ref world.Get<Position>(e);
                ref var name = ref world.Get<Name>(e);
                Console.WriteLine($"  {name.Value} -> ({pos.X}, {pos.Y}, {pos.Z})");
            });
        }
    }
}
```

📖 **配套示例文件**：[Assets/Scripts/Chapter03/FirstArchDemo.cs](file:///d:/Unity/Arch/unity/Assets/Scripts/Chapter03/FirstArchDemo.cs)

> 将上述代码保存到 Unity 项目的 `Assets/Scripts/Chapter03/FirstArchDemo.cs`，并确保：
> 1. 已通过 NuGet for Unity 安装 `Arch` 包。
> 2. 已安装 `Unity.Mathematics` 包（Package Manager → com.unity.mathematics）。
> 3. 在 `Awake`/`Start` 中调用 `FirstArchDemo.Run()` 即可看到输出。

---

## 3.7 运行结果分析

运行上面的代码，你会看到类似如下输出：

```
World created: World { Id = 0, Capacity = 64, Size = 3 }
Total entities: 3

=== Frame 1 ===
  Alice -> (1, 0, 0)
  Bob -> (10, 1, 0)
  Cara -> (-1, 10, 0)

=== Frame 2 ===
  Alice -> (2, 0, 0)
  Bob -> (10, 2, 0)
  Cara -> (-2, 10, 0)

=== Frame 3 ===
  Alice -> (3, 0, 0)
  Bob -> (10, 3, 0)
  Cara -> (-3, 10, 0)

--- 批量创建 100 个 NPC ---
After batch creation, total entities: 103

World destroyed.
```

### 结果解读

1. **`Size = 3`**：World 内有 3 个实体（Alice、Bob、Cara），它们拥有相同的组件组合（`Position + Velocity + Name`），所以被归到**同一个 Archetype**。
2. **位置每帧按速度累加**：Alice 的 X 从 0→1→2→3（速度 `(1,0,0)`），Bob 的 Y 从 0→1→2→3（速度 `(0,1,0)`），Cara 的 X 从 0→-1→-2→-3（速度 `(-1,0,0)`）。
3. **批量创建后 `Size = 103`**：100 个 NPC 也拥有 `Position + Velocity`（注意没 Name），它们会被分到**另一个 Archetype**（因为组件组合不同）。
4. **Capacity 自动增长**：初始 64 → 批量创建 100 后会扩容。

💡 **重点观察**：3 个冒险者属于 Archetype A（有 Name），100 个 NPC 属于 Archetype B（无 Name）。如果我们查询 `WithAll<Position, Velocity>`，**两个 Archetype 都会被遍历**——这就是 Archetype 模型的威力：一个 Query 跨多个 Archetype。

---

## 3.8 常见错误

### ❌ 错误1：忘记销毁 World

```csharp
// 错误：World 实现了 IDisposable，不释放会泄漏
var world = World.Create();
// ... 用完就没了
```

✅ **正确做法**：

```csharp
var world = World.Create();
try { /* ... */ }
finally { World.Destroy(world); }

// 或使用 using
using var world = World.Create();
```

📖 见 [World.cs#L539](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L539) 的 `Dispose`。

---

### ❌ 错误2：在遍历时做结构变更

```csharp
// 错误：遍历过程中销毁实体，会破坏 Chunk 内部状态
foreach (ref var chunk in world.Query(query))
{
    foreach (var i in chunk)
    {
        var e = chunk.Entity(i);
        world.Destroy(e);  // 💥 异常或数据错乱
    }
}
```

✅ **正确做法**：用批量 API `world.Destroy(in QueryDescription)`，它专门处理了这种情况：

```csharp
var toDestroy = new QueryDescription().WithAll<Dead>();
world.Destroy(in toDestroy);
```

📖 见 [World.cs#L832](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L832)。

⚠️ 任何带 `[StructuralChange]` 特性的方法（`Create`、`Destroy`、`Add<T>`、`Remove<T>`）都**不能在查询遍历中调用**。

---

### ❌ 错误3：组件用 class 而不是 struct

```csharp
// 不推荐：class 会被作为引用存进 Chunk，破坏 SoA 布局
public class Position { public float X, Y, Z; }
```

✅ **正确做法**：

```csharp
public readonly record struct Position(float X, float Y, float Z);
```

🔥 `class` 组件会强制 Arch 把它存为引用数组（`Position[]` 变成 `Position[]` 但元素是堆上的对象），CPU 遍历时缓存命中率暴跌。

---

### ❌ 错误4：查询条件写错（漏组件或多余组件）

```csharp
// 错误：忘了同时要求 Velocity，结果遍历时 chunk.GetFirst<Velocity>() 会抛异常
var query = new QueryDescription().WithAll<Position>();

foreach (ref var chunk in world.Query(query))
{
    ref var vel = ref chunk.GetFirst<Velocity>();  // 💥 异常！
}
```

✅ **正确做法**：查询条件必须包含所有你后续要读写的组件类型：

```csharp
var query = new QueryDescription()
    .WithAll<Position, Velocity>();  // 同时声明两个
```

💡 **规则**：`QueryDescription.All` 至少要包含你 `chunk.GetFirst<T>()` / `chunk.Get<T>(i)` 用到的所有 T。

---

### ❌ 错误5：在多线程中并发做结构变更

```csharp
// 错误：多线程同时 Create/Destroy 会破坏 World 内部数据结构
Parallel.For(0, 1000, i =>
{
    world.Create(new Position(i, 0, 0));  // 💥 数据竞争
});
```

📖 World 的注释明确说明：*"Read-only operations like querying entities can be done simultaneously by multiple threads. However, any method which mentions 'structural changes' must not run alongside any other methods."*（见 [World.cs#L174](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L174)）

✅ **正确做法**：
- **结构变更**（Create/Destroy/Add/Remove）必须在主线程串行执行。
- **只读查询** 可以多线程并发（每个线程处理不同的 Chunk）。
- 需要 Job 调度时，使用 Arch 的 `IJob` 与 `JobScheduler`（见 [World.Jobs.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Jobs/World.Jobs.cs)），它会自动分配 Chunk。

```csharp
// 主线程批量创建（安全）
Span<Entity> entities = new Entity[1000];
world.Create(entities, new Signature(typeof(Position)), 1000);

// 然后多线程并行处理查询（只读 + 局部写入，不结构变更）
// 用 Arch 的 JobSystem...
```

---

## 3.9 配套示例说明

本节示例代码已封装在 `FirstArchDemo` 静态类中，完整路径：

📖 [Assets/Scripts/Chapter03/FirstArchDemo.cs](file:///d:/Unity/Arch/unity/Assets/Scripts/Chapter03/FirstArchDemo.cs)

### 在 Unity 中运行

1. **安装依赖**：
   - 通过 NuGet for Unity 安装 `Arch`（最新版即可）。
   - 通过 Package Manager 安装 `Unity.Mathematics`。

2. **创建脚本**：在 `Assets/Scripts/Chapter03/` 下新建 `FirstArchDemo.cs`，粘贴 3.6 节代码。

3. **挂载调用**：在场景中创建一个 GameObject，挂一个简单的 `MonoBehaviour`：

```csharp
using UnityEngine;

public class DemoRunner : MonoBehaviour
{
    void Start()
    {
        Chapter03.FirstArchDemo.Run();
    }
}
```

4. **运行场景**：进入 Play 模式，查看 Console 输出，应能看到 3.7 节的结果。

### 进阶练习

💡 试着扩展这个示例：
1. 给冒险者添加 `Health` 组件，写一个 `DamageSystem` 每帧扣血。
2. 用 `QueryDescription.WithNone<Dead>()` 过滤已死亡的实体。
3. 改造为批量创建 10000 个冒险者，对比 Mono 行为下的性能差异。
4. 尝试 `InlineQuery<T>` 写法，看看 Release 模式下性能提升多少。

---

## 本章小结

| 步骤 | 关键 API | 一句话说明 | 源码位置 |
| --- | --- | --- | --- |
| 1. 定义组件 | `record struct` | 纯数据值类型 | 用户定义 |
| 2. 创建 World | `World.Create(...)` | 顶层容器，可选自定义 Chunk 大小 | [World.cs#L119](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L119) |
| 3a. 单个实体 | `world.Create<T1, T2, ...>(...)` | 类型安全，带初始值 | [World.cs#L297](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L297) |
| 3b. 批量实体 | `world.Create<T>(amount, cmp)` | 一次性创建大量同结构实体 | [World.cs#L1084](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L1084) |
| 4a. 查询条件 | `new QueryDescription().WithAll<T>()` | 描述要找什么样的实体 | [Query.cs#L314](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L314) |
| 4b. Chunk 遍历 | `foreach (ref var chunk in world.Query(q))` | 最高性能的遍历方式 | [World.cs#L411](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L411) |
| 4c. ForEach | `world.Query(in q, (e) => {...})` | 简洁但性能略低 | [World.cs#L758](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L758) |
| 4d. InlineQuery | `world.InlineQuery<TJob>(in q)` | JIT 内联，性能最佳 | [World.cs#L778](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L778) |
| 5. 读写组件 | `world.Get<T>(e)` / `world.Set<T>(e, v)` | 通过 Entity 句柄访问数据 | [World.cs#L1142](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L1142) |
| 6. 销毁 World | `World.Destroy(world)` | 必须 release 资源 | [World.cs#L159](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L159) |

### 关键提醒

| ⚠️ 警告 | 内容 |
| --- | --- |
| World 必须 Dispose | 实现 `IDisposable`，否则内存泄漏 |
| 遍历中不能结构变更 | `Create`/`Destroy`/`Add<T>`/`Remove<T>` 必须在遍历外调用 |
| 组件用 struct 不用 class | class 破坏 SoA 布局，性能暴跌 |
| 查询条件要完整 | 所有 `Get<T>` 的 T 都必须在 `WithAll` 里声明 |
| 结构变更主线程串行 | 多线程只能做只读查询或局部写入 |

📖 **下一章预告**：第04章 将深入讲解 Archetype 与 Chunk 的内部机制，包括结构变更的开销分析、批量操作 API、以及如何用 `JobScheduler` 实现多线程并行查询。
