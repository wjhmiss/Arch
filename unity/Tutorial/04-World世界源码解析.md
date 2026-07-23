# 第04章 World 世界源码解析

> 📖 本章基于 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs) 进行逐行解析。World 是 Arch ECS 框架的"根容器"，所有实体、原型、查询的入口都从这里开始。理解 World，就理解了 Arch 的整体架构。

---

## 4.1 World 类的角色

在 Arch 中，`World` 是 ECS 的根容器（Root Container）。它承担三大职责：

1. **持有所有 Archetype**：通过 `Archetypes` 字段集中管理本世界内的全部原型。
2. **维护实体到数据的映射**：通过 `EntityInfo` 把每个 `Entity` 的 ID 映射到其所在的 `Archetype` 与 `Slot`。
3. **提供 CRUD 与查询 API**：`Create` / `Destroy` / `Add` / `Set` / `Remove` / `Get` / `Has` / `Query` 等所有操作的入口都挂在 `World` 上。

> 💡 一个进程可以同时存在多个 `World`，它们彼此隔离。例如 Unity 中常见做法是为"游戏世界"和"UI 世界"各开一个 `World`，互不干扰。

`World` 是 `partial class`，源码被拆分到多个 region 中：静态创建/销毁、世界管理、原型管理、查询、批量查询、访问器、非泛型访问器、工具方法。本章节按功能逐个剖析。

---

## 4.2 静态成员分析

### 4.2.1 `_worlds` 数组与 `Worlds` 属性

📖 [World.cs L75-81](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L75)

```csharp
public static World[] Worlds
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get => _worlds;
}

private static World[] _worlds = new World[4];
```

`_worlds` 是一个初始容量为 4 的数组，**所有活着的 World 都按其 `Id` 作为索引存放在这里**。`Worlds` 属性只读，且通过 `AggressiveInlining` 标注内联，因为这是 Entity 扩展方法访问 World 的热路径。

> 🔥 为什么用数组而不是 `List<World>`？因为 Entity 通过 `WorldId` 直接索引访问（`World.Worlds[entity.WorldId]`），数组的 O(1) 索引访问比 List 更快，且没有边界检查外的额外开销。`DangerousGetReferenceAt` 等扩展方法甚至跳过边界检查进一步提速。

### 4.2.2 `WorldsLock` 专用锁对象

📖 [World.cs L83-90](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L83)

```csharp
/// <summary>
///     Guards <see cref="_worlds"/>, <see cref="RecycledWorldIds"/> and world id assignment.
///     A dedicated lock object: locking the array itself is unsound because the array
///     reference is replaced on resize, after which concurrent creators lock different
///     objects and race — duplicate world ids and lost slot writes (NREs in
///     <see cref="EntityExtensions"/>, AccessViolation in <see cref="Chunk"/>).
/// </summary>
private static readonly object WorldsLock = new();
```

> ⚠️ 这段注释非常重要，它揭示了 Arch 曾经踩过的一个坑。**不能直接 `lock(_worlds)`**，原因有二：
> 1. `_worlds` 在扩容时会被替换成新数组引用，并发创建者会锁定**不同的对象**，导致锁失效。
> 2. 锁失效后会出现重复 World Id、槽位写丢失，进而引发 `EntityExtensions` 中的 NRE 和 `Chunk` 中的 AccessViolation。
>
> 用一个 `readonly` 的专用锁对象，引用永不变化，才能保证所有线程锁的是同一把锁。

### 4.2.3 `RecycledWorldIds` —— ID 回收机制

📖 [World.cs L92-95](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L92)

```csharp
private static PooledQueue<int> RecycledWorldIds {  get; set; } = new(8);
```

当 `World` 被 `Dispose` 时，它的 `Id` 不会永久消失，而是被回收到这个队列。下一次 `World.Create` 时优先取出复用，避免数组中留下空洞。

> 💡 这里使用了 `Collections.Pooled.PooledQueue<T>` 而非 `System.Collections.Generic.Queue<T>`。Pooled 集合底层使用 `ArrayPool` 租赁数组，减少了 GC 压力，是 Arch 在热路径上常见的优化手段。

### 4.2.4 `WorldSize` 与 `worldSizeUnsafe`

