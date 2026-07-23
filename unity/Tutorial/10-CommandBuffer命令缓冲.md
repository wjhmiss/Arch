# 第10章 CommandBuffer 命令缓冲

## 10.1 概述

在前面的章节里，我们一直在 World 上直接调用 `Create` / `Destroy` / `Add` / `Set` / `Remove` 等结构变更 API。这些 API 立即生效——调用一结束，实体就已被搬到新的 Archetype，Chunk 也已经重新组织。

但 ECS 中存在一类非常常见的场景：

- 在 `Query` 回调中遍历实体时，需要根据某些条件**销毁**当前实体或**给当前实体添加**新组件；
- 在 `ParallelQuery` 多线程遍历时，多个线程同时尝试增删组件；
- 一帧内累积了大量结构变更，希望**统一一次性**应用，减少 Archetype 反复搬运。

这些场景下"立即生效"反而是个麻烦：遍历过程中修改结构会导致 Chunk 内部数据移动，可能让迭代器失效；多线程并发修改 Archetype 也会破坏一致性。Arch 的答案是 **CommandBuffer**——一个把结构变更操作**记录**下来、稍后**回放**执行的缓冲区。

> 💡 类比：CommandBuffer 就像 ECS 的"购物车"。你在购物时（Query 中）不能直接修改货架（Archetype），只能把要买的东西写进购物车（记录命令），等逛完之后到收银台统一结账（Playback）。

## 10.2 为什么需要 CommandBuffer

### 10.2.1 在 Query 回调中修改实体的风险

回顾第 08 章我们学过的 `world.Query(in queryDescription, (Entity e) => { ... })`：迭代器内部按 Archetype → Chunk → Entity 的顺序遍历。当你在这回调里调用 `world.Add<T>(e)` 时：

1. 实体 `e` 会被从原 Archetype 的 Chunk 中移除；
2. 创建（或复用）一个新 Archetype，把 `e` 搬过去；
3. 搬迁过程可能触发 Chunk 扩容、`Slot` 重排。

如果这个 `e` 恰好是当前 Chunk 内还未迭代到的位置上的实体，迭代器下一次取到的就是错误的数据。即便不报错，行为也是**未定义**的。

Arch 官方文档明确建议：

> **不推荐**在 Query 内部直接修改实体结构。可以使用 `CommandBuffer`，或自行维护一个待处理实体列表，等 Query 结束后再统一处理。

### 10.2.2 多线程结构变更的不可行性

`ParallelQuery` 会把 Chunk 数组切分成多段，由 `JobScheduler` 调度到多个线程并行执行。如果允许在工作线程里直接调用 `world.Destroy(e)`：

- 多个线程同时搬迁同一个 Archetype，会破坏 `EntityInfo` 索引；
- `Chunk` 内部的数组写入需要加锁，性能反而下降；
- `Archetype` 的 `BitSet` 哈希查找是共享状态。

正确的做法是：每个工作线程把要做的修改写进自己的 `CommandBuffer`（线程安全），等所有线程都结束后，在主线程一次性 `Playback`。

### 10.2.3 帧内结构性变更的合并

有些游戏逻辑会一帧内多次给同一批实体加/减组件，例如：

```csharp
foreach (var e in dirtyEntities)
{
    world.Remove<Dirty>(e);
    world.Add<Recompute>(e);
}
```

每条语句都触发一次 Archetype 迁移，性能损耗会累积。用 CommandBuffer 记录后 `Playback`，等价操作会被合并、批量化执行，迁移次数显著减少。

## 10.3 API 解析

CommandBuffer 完整源码见 [CommandBuffer.cs](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/CommandBuffer.cs)，内部依赖两个稀疏集合 [SparseSet.cs](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/SparseSet.cs) 与 [StructuralSparseSet.cs](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/StructuralSparseSet.cs)。

### 10.3.1 创建 CommandBuffer

```csharp
// 默认初始容量 128，足够大多数帧内缓冲场景
var cb = new CommandBuffer();

// 显式指定初始容量（如果你预计要记录大量命令）
var cb = new CommandBuffer(initialCapacity: 1024);
```