📖 [World.cs L97-102](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L97)

```csharp
public static int WorldSize => Interlocked.CompareExchange(ref worldSizeUnsafe, 0, 0);

private static int worldSizeUnsafe;
```

`worldSizeUnsafe` 是真实的计数字段，但读取时通过 `Interlocked.CompareExchange(ref x, 0, 0)` 这种"无操作 CAS"来原子读取，保证读到的是最新值。写入时则使用 `Interlocked.Increment/Decrement`。这样读取方无需加锁即可拿到一致计数。

### 4.2.5 `SharedJobScheduler` —— 多线程调度器

📖 [World.cs L104-107](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L104)

```csharp
/// <summary>
///     The shared static <see cref="JobScheduler"/> used for Multithreading.
/// </summary>
public static JobScheduler? SharedJobScheduler { get; set; }
```

`JobScheduler` 来自 `Schedulers` 库，是 Arch 多线程 Job 的统一调度入口。它声明为 `static` 而非实例字段，是因为一个进程通常只需要一个调度器，所有 World 共享同一池工作线程。

> 📖 详见第11章《多线程与 Jobs》。

---

## 4.3 `World.Create` 方法逐行解析

📖 [World.cs L119-153](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L119)

```csharp
public static World Create(int chunkSizeInBytes = 16_384,
                            int minimumAmountOfEntitiesPerChunk = 100,
                            int archetypeCapacity = 2,
                            int entityCapacity = 64)
{
#if PURE_ECS
    return new World(-1, chunkSizeInBytes, minimumAmountOfEntitiesPerChunk,
                     archetypeCapacity, entityCapacity);
#else
    lock (WorldsLock)
    {
        var recycle = RecycledWorldIds.TryDequeue(out var id);
        var recycledId = recycle ? id : WorldSize;

        var world = new World(recycledId, chunkSizeInBytes, minimumAmountOfEntitiesPerChunk,
                              archetypeCapacity, entityCapacity);

        var worlds = _worlds;
        if (recycledId >= worlds.Length)
        {
            var resized = new World[worlds.Length * 2];
            Array.Copy(worlds, resized, worlds.Length);
            resized[recycledId] = world;
            Volatile.Write(ref _worlds, resized);
        }
        else
        {
            Volatile.Write(ref worlds[recycledId], world);
        }

        Interlocked.Increment(ref worldSizeUnsafe);
        return world;
    }
#endif
}
```

### 4.3.1 `PURE_ECS` 分支与默认分支的区别

`PURE_ECS` 编译符号开启一个"纯净模式"：World 不进入全局 `_worlds` 数组，Id 固定为 `-1`，Entity 也不携带 `WorldId` 字段。这换取了更小的内存占用与更快的访问速度，但代价是**无法通过 Entity 反查 World**，也无法多 World 共存。

> 📖 PURE_ECS 的取舍详见第13章《PureECS 与性能优化》。

### 4.3.2 锁机制与 `Volatile.Write` 的作用

默认分支进入 `lock(WorldsLock)` 临界区，确保 `RecycledWorldIds`、`_worlds` 数组、`worldSizeUnsafe` 三者的修改是原子的。

但**读取方（如 `EntityExtensions`）并不持锁**，它们直接 `World.Worlds[entity.WorldId]` 取引用。这就引出了 `Volatile.Write`：

- `Volatile.Write(ref _worlds, resized)`：保证 resized 数组的所有内容（通过 `Array.Copy` 写入）在引用发布前对其他 CPU 可见。
- `Volatile.Write(ref worlds[recycledId], world)`：保证 world 内部字段初始化在引用发布前完成。

> 🔥 没有这两个 `Volatile.Write`，在弱内存模型 CPU 上，其他线程可能先看到 `_worlds` 数组的新引用，但读不到槽位里的 World 实例或其字段，导致 NRE。

### 4.3.3 数组扩容逻辑（L132-143）

扩容触发条件是 `recycledId >= worlds.Length`，即没有回收 Id 可用、且 `WorldSize` 已经超出当前数组容量。扩容策略是经典的 **×2 翻倍**：

```csharp
var resized = new World[worlds.Length * 2];
Array.Copy(worlds, resized, worlds.Length);
resized[recycledId] = world;
Volatile.Write(ref _worlds, resized);
```

> 💡 注意 **先填槽位、再发布引用** 的顺序。如果先 `Volatile.Write(ref _worlds, resized)` 再 `resized[recycledId] = world`，读者可能看到一个空槽，引发 NRE。

### 4.3.4 ARM64 弱内存模型的考量

📖 [World.cs L134-138](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L134) 的注释明确指出：

> Fill the slot in the copy before publishing the new array so readers (EntityExtensions and generated accessors index Worlds without a lock, and Entity handles carry an address dependency on the array reference) never observe a published array whose contents are not yet visible on weakly-ordered CPUs (ARM64).

ARM64 是弱内存序架构，普通写指令可以被 CPU 重排。`Volatile.Write` 在 .NET 中插入合适的内存屏障，确保：
1. 之前的 `Array.Copy` 与 `resized[recycledId] = world` 完成且对其他核心可见。
2. 然后才发布新的 `_worlds` 引用。

此外注释提到 "Entity handles carry an address dependency"——即 Entity 持有 `WorldId`，通过该 Id 索引数组取出 World 引用，这是一种**地址依赖**。在 ARM64 上地址依赖本身能提供一定的顺序保证，但只有在引用发布顺序正确时才有意义。

---

## 4.4 构造函数解析

📖 [World.cs L190-212](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L190)

```csharp
private World(int id, int baseChunkSize, int baseChunkEntityCount,
              int archetypeCapacity, int entityCapacity)
{
    Id = id;

    // Mapping.
    GroupToArchetype = new Dictionary<int, Archetype>(archetypeCapacity);

    // Entity stuff.
    Archetypes = new Archetypes(archetypeCapacity);
    EntityInfo = new EntityInfoStorage(baseChunkSize, entityCapacity);
    RecycledIds = new Queue<RecycledEntity>(entityCapacity);

    // Query.
    QueryCache = new Dictionary<QueryDescription, Query>(archetypeCapacity);

    // Multithreading/Jobs.
    JobHandles = new NetStandardList<JobHandle>(Environment.ProcessorCount);
    JobsCache = new List<IJob>(Environment.ProcessorCount);

    // Config
    BaseChunkSize = baseChunkSize;
    BaseChunkEntityCount = baseChunkEntityCount;
}
```

构造函数是 `private`，外部只能通过 `World.Create` 工厂方法访问，这保证了**所有 World 都会被注册到全局 `_worlds`**（PURE_ECS 除外），不会出现"游离的 World"。

### 4.4.1 `GroupToArchetype` 字典的作用

📖 [World.cs L605](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L605)：`Dictionary<int, Archetype>`，键是 `Signature.GetHashCode()`（组件组合的哈希），值是对应的 `Archetype`。这让我们在 `Add`/`Remove` 组件时能 O(1) 查到目标原型是否已存在，避免重复创建。

### 4.4.2 `Archetypes`、`EntityInfo`、`RecycledIds` 的初始化

| 字段 | 类型 | 作用 |
|------|------|------|
| `Archetypes` | `Archetypes` | 所有原型的有序列表，查询时遍历它 |
| `EntityInfo` | `EntityInfoStorage` | 实体 Id → `EntityData`（Archetype + Slot + Version）的映射 |
| `RecycledIds` | `Queue<RecycledEntity>` | 已销毁实体的 Id+Version 队列，等待复用 |

初始容量都由构造参数控制，默认 `archetypeCapacity=2`、`entityCapacity=64`，对小项目足够；大批量创建时可显式传更大值。

---

## 4.5 核心 API 解析

### 4.5.1 `Create`（创建实体）—— 与 Archetype 协作

📖 [World.cs L312-338](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L312)

```csharp
[StructuralChange]
public Entity Create(in Signature types)
{
    // Create new entity and put it to the back of the array
    GetOrCreateEntityInternal(out var entity);

    // Add to archetype & mapping
    var archetype = GetOrCreate(in types);
    var allocatedEntities = archetype.Add(entity, out _, out var slot);

    // Resize map & Array to fit all potential new entities
    Capacity += allocatedEntities;
    EntityInfo.EnsureCapacity(Capacity);

    // Add entity to info storage
    EntityInfo.Add(entity.Id, archetype, slot, entity.Version);
    OnEntityCreated(entity);
    // ...
    return entity;
}
```