> ⚠️ 注意：CommandBuffer **不是**从 World 创建的，它本身就是独立的 `sealed class`，实现 `IDisposable`。Arch 没有 `world.CreateCommandBuffer()` 这样的工厂方法，因为 CommandBuffer 不绑定任何 World——同一个 buffer 理论上可以 `Playback` 到不同的 World。

构造函数源码（[CommandBuffer.cs#L78](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/CommandBuffer.cs#L78)）：

```csharp
public CommandBuffer(int initialCapacity = 128)
{
    Entities = new PooledList<Entity>(initialCapacity);
    BufferedEntityInfo = new PooledDictionary<int, BufferedEntityInfo>(initialCapacity);
    Creates = new PooledList<CreateCommand>(initialCapacity);
    Sets = new SparseSet(initialCapacity);
    Adds = new StructuralSparseSet(initialCapacity);
    Removes = new StructuralSparseSet(initialCapacity);
    Destroys = new PooledList<int>(initialCapacity);
    _addTypes = new PooledList<ComponentType>(16);
    _removeTypes = new PooledList<ComponentType>(16);
}
```

可以看到它内部维护了 5 个独立的存储区，分别对应 5 种命令：

| 命令类型 | 存储容器 | 是否保存组件值 |
|---------|---------|---------------|
| `Create` | `PooledList<CreateCommand>` | 否（只记录组件类型数组） |
| `Add<T>` | `StructuralSparseSet` | 是（值存在 Sets 里） |
| `Set<T>` | `SparseSet` | 是 |
| `Remove<T>` | `StructuralSparseSet` | 否（移除不需要值） |
| `Destroy` | `PooledList<int>` | 否 |

### 10.3.2 记录命令

#### Create —— 创建实体

```csharp
// 返回一个"占位 Entity"，Id 为负数，Playback 之后会被替换成真实 Entity
Entity placeholder = cb.Create(new ComponentType[]
{
    typeof(Position), typeof(Velocity)
});

// 你可以立即对这个占位实体继续记录命令，它们会关联起来
cb.Set(placeholder, new Position { X = 10, Y = 20 });
```

实现（[CommandBuffer.cs#L173](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/CommandBuffer.cs#L173)）非常巧妙：构造一个 `Id = -(Size+1)` 的"负数实体"作为占位，记录到 `Creates` 列表中。`Playback` 时真正调用 `world.Create(types)` 得到真实 Entity，再用 `Entities[cmd.Index] = entity` 替换占位。

#### Destroy —— 销毁实体

```csharp
cb.Destroy(existingEntity);  // 已存在的实体
cb.Destroy(placeholder);      // 占位实体也可以销毁（PlayBack 时不会创建它）
```

#### Add / Set / Remove —— 修改组件

```csharp
cb.Add<Health>(entity, new Health { Value = 100 });  // 添加组件（带初始值）
cb.Set<Position>(entity, new Position { X = 5, Y = 5 });  // 覆盖已有组件值
cb.Remove<Velocity>(entity);  // 移除组件
```

观察 [CommandBuffer.cs#L238](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/CommandBuffer.cs#L238) 中 `Add<T>` 的实现：

```csharp
public void Add<T>(in Entity entity, in T? component = default)
{
    BufferedEntityInfo info;
    lock (this)
    {
        if (!BufferedEntityInfo.TryGetValue(entity.Id, out info))
        {
            Register(entity, out info);
        }
    }

    Adds.Set<T>(info.AddIndex);
    Sets.Set(info.SetIndex, in component);
}
```

💡 关键点：

1. `Add<T>` 内部实际上做了**两件事**——在 `Adds` 中标记"需要添加 T"，在 `Sets` 中保存"添加时的初始值"。`Playback` 时先调用 `world.AddRange` 完成结构变更，再从 `Sets` 拷贝值到新 Chunk。
2. 所有公共 API 都用 `lock (this)` 包了一层，保证**线程安全**。这就是 ParallelQuery 中可以放心调用 CommandBuffer 的原因。
3. `BufferedEntityInfo` 是个字典，第一次操作某实体时会 `Register` 它，分配三个索引（SetIndex/AddIndex/RemoveIndex），后续操作复用这些索引。

### 10.3.3 Playback —— 回放执行

```csharp
// 在主线程上回放所有命令，并清空 buffer（默认行为）
cb.Playback(world);

// 回放但保留命令，可以再次 Playback 到另一个 World（或同一 World 多次）
cb.Playback(world, dispose: false);
```

`Playback` 是 CommandBuffer 中最复杂的方法（[CommandBuffer.cs#L283](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/CommandBuffer.cs#L283)），执行顺序固定为：

1. **Creates**：调用 `world.Create(cmd.Types)` 创建所有待创建实体，把真实 Entity 写回 `Entities` 列表。
2. **Adds**：遍历每个被标记添加组件的实体，收集它所有要添加的类型，**一次性**调用 `world.AddRange(entity, types)`。这是关键优化——多个 `Add<T>` 调用被合并成一次 Archetype 迁移。
3. **Sets**：把记录的组件值 `Array.Copy` 到对应 Chunk 的组件数组中。
4. **Removes**：与 Adds 类似，合并多个 `Remove<T>` 为一次 `world.RemoveRange(entity, types)`。
5. **Destroys**：最后才销毁实体（保证前面步骤中实体还活着）。

⚠️ `Playback` 必须在**主线程**调用，因为内部直接调用了 World 的结构变更 API。`dispose=true`（默认）会清空所有内部容器，CommandBuffer 可以复用记录下一帧的命令。

## 10.4 内部数据结构

### 10.4.1 SparseSet —— 带值的稀疏集

[SparseSet.cs](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/SparseSet.cs) 用于存储 `Set<T>` 命令的组件值。它是一个"按组件类型分桶"的稀疏数组：

```
Components 数组（按 componentType.Id 索引）
  ├── [0] SparseArray<Transform>  -> Entities: [0, 2, 5], Components: [t0, t1, t2]
  ├── [1] SparseArray<Velocity>   -> Entities: [1, 3],    Components: [v0, v1]
  └── [2] null
```

每个 `SparseArray` 内部：
- `Entities[index]` 存储实体在 CommandBuffer 中的序号到组件数组位置的映射；
- `Components` 是一个 `Array`，实际类型由 `ComponentType` 决定，通过 `Unsafe.As<T[]>` 转换后访问。

> 📖 这种设计避免了泛型参数泄漏到容器类型，同时仍能保持强类型访问。详见 [SparseSet.cs#L141](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/SparseSet.cs#L141) 中 `Set<T>` 的实现。

### 10.4.2 StructuralSparseSet —— 无值的结构稀疏集

[StructuralSparseSet.cs](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/StructuralSparseSet.cs) 与 `SparseSet` 结构几乎一样，但 `StructuralSparseArray` **不存储组件值**——只记录"这个实体需要添加/移除类型 T"的标记。它服务于 `Add<T>` 和 `Remove<T>`，因为这两个操作在记录时只需要类型信息，值通过 `Sets` 单独保存。

这样分离的好处是：`Playback` 阶段可以高效地"先合并所有结构变更，再批量应用值"，减少 Archetype 搬迁次数。

## 10.5 使用示例

### 10.5.1 在普通 Query 中收集销毁请求

```csharp
var world = World.Create();
for (int i = 0; i < 1000; i++)
    world.Create(new Position { X = i }, new Health { Value = i });

var cb = new CommandBuffer();
var query = new QueryDescription(all: [typeof(Position), typeof(Health)]);

// 遍历时不能直接 world.Destroy，但可以记录到 cb
world.Query(in query, (Entity e, ref Health h) =>
{
    if (h.Value <= 0)
    {
        cb.Destroy(e);  // 仅记录，不立即执行
    }
});

cb.Playback(world);  // 主线程统一执行销毁
cb.Dispose();
```

### 10.5.2 在 ParallelQuery 中使用 CommandBuffer

🔥 这是 CommandBuffer 最经典的用法。完整示例可参考 `Assets/Scripts/Chapter10/CommandBufferDemo.cs`：

```csharp
using System.Threading;
using Arch.Buffer;
using Arch.Core;
using Schedulers;
using UnityEngine;

public class CommandBufferDemo : MonoBehaviour
{
    private World _world;
    private JobScheduler _scheduler;

    private struct Position { public float X, Y; }
    private struct Health { public float Value; }
    private struct Dead { }  // 标记组件，无字段

    private void Start()
    {
        // 1. 初始化 JobScheduler（ParallelQuery 必需）
        _scheduler = new JobScheduler(new JobScheduler.Config
        {
            ThreadPrefixName = "Arch.Demo",
            ThreadCount = 0,            // 0 = 按处理器数
            MaxExpectedConcurrentJobs = 64,
            StrictAllocationMode = false
        });
        World.SharedJobScheduler = _scheduler;

        _world = World.Create();
        for (int i = 0; i < 10000; i++)
        {
            _world.Create(
                new Position { X = i, Y = 0 },
                new Health { Value = Random.Range(0f, 100f) }
            );
        }

        // 2. 多线程遍历，每个线程记录自己的命令到同一个 CommandBuffer
        var cb = new CommandBuffer(initialCapacity: 256);
        var query = new QueryDescription(all: [typeof(Position), typeof(Health)]);

        _world.ParallelQuery(in query, (Entity e, ref Health h) =>
        {
            if (h.Value <= 10f)
            {
                // 线程安全：CommandBuffer 内部已加锁
                cb.Add<Dead>(e);        // 标记为死亡
                cb.Set<Health>(e, new Health { Value = 0 });
            }
        });

        // 3. 主线程回放
        cb.Playback(_world);
        Debug.Log($"死亡实体数: {_world.CountEntities(new QueryDescription(all: [typeof(Dead)]))}");
        cb.Dispose();
    }

    private void OnDestroy()
    {
        _scheduler.Dispose();
        World.Destroy(_world);
    }
}
```

注意几点：

- `CommandBuffer` 的所有公共方法都通过 `lock (this)` 保证线程安全，多个工作线程可以**同时**调用它；
- `ParallelQuery` 本身**不是**线程安全的，必须从主线程调用——但它启动后内部回调是并行的；
- `Playback` 一定要等 `ParallelQuery` 返回之后在主线程执行。

### 10.5.3 创建占位实体并继续操作

```csharp
var cb = new CommandBuffer();

// 还没真正创建，但可以拿到占位引用
Entity e1 = cb.Create([typeof(Position), typeof(Velocity)]);
Entity e2 = cb.Create([typeof(Position), typeof(Velocity)]);

// 给占位实体记录修改
cb.Set(e1, new Position { X = 10 });
cb.Set(e2, new Position { X = 20 });

// 也可以销毁占位实体（Playback 后这个实体不会被创建）
// cb.Destroy(e2);

cb.Playback(world);
// 此后 e1 已经被替换为真实 Entity，但本地变量还是负数 Id
// 需要通过 cb 内部机制或重新查询来获取真实 Entity
```

⚠️ 注意：`Playback` 后，本地变量 `e1` 仍然是占位（负数 Id），不能直接用于 `world.Get<T>(e1)`。如果需要拿到真实 Entity，应在 `Playback` 前从 CommandBuffer 暴露的 `Entities` 列表读取（不过这是 `internal` 的），或干脆重新查询。

## 10.6 性能考量

### 10.6.1 何时该用 CommandBuffer

| 场景 | 推荐 |
|------|------|
| Query 内部根据条件销毁实体 | ✅ 用 CommandBuffer |
| ParallelQuery 中需要修改结构 | ✅ 必须用 CommandBuffer |
| 一帧内大量小修改需要合并 | ✅ 用 CommandBuffer |
| 单次直接 `world.Add<T>(e)` | ❌ 不必包一层 cb，反而慢 |
| 纯修改组件值（`Set<T>`） | ❌ 不需要 cb，值修改不改结构 |

### 10.6.2 开销来源

- **记录阶段**：每次 `Add/Set/Remove` 都会查字典 `BufferedEntityInfo`、可能扩容稀疏数组，但都是 O(1) 摊还。
- **Playback 阶段**：实际的结构变更开销无法省掉，但由于命令被合并（多个 `Add<T>` 合并为一次 `AddRange`），整体比"逐条立即执行"快得多。
- **内存分配**：内部使用 `Collections.Pooled` 的 `PooledList` / `PooledDictionary`，相比 `List<T>` 减少 GC 压力；`Dispose` 后池化内存归还。

### 10.6.3 容量预估

构造时 `initialCapacity` 选得合理可以避免运行时扩容：

```csharp
// 预计每帧最多记录 500 条命令
var cb = new CommandBuffer(initialCapacity: 512);
```

💡 ParallelQuery 中建议每个工作线程一个独立 CommandBuffer，而不是共享一个大的——减少锁竞争。`Playback` 时按顺序调用每个 cb 即可。

### 10.6.4 复用 CommandBuffer

`Playback(world, dispose: false)` 不清空缓冲区，可以反复 `Playback` 到不同 World，适合"录制一次模板，多次实例化"的场景：

```csharp
// 录制一个"创建敌人"的模板
var template = new CommandBuffer();
Entity enemy = template.Create([typeof(Position), typeof(Health), typeof(AI)]);
template.Set(enemy, new Health { Value = 100 });

// 在多个 World 中实例化
template.Playback(worldA, dispose: false);
template.Playback(worldB, dispose: false);
```

## 10.7 与事件系统的交互

如果你启用了 `EVENTS` 符号（见第 09 章），`Playback` 中调用 `world.Add` 等方法时会触发 `OnComponentAdded` 等事件。源码 [CommandBuffer.cs#L355](file:///d:/Unity/Arch/Arch/src/Arch/Buffer/CommandBuffer.cs#L355) 中能看到：

```csharp
#if EVENTS
if (Adds.Used.Length > i && Adds.Components[Adds.Used[i]].Contains(id))
{
    world.OnComponentAdded(entity, sparseArray.Type);
}
else
{
    world.OnComponentSet(entity, sparseArray.Type);
}
#endif
```

CommandBuffer 智能地区分了"新添加的组件"与"已存在的组件被 Set"，分别派发 `OnComponentAdded` 与 `OnComponentSet`，让事件订阅者收到正确的回调。

## 10.8 本章小结

| 主题 | 关键点 |
|------|--------|
| **CommandBuffer 作用** | 延迟执行结构变更（Create/Destroy/Add/Remove/Set） |
| **为什么需要** | Query 回调中直接修改结构未定义；多线程不能并发修改 Archetype |
| **创建方式** | `new CommandBuffer(initialCapacity = 128)`，不依赖 World |
| **记录 API** | `Create(types)` / `Destroy(e)` / `Add<T>(e, val)` / `Set<T>(e, val)` / `Remove<T>(e)` |
| **线程安全** | 所有公共方法用 `lock (this)` 包裹，可在 ParallelQuery 中并行调用 |
| **回放 API** | `Playback(world, dispose = true)`，必须在主线程调用 |
| **占位 Entity** | `Create` 返回负数 Id 的占位，Playback 时替换为真实 Entity |
| **内部存储** | `SparseSet` 存值，`StructuralSparseSet` 存结构标记，分离提升合并效率 |
| **命令合并** | 多个 `Add<T>` 合并为一次 `AddRange`，减少 Archetype 迁移次数 |
| **事件兼容** | `EVENTS` 启用时，Playback 会正确派发 Added/Set 事件 |
| **复用模式** | `dispose: false` 可多次 Playback，适合模板化创建 |
| **典型用法** | ParallelQuery + 每线程一个 cb + 主线程统一 Playback |

下一章我们将深入多线程与 Jobs 系统，详细讨论 `ParallelQuery` 内部如何调度 ChunkIterationJob，以及如何写出线程安全的并行 ECS 代码。