整个流程分四步：

1. **拿一个 Id**：`GetOrCreateEntityInternal` 优先从 `RecycledIds` 复用，否则用 `Size` 作为新 Id，Version 默认为 1。
2. **找/建原型**：`GetOrCreate(in types)` 用签名哈希查 `GroupToArchetype`，命中则复用，未命中则 `new Archetype(...)` 并加入 `Archetypes`。
3. **入原型**：`archetype.Add(entity, ...)` 把实体放入某个 `Chunk` 的 `Slot`，可能触发新 Chunk 分配，返回新分配的实体容量。
4. **登记映射**：扩容 `EntityInfo` 并写入 `(Id → Archetype, Slot, Version)`。

> ⚠️ 该方法带有 `[StructuralChange]` 特性，意味着**调用时禁止其他线程并发访问 World**。详见 4.7 节。

### 4.5.2 `Destroy`（销毁实体）—— 实体回收流程

📖 [World.cs L382-401](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L382)

```csharp
[StructuralChange]
public void Destroy(Entity entity)
{
    // ... EVENTS ...
    OnEntityDestroyed(entity);

    // Remove from archetype and move other entity to replace its slot
    ref var entityData = ref EntityInfo.GetEntityData(entity.Id);
    entityData.Archetype.Remove(entityData.Slot, out var movedEntityId);
    EntityInfo.Move(movedEntityId, entityData.Slot);

    DestroyEntityInternal(entity);
}
```

销毁流程的关键是 **"删除-交换"（swap-remove）**：

1. 从 `EntityInfo` 取出该实体所在的原型与槽位。
2. `Archetype.Remove(slot, out movedEntityId)`：把最后一个实体搬过来填补空洞，返回被搬移实体的 Id。
3. `EntityInfo.Move(movedEntityId, slot)`：更新被搬移实体的映射，让它指向新槽位。
4. `DestroyEntityInternal(entity)`：把被销毁实体的 `(Id, Version+1)` 入队 `RecycledIds`，并从 `EntityInfo` 移除，`Size--`。

> 🔥 swap-remove 是 ECS 的经典技巧：保证 Chunk 内实体连续无空洞，O(1) 删除。代价是实体在 Chunk 内的位置会变化，所以不能缓存实体的 Slot 引用。

📖 内部实现 [World.cs L266-285](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L266)：

```csharp
private void GetOrCreateEntityInternal(out Entity entity)
{
    var recycle = RecycledIds.TryDequeue(out var recycledId);
    var recycled = recycle ? recycledId : new RecycledEntity(Size, 1);
    entity = new Entity(recycled.Id, Id, recycled.Version);
    Size++;
}

private void DestroyEntityInternal(Entity entity)
{
    var recycledEntity = new RecycledEntity(entity.Id, unchecked(entity.Version + 1));
    RecycledIds.Enqueue(recycledEntity);
    EntityInfo.Remove(entity.Id);
    Size--;
}
```

> 💡 注意 `unchecked(entity.Version + 1)`：版本号溢出时不报错，回绕到负数。配合 `IsAlive` 的 Version 比较能正确处理。

### 4.5.3 `Query`（查询）—— 同步与并行两种模式

📖 [World.cs L411-424](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L411)

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
[Pure]
public Query Query(in QueryDescription queryDescription)
{
    var queryCache = QueryCache; // Storing locally to only access the QueryCache once
    if (queryCache.TryGetValue(queryDescription, out var query))
    {
        return query;
    }

    query = new Query(Archetypes, queryDescription);
    queryCache[queryDescription] = query;

    return query;
}
```

**同步查询**走 `QueryCache` 缓存：同一个 `QueryDescription` 只构建一次 `Query` 对象，后续直接复用。`[MethodImpl(MethodImplOptions.NoInlining)]` 防止这个相对少调用（缓存命中率高）的方法膨胀调用方代码。

**两种遍历模式**：

- **委托模式** `Query(QueryDescription, ForEach)` ([L758-770](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L758))：通过委托回调遍历实体，灵活但有委托调用开销。
- **内联模式** `InlineQuery<T>(QueryDescription)` ([L778-792](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L778))：用 `IForEach` 结构体泛型约束，JIT 能内联 `Update` 调用，性能接近手写循环。

> 🔥 并行查询需配合 `SharedJobScheduler` 与 `IJob`，详见第11章。核心思路：按 Chunk 切分任务，每个 Job 处理一组 Chunk，写入不同的内存区域避免竞争。

### 4.5.4 `Add` / `Set` / `Remove` / `Get` / `Has`

| 方法 | 性质 | 关键步骤 |
|------|------|----------|
| `Add<T>` | 结构变更 | 找/建目标原型 → `Move`（拷贝组件 + swap-remove 旧位置）→ 写入新组件 |
| `Set<T>` | 非结构变更 | `EntityInfo.GetEntityData` → `Archetype.Set`，原地替换 |
| `Remove<T>` | 结构变更 | 找/建目标原型 → `Move` → 不拷贝被移除类型的组件 |
| `Get<T>` | 非结构变更 | 取 `EntityData` → 返回 `ref T`，零拷贝 |
| `Has<T>` | 非结构变更 | 取 `Archetype` → 检查 `BitSet`，O(1) |

📖 以 `Set<T>` 为例 [World.cs L1113-1120](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L1113)：

```csharp
public void Set<T>(Entity entity, in T? component = default)
{
    var entitySlot = EntityInfo.GetEntityData(entity.Id);
    var slot = entitySlot.Slot;
    var archetype = entitySlot.Archetype;
    archetype.Set(ref slot, in component);
    OnComponentSet<T>(entity);
}
```

`Set` 不改变实体的原型归属，只是覆盖现有组件值，因此可以与其他线程的只读查询并发（详见 4.7）。

📖 `Add<T>` 的关键在 `Move` 方法 [World.cs L348-371](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L348)：它把实体从旧原型拷贝到新原型，并 `swap-remove` 旧位置。整个过程中实体的 `Id` 不变，但 `EntityData.Slot` 和 `EntityData.Archetype` 都会更新。

---

## 4.6 资源管理 —— `Dispose` 方法与 `using` 模式

📖 [World.cs L539-576](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L539)

```csharp
[StructuralChange]
public void Dispose()
{
    Dispose(true);
    GC.SuppressFinalize(this);
}

protected virtual void Dispose(bool disposing)
{
    if (_isDisposed) return;
    _isDisposed = true;

    var world = this;
#if !PURE_ECS
    lock (WorldsLock)
    {
        Volatile.Write(ref _worlds[world.Id], null!);
        RecycledWorldIds.Enqueue(world.Id);
        Interlocked.Decrement(ref worldSizeUnsafe);
    }
#endif

    world.Capacity = 0;
    world.Size = 0;
    world.JobHandles.Clear();
    world.GroupToArchetype.Clear();
    world.RecycledIds.Clear();
    world.QueryCache.Clear();
    world.Archetypes.Clear();
}
```

`Dispose` 是标准的"释放模式"：
1. 通过 `_isDisposed` 防重入。
2. 在 `WorldsLock` 内：把 `_worlds[Id]` 置 null、Id 入回收队列、计数减一。
3. 清空所有内部集合，让 GC 能回收 Chunk 数组等大块内存。

> ⚠️ 注意源码注释 [L578-582](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L578)：终结器 `~World()` 被注释掉了，原因是"it fails the WorldRecycle test"。**不要依赖终结器来释放 World**，必须显式 `Dispose` 或用 `using`：

```csharp
using var world = World.Create();
// ... 使用 world ...
// 离开作用域自动 Dispose
```

> 💡 `World.Destroy(world)` 静态方法只是 `world.Dispose()` 的语法糖 ([L159-162](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L159))，两者等价。

---

## 4.7 线程安全模型

📖 类注释 [World.cs L174-179](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L174) 明确指出：

> The World class is only thread-safe under specific circumstances. Read-only operations like querying entities can be done simultaneously by multiple threads. However, any method which mentions "structural changes" must not run alongside any other methods.

Arch 把操作分为两类：

| 类型 | 例子 | 并发规则 |
|------|------|----------|
| **非结构变更** | `Query`、`Get`、`Has`、`Set`、`InlineQuery` | 多线程可并发执行 |
| **结构变更** | `Create`、`Destroy`、`Add`、`Remove`、`Clear`、`Dispose` | **独占** World，不能与任何其他操作并发 |

结构变更方法都标记了 [StructuralChangeAttribute](file:///d:/Unity/Arch/Arch/src/Arch/Core/Utils/StructuralChangeAttribute.cs)。该特性有两个用途：
1. 文档提示开发者。
2. **源生成器**可识别它，在并行上下文中自动插入屏障或抛错。

> ⚠️ 为什么 `Set` 算非结构变更？因为它不改变实体所属原型、不增删组件、不移动 Slot。但它会写入组件内存，**与另一个线程对同一实体的 `Get` 仍存在数据竞争**。实践中要求：并行 Job 间不写同一个组件，或写不同实体。

### 实战建议

- **主线程**做结构变更（创建/销毁/增删组件），帧内收集到 `CommandBuffer`。
- **Worker 线程**只做查询与 `Set`，且每个 Job 写不同的 Chunk。
- 结构变更与查询之间用 `JobHandle.Complete()` 同步。

---

## 4.8 配套示例

📖 完整示例见 `Assets/Scripts/Chapter04/WorldDemo.cs`，演示 World 的生命周期与基本操作：

```csharp
using Arch.Core;

public class WorldDemo
{
    public static void Run()
    {
        // 1. 创建世界（带初始容量提示）
        using var world = World.Create(
            chunkSizeInBytes: 16_384,
            minimumAmountOfEntitiesPerChunk: 128,
            archetypeCapacity: 4,
            entityCapacity: 256);

        // 2. 创建实体
        var entity = world.Create(typeof(Position), typeof(Velocity));
        world.Set(entity, new Position { X = 0, Y = 0 });
        world.Set(entity, new Velocity { Dx = 1, Dy = 0 });

        // 3. 查询
        var desc = new QueryDescription().WithAll<Position, Velocity>();
        world.InlineQuery<MovementJob>(desc);

        // 4. 结构变更：动态加组件
        world.Add<NameTag>(entity);
        System.Console.WriteLine($"Has NameTag: {world.Has<NameTag>(entity)}");

        // 5. 销毁与回收
        world.Destroy(entity);
        // 离开 using 作用域，World.Dispose 自动调用
    }
}

public struct Position { public float X, Y; }
public struct Velocity { public float Dx, Dy; }
public struct NameTag { public string Name; }

public struct MovementJob : IForEach
{
    public void Update(Entity entity) { /* 移动逻辑 */ }
}
```

运行后，`World.Worlds` 中对应槽位被置 null，Id 进入回收队列，再次 `World.Create` 会复用该 Id。

---

## 本章小结

| 主题 | 关键点 |
|------|--------|
| **World 角色** | ECS 根容器，持有 Archetype、EntityInfo、QueryCache |
| **`_worlds` 数组** | 按 World Id 索引的 O(1) 查表，初始容量 4 |
| **`WorldsLock`** | 专用 `readonly object`，避免数组扩容时锁失效 |
| **`RecycledWorldIds`** | PooledQueue<int>，回收已 Dispose 的 World Id |
| **`WorldSize`** | `Interlocked.CompareExchange` 原子读取计数 |
| **`SharedJobScheduler`** | 进程级共享 Job 调度器 |
| **`World.Create`** | 双分支：PURE_ECS 不入表；默认分支加锁+Volatile.Write 发布 |
| **`Volatile.Write`** | 防止 ARM64 弱内存模型下读者看到未初始化的数组内容 |
| **构造函数** | `private`，强制走 `World.Create` 工厂 |
| **`Create`/`Destroy`** | swap-remove 保证 Chunk 连续；Version+1 入 RecycledIds |
| **`Query`** | QueryCache 缓存；委托/内联两种遍历 |
| **`Set` vs `Add`** | `Set` 非结构变更可并发；`Add` 结构变更需独占 |
| **`Dispose`** | 释放全局槽位、清空集合；`using` 模式推荐 |
| **线程模型** | 非结构变更可多线程并发；结构变更必须独占 |

> 📖 下一章我们将深入 [Entity.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs)，看一个 12 字节的结构体如何承载实体的全部身份信息。
