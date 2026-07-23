# 第07章 Archetype与Chunk源码解析

> 📖 上一章我们认识了 Component 与 ComponentRegistry——它们回答了"组件是什么、如何被注册"。本章我们将深入 ECS 框架最核心的两个数据结构：**Archetype（原型）**与 **Chunk（内存块）**——它们回答了"实体和组件如何在内存中布局，如何被高效遍历"。

源码参考：
- [Archetype.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs)
- [Chunk.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs)
- [Settings.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Settings.cs)
- [Archetype.Edges.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Edges/Archetype.Edges.cs)

---

## 7.1 Archetype概念

**Archetype（原型）** 是 Arch ECS 框架的核心数据结构，它包含所有**拥有相同组件组合**的实体。

举例说明：假设你的游戏里有以下实体：

| 实体 | 组件组合 |
|------|----------|
| Player | `Position` + `Velocity` + `Health` + `Sprite` |
| Enemy1 | `Position` + `Velocity` + `Health` + `Sprite` |
| Enemy2 | `Position` + `Velocity` + `Health` + `Sprite` |
| Bullet | `Position` + `Velocity` |
| Background | `Position` + `Sprite` |

那么 World 内会有 **3 个不同的 Archetype**：
1. `{Position, Velocity, Health, Sprite}` —— 容纳 Player、Enemy1、Enemy2
2. `{Position, Velocity}` —— 容纳 Bullet
3. `{Position, Sprite}` —— 容纳 Background

🔥 **为什么这样组织？**

1. **批量遍历**：当一个 System 想处理"所有能动的实体"（即同时有 `Position` 和 `Velocity`），它只需要查询 `{Position, Velocity}` 和 `{Position, Velocity, Health, Sprite}` 两个 Archetype，而不需要遍历所有实体。
2. **内存紧凑**：相同组件组合的实体存放在连续内存中（Structure of Arrays 布局），CPU 缓存命中率极高。
3. **快速查询**：每个 Archetype 有一个 `BitSet` 表示自己持有哪些组件，Query 时只需一次位运算就能判定是否匹配。

---

## 7.2 Chunk概念

**Chunk（内存块）** 是 Archetype 内部的物理存储单元。一个 Archetype 持有**多个 Chunk**，每个 Chunk 装载一定数量的实体。

源码位置：[Settings.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Settings.cs)

```csharp
public struct ChunkLayoutConfig()
{
    public ChunkLayoutMode LayoutMode { get; set; } = ChunkLayoutMode.MinChunkSizeAndMinEntities;

    public int FixedChunkSize { get; set; } = 32 * 1024;        // 32 KB
    public int FixedEntityCount { get; set; } = 256;

    public int MinChunkSize { get; set; } = 16 * 1024;          // 16 KB
    public int MinEntityCount { get; set; } = 64;
}
```

💡 **三种布局模式**：
- `FixedChunkSize`：固定 Chunk 字节数（如 16KB），实体数随组件大小变化。
- `FixedEntityCount`：固定每 Chunk 实体数（如 256 个），字节数随组件大小变化。
- `MinChunkSizeAndMinEntities`：**软约束**——同时满足最小字节数和最小实体数。这是默认模式。

🔥 **为什么默认 16KB？** 因为现代 Intel CPU 的 L1d cache 通常是 **32KB**（每核），16KB 的 Chunk 可以保证**至少半个 Chunk 同时驻留在 L1d**，对线性遍历非常友好。后续会详细说明。

---

## 7.3 Slot结构体 —— 实体在Archetype中的位置

源码位置：[Archetype.cs L18-L115](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L18)

```csharp
[SkipLocalsInit]
public record struct Slot
{
    /// <summary> The index of the Entity in the Chunk. </summary>
    public int Index;            // L24

    /// <summary> The index of the Chunk in which the Entity is located. </summary>
    public int ChunkIndex;       // L29

    public Slot(int index, int chunkIndex)
    {
        Index = index;
        ChunkIndex = chunkIndex;
    }
    ...
}
```

`Slot` 是一个**二维坐标**——`(Index, ChunkIndex)` 唯一确定一个实体在 Archetype 中的位置：
- `ChunkIndex`：实体所在的 Chunk 在 `Chunks` 数组中的下标。
- `Index`：实体在该 Chunk 内的具体位置。

💡 **为什么需要二维坐标？** 因为单个 Chunk 容量有限（16KB / 实体大小），当实体数超过 `EntitiesPerChunk` 时就必须开新 Chunk。Slot 的二维结构允许 `EntityInfo` 通过一个 8 字节结构（两个 int）直接定位实体。

### 7.3.1 Wrap方法 —— 跨Chunk边界处理

源码位置：[L68-L82](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L68)

```csharp
public void Wrap(int capacity)
{
    // Result outside valid chunk, wrap into next one
    if (Index < capacity)
    {
        return;
    }

    // Index outside of its chunk, so we calculate how many times a chunk fit into the index
    // for adjusting the chunkindex to that position.
    ChunkIndex += (int)Math.Floor(Index / (float)capacity);

    // After moving the chunk index we can simply take the rest and assign it as a index.
    Index %= capacity;
}
```

`Wrap` 的作用是：当 `Index` 超过单个 Chunk 的容量时，把它"折叠"到下一个 Chunk。这是 ECS 框架在批量插入/迭代时常用的小技巧。

**举例**：假设 `capacity = 100`，当前 `Slot = (Index: 250, ChunkIndex: 0)`：
- `Index >= capacity`，需要 wrap。
- `ChunkIndex += floor(250 / 100) = 2` → `ChunkIndex = 2`。
- `Index = 250 % 100 = 50` → `Index = 50`。
- 最终：`Slot = (Index: 50, ChunkIndex: 2)`，即"第 2 号 Chunk 的第 50 个位置"。

🔥 **设计巧思**：用 `Math.Floor` 和取模运算，把一个线性偏移拆解成二维坐标。这在批量插入时极为高效——不需要逐个判断"是否跨 Chunk"。

### 7.3.2 Shift方法 —— 实体迁移计算

源码位置：[L91-L114](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L91)

`Shift` 有两个重载：

```csharp
// 单参版本：把 Slot 向前移动一位
public static Slot Shift(ref Slot source, int sourceCapacity)
{
    source.Index++;
    source.Wrap(sourceCapacity);
    return source;
}

// 多参版本：根据 destination 计算源 Slot 的最终落点
public static Slot Shift(in Slot source, int sourceCapacity, in Slot destination, int destinationCapacity)
{
    var freeSpot = destination;
    var resultSlot = source + freeSpot;
    resultSlot.Index += source.ChunkIndex * (sourceCapacity - destinationCapacity);
    resultSlot.Wrap(destinationCapacity);

    return resultSlot;
}
```

💡 **多参版本的用途**：当批量从源 Archetype 复制实体到目标 Archetype 时，由于两个 Archetype 的 `EntitiesPerChunk` 可能不同（组件结构不同导致容量差异），需要换算"源 Slot 的实体去到目标 Archetype 的什么位置"。

`source.ChunkIndex * (sourceCapacity - destinationCapacity)` 是关键的容量差补偿项——它确保跨 Chunk 的偏移被正确映射到目标 Archetype 的 Chunk 结构上。

---

## 7.4 Archetypes类 —— Archetype集合的缓存哈希

源码位置：[L122-L254](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L122)

```csharp
public class Archetypes : IDisposable
{
    private int _hashCode;                    // L127

    public Archetypes(int capacity)
    {
        Items = new NetStandardList<Archetype>(capacity);
        _hashCode = -1;                       // L136: -1 表示"脏"
    }

    public NetStandardList<Archetype> Items { get; }   // L142

    public void Add(Archetype archetype)
    {
        Items.Add(archetype);
        _hashCode = -1;                       // L162: 标记为脏
        GetHashCode();                        // L163: 立即重算并缓存
    }
    ...
}
```

### 7.4.1 缓存哈希机制

源码位置：[L220-L237](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L220)

```csharp
public override int GetHashCode()
{
    // Cached hashcode, return
    if (_hashCode != -1)
    {
        return _hashCode;
    }

    // Calculate and cache hashcode
    var hash = 17;
    foreach (var item in Items)
    {
        hash = (hash * 31) + (item?.GetHashCode() ?? 0);
    }

    _hashCode = hash;
    return hash;
}
```

🔥 **缓存策略**：
- 用 `-1` 作为"脏标记"——只要 `Items` 变化（Add/Remove），就把 `_hashCode` 置为 -1。
- 下次访问 `GetHashCode` 时，发现脏标记则重算，否则直接返回缓存值。
- 这是经典的 **lazy invalidation** 模式——写入时只标记，读取时才真正重算。

💡 **为什么要缓存哈希？** `Archetypes` 集合可能被多个 Query 缓存用作"是否需要刷新查询结果"的指纹。如果每次访问都重新遍历计算，开销会很大。缓存后，每次访问是 O(1)，只有结构性变更时才付出 O(n) 重算代价。

---

## 7.5 Archetype类的核心字段

源码位置：[L261-L396](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L261)

```csharp
public sealed partial class Archetype
{
    // 查找数组：组件 Id → 组件在 Components 数组中的下标
    private readonly int[] _componentIdToArrayIndex;          // L267

    internal Archetype(Signature signature, int baseChunkSize, int baseChunkEntityCount)
    {
        Signature = signature;
        BaseChunkSize = baseChunkSize;

        // 计算 Chunk 大小和每 Chunk 实体数
        ChunkSize = GetChunkSizeInBytesFor(baseChunkSize, baseChunkEntityCount, signature);
        EntitiesPerChunk = GetEntityCountFor(ChunkSize, signature);

        // 位图与查找数组
        BitSet = signature;                                  // L285
        _componentIdToArrayIndex = signature.Components.ToLookupArray();

        // 初始化 Chunk 数组并放入第一个空 Chunk
        Chunks = new Chunks(1);
        AddChunk();

        // 边缘缓存（用于加速结构变更）
        _addEdges = new SparseJaggedArray<Archetype>(BucketSize);
        _removeEdges = new SparseJaggedArray<Archetype>(BucketSize);
    }
    ...
}
```

### 7.5.1 核心字段一览

| 字段 | 类型 | 作用 |
|------|------|------|
| `Chunks` | `Chunks` | 实际存储实体的 Chunk 数组（[L336](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L336)） |
| `BitSet` | `BitSet` | 组件位图，第 i 位 = 1 表示持有 Id = i 的组件（[L322](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L322)） |
| `Signature` | `Signature` | 组件签名（包含 ComponentType[]），用于复制组件（[L317](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L317)） |
| `_componentIdToArrayIndex` | `int[]` | 组件 Id → Chunk 内组件数组下标的查找表（[L267](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L267)） |
| `_addEdges` / `_removeEdges` | `SparseJaggedArray<Archetype>` | 边缘缓存，加速结构变更（[L292-L293](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L292)） |
| `ChunkSize` | `int` | 实际 Chunk 字节数（[L307](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L307)） |
| `EntitiesPerChunk` | `int` | 每 Chunk 能容纳的实体数（[L312](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L312)） |
| `EntityCount` | `int` | 当前实体总数（[L384](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L384)） |
| `Count` | `int` | 当前最后一个非空 Chunk 的下标（[L362](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L362)） |

### 7.5.2 _componentIdToArrayIndex —— O(1)组件查找

🔥 这是一个非常关键的优化。假设 Archetype 的 Signature 是 `{Position(Id=0), Velocity(Id=2), Sprite(Id=5)}`，那么 `Components` 数组只有 3 个元素：

```
Components[0] = Position[]  ← 索引 0
Components[1] = Velocity[]  ← 索引 1
Components[2] = Sprite[]    ← 索引 2
```

但 Chunk 在 `Get<T>()` 时只知道组件 `Id`（如 `Id=5`），如何快速找到对应的 `Sprite[]`？如果用 `Dictionary<int, int>` 查找，每次都要 hash。

`_componentIdToArrayIndex` 是一个**稀疏数组**：

```
_componentIdToArrayIndex[0] = 0   (Position → Components[0])
_componentIdToArrayIndex[1] = -1  (不存在)
_componentIdToArrayIndex[2] = 1   (Velocity → Components[1])
_componentIdToArrayIndex[3] = -1
_componentIdToArrayIndex[4] = -1
_componentIdToArrayIndex[5] = 2   (Sprite → Components[2])
```

访问时：`arrayIndex = _componentIdToArrayIndex[componentId]` —— **一次数组下标访问，无 hash 开销**。这正是 [Chunk.cs L394-L399](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L394) 中 `Index<T>()` 的实现：

```csharp
private int Index<T>()
{
    var id = Component<T>.ComponentType.Id;
    return ComponentIdToArrayIndex.DangerousGetReferenceAt(id);
}
```

💡 注意：`_componentIdToArrayIndex` 是在 Archetype 层创建的，然后**所有 Chunk 共享同一个实例**（[L263-L266 注释](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L263)），避免每个 Chunk 都重新分配一份。

### 7.5.3 Edges边缘缓存 —— 加速结构变更

源码位置：[Archetype.Edges.cs L8-L51](file:///d:/Unity/Arch/Arch/src/Arch/Core/Edges/Archetype.Edges.cs#L8)

```csharp
public partial class Archetype
{
    private const int BucketSize = 16;

    /// <summary> Caches other Archetypes indexed by the ComponentType.Id that needs to be added. </summary>
    private readonly SparseJaggedArray<Archetype> _addEdges;

    /// <summary> Caches other Archetypes indexed by the ComponentType.Id that needs to be removed. </summary>
    private readonly SparseJaggedArray<Archetype> _removeEdges;

    internal void AddAddEdge(int index, Archetype archetype) { ... }
    internal void AddRemoveEdge(int index, Archetype archetype) { ... }
    internal Archetype GetAddEdge(int index) => _addEdges[index];
    internal Archetype GetRemoveEdge(int index) => _removeEdges[index];
    ...
}
```

🔥 **边缘缓存解决什么问题？**

当用户给实体添加一个新组件（如 `world.Add<BossTag>(entity)`），实体需要从当前 Archetype 迁移到新的 Archetype。这面临两个问题：
1. **如何找到目标 Archetype？** 朴素做法：遍历 World 中所有 Archetype，逐个比对 BitSet —— O(N)。
2. **目标 Archetype 不存在怎么办？** 需要创建新 Archetype，并把所有相关 Chunk、ArrayRegistry 等都设置好 —— 极慢。

**Edges 机制**：每个 Archetype 维护两个稀疏锯齿数组，**以"要添加/删除的组件 Id"为索引**缓存目标 Archetype。下次同样的迁移只需 O(1) 查表。

举例：
- Archetype A = `{Position, Velocity}`，给其中实体添加 `Health` → 迁移到 Archetype B = `{Position, Velocity, Health}`。
- 第一次迁移时，A 没有对应的 `_addEdges[Health.Id]`，需要全局查找或创建 B，然后缓存：`A._addEdges[Health.Id] = B`。
- 第二次再有 A 中的实体添加 `Health`，直接 `A._addEdges[Health.Id]` 拿到 B —— O(1)。

💡 这与 Unity DOTS 的 EntityArchetype 上的 "Adjacency List" 概念一致，是 ECS 框架的标配优化。

---

## 7.6 Archetype的Add/Remove/Set/Get/Has方法

### 7.6.1 Add方法 —— 触发实体迁移

源码位置：[L430-L467](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L430)

```csharp
internal int Add(Entity entity, out Chunk chunk, out Slot slot)
{
    EntityCount++;

    var count = Count;
    ref var currentChunk = ref GetChunk(count);

    // 1. 当前 Chunk 还有空位，直接放进
    if (currentChunk.IsEmpty)
    {
        slot = new Slot(currentChunk.Add(entity), count);
        chunk = currentChunk;
        return 0;
    }

    // 2. 当前 Chunk 满了，但 Chunks 数组中还有预留空位，切到下一个
    count++;
    if (count < ChunkCapacity)
    {
        currentChunk = ref GetChunk(count);
        slot = new Slot(currentChunk.Add(entity), count);
        chunk = currentChunk;
        Count = count;
        return 0;
    }

    // 3. 所有 Chunk 都满了，分配新 Chunk
    ref var newChunk = ref AddChunk();
    slot = new Slot(newChunk.Add(entity), count);
    chunk = newChunk;
    Count = count;

    return EntitiesPerChunk;   // 返回新分配的实体容量
}
```

🔥 **三级容量策略**：
1. **Chunk 内有空位** → 直接 `chunk.Add()`，零分配。
2. **Chunk 满但 Chunks 数组有预留** → 切换到下一个 Chunk（Chunks 是预分配的池）。
3. **Chunks 数组也满** → 调用 `AddChunk()` 分配新 Chunk，返回新增容量供上层记账。

`AddChunk` 内部通过 `ArrayPool<Chunk>` 池化分配（见 7.7 节），避免 GC。

### 7.6.2 Remove方法 —— 用最后一个实体填补空位

源码位置：[L507-L523](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L507)

```csharp
internal void Remove(Slot slot, out int movedEntityId)
{
    // 把最后一个 Chunk 的最后一个实体搬到被删除位置
    ref var chunk = ref GetChunk(slot.ChunkIndex);
    ref var lastChunk = ref CurrentChunk;

    movedEntityId = chunk.Transfer(slot.Index, ref lastChunk);
    EntityCount--;

    // 如果最后一个 Chunk 现在空了，回收它
    if (lastChunk.Count > 0 || Count <= 0)
    {
        return;
    }

    Count--;
}
```

🔥 **经典 ECS 删除技巧**：
- 直接删除中间实体会在 Chunk 内留下"空洞"，导致后续遍历需要判断"这个槽位是否有效"。
- **解决方案**：把 Chunk 内最后一个实体搬到被删位置，然后 `Count--`。这样所有有效实体始终在 `[0, Count)` 区间内紧凑排列。
- `movedEntityId` 被返回给上层，因为搬家的实体其 `Slot` 已变，需要更新 `EntityInfo` 表。

⚠️ **副作用**：实体的"物理顺序"会随删除操作变化。System 在遍历中删除实体时要小心索引越界——通常的做法是倒序遍历或批量记录后再删。

### 7.6.3 Set/Get/Has方法

源码位置：[L557-L630](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L557)

```csharp
internal void Set<T>(ref Slot slot, in T? cmp)
{
    ref var chunk = ref GetChunk(slot.ChunkIndex);
    chunk.Copy(slot.Index, in cmp);   // 转发给 Chunk
}

internal ref T Get<T>(scoped ref Slot slot)
{
    ref var chunk = ref GetChunk(slot.ChunkIndex);
    return ref chunk.Get<T>(slot.Index);
}

public bool Has<T>()
{
    var id = Component<T>.ComponentType.Id;
    return BitSet.IsSet(id);          // 位运算判定
}
```

💡 这些方法的核心都是 **(1) 根据 ChunkIndex 找到 Chunk → (2) 根据 Index 在 Chunk 内定位**。所有重活都委托给 Chunk，Archetype 只负责"哪个 Chunk"。

`Has<T>()` 完全不查 Chunk，只看 `BitSet`，这是 O(1) 的位运算。

---

## 7.7 Chunk类 —— 16KB固定大小的内存块

源码位置：[Chunk.cs L20-L137](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L20)

### 7.7.1 Chunks管理类

```csharp
public class Chunks
{
    public Chunks(int capacity = 1)
    {
        Items = ArrayPool<Chunk>.Shared.Rent(capacity);   // L28: 池化分配
        Count = 0;
        Capacity = capacity;
    }

    private Array<Chunk> Items { get; set; }
    public int Count { get; set; }
    public int Capacity { get; private set; }

    public void Add(in Chunk chunk)
    {
        Debug.Assert(Count + 1 <= Capacity, "Capacity exceeded.");
        Items[Count++] = chunk;
    }

    public void EnsureCapacity(int newCapacity)
    {
        if (newCapacity <= Capacity) return;

        var sourceArray = Items;
        var destinationArray = (Array<Chunk>)ArrayPool<Chunk>.Shared.Rent(newCapacity);
        Arch.LowLevel.Array.Copy(ref sourceArray, 0, ref destinationArray, 0, Capacity);
        ArrayPool<Chunk>.Shared.Return(sourceArray, true);   // L72: 旧数组归还池

        Items = destinationArray;
        Capacity = newCapacity;
    }
    ...
}
```

🔥 **池化设计**：
- `Chunks` 内部的 `Items` 数组从 `ArrayPool<Chunk>.Shared` 租借，**不直接走 GC 堆**。
- 扩容时新数组也从池借，旧数组归还。
- 这样即使 Archetype 频繁创建销毁 Chunk 数组，**也不会触发 Gen0/Gen1 GC**。

### 7.7.2 Chunk结构体本身

源码位置：[L139-L368](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L139)

```csharp
[SkipLocalsInit]
public partial struct Chunk
{
    public readonly Entity[] Entities { get; }                  // L185: 实体数组
    public readonly Array[] Components { get; }                 // L192: 每种组件一个数组 (SoA)
    public readonly int[] ComponentIdToArrayIndex { get; }      // L197: 共享的查找表
    public int Count { get; internal set; }                     // L202: 已用槽位数
    public int Capacity { get; }                                // L207: 总容量

    public readonly int Buffer => Capacity - Count;             // L212: 剩余空间
    public readonly bool IsFull => Count >= Capacity;
    public readonly bool IsEmpty => Count < Capacity;

    internal Chunk(int capacity, int[] componentIdToArrayIndex, Span<ComponentType> types)
    {
        Count = 0;
        Capacity = capacity;

        Entities = new Entity[Capacity];
        Components = new Array[types.Length];

        ComponentIdToArrayIndex = componentIdToArrayIndex;
        for (var index = 0; index < types.Length; index++)
        {
            var type = types[index];
            Components[index] = ArrayRegistry.GetArray(type, Capacity);   // L176: 用工厂创建
        }
    }
    ...
}
```

🔥 **Structure of Arrays (SoA) 布局**：

假设 Archetype 的 Signature 是 `{Position, Velocity, Sprite}`，`Capacity = 100`，那么 Chunk 内部布局是：

```
Entities:    [Entity0, Entity1, ..., Entity99]      ← 100 个 Entity
Components:
  [0]: Position[] = [Pos0, Pos1, ..., Pos99]        ← 100 个 Position
  [1]: Velocity[] = [Vel0, Vel1, ..., Vel99]        ← 100 个 Velocity
  [2]: Sprite[]   = [Spr0, Spr1, ..., Spr99]        ← 100 个 Sprite
```

而不是 AoS（Array of Structures）：

```
Entities[0] = { Entity0, Pos0, Vel0, Spr0 }   ← 一个实体的数据全在一起
Entities[1] = { Entity1, Pos1, Vel1, Spr1 }
...
```

💡 **为什么 SoA 更好？**
- 当 System 只读写 `Position` 时，CPU 加载缓存行只会把 `Position[]` 拉进 L1d，**不会污染缓存**。
- AoS 会把无关组件（如 `Sprite`）也拉进缓存，浪费带宽。
- SoA 还能利用 SIMD（如 `Vector<float>`）进行批量计算。

### 7.7.3 Chunk的Add/Get/Set/Remove

```csharp
internal int Add(Entity entity)
{
    var size = Count;
    Entity(size) = entity;
    Count = size + 1;
    return size;
}

public void Copy<T>(int index, in T cmp)
{
    ref var item = ref GetFirst<T>();
    Unsafe.Add(ref item, index) = cmp;     // 零开销索引
}

public ref T Get<T>(int index)
{
    ref var item = ref GetFirst<T>();
    return ref Unsafe.Add(ref item, index);
}

public bool Has<T>()
{
    var id = Component<T>.ComponentType.Id;
    return Has(id);
}

public bool Has(int id)
{
    var idToArrayIndex = ComponentIdToArrayIndex;
    return id < idToArrayIndex.Length && idToArrayIndex.DangerousGetReferenceAt(id) != -1;
}
```

`DangerousGetReferenceAt` 是 CommunityToolkit 提供的**无边界检查数组访问**——JIT 编译后等同于指针偏移。

### 7.7.4 Transfer方法 —— 跨Chunk搬家

源码位置：[L650-L667](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L650)

```csharp
internal int Transfer(int index, ref Chunk chunk)
{
    // 取最后一个实体
    var lastIndex = chunk.Count - 1;
    var lastEntity = chunk.Entity(lastIndex);

    // 把它搬到本 Chunk 的 index 位置
    Entity(index) = lastEntity;
    for (var i = 0; i < Components.Length; i++)
    {
        var sourceArray = chunk.Components[i];
        var desArray = Components[i];
        Array.Copy(sourceArray, lastIndex, desArray, index, 1);
    }

    chunk.Count--;       // 源 Chunk 计数减一
    return lastEntity.Id;   // 返回被搬家的实体 Id，供 EntityInfo 更新
}
```

这是 `Archetype.Remove` 内部调用的核心方法。它把 `chunk`（最后一个 Chunk）的最后一个实体搬到 `this`（被删除位置所在的 Chunk）的 `index` 位置。

🔥 **关键设计**：`Transfer` 内部用 `Array.Copy` 拷贝每个组件数组的一个元素，**而非逐字段拷贝**。`Array.Copy` 在 .NET 中是 highly optimized（直接走内存移动），比手动 for 循环快得多。

---

## 7.8 Archetype间的实体迁移流程

当用户给一个实体添加/删除组件时，整个迁移流程如下（以 `world.Add<BossTag>(entity)` 为例）：

### 7.8.1 总体步骤

```
1. 通过 Entity.Id 查 EntityInfo → 拿到当前 Archetype + Slot
2. 在当前 Archetype 的 _addEdges[BossTag.Id] 查找目标 Archetype
   ├─ 命中：直接拿到目标 Archetype
   └─ 未命中：通过 World 查找或创建新 Archetype，缓存到 _addEdges
3. 在目标 Archetype 中 Add(entity) → 拿到新 Slot
4. 用 Chunk.CopyComponents 把旧组件数据搬到新 Chunk
5. 在旧 Archetype 中 Remove(oldSlot) → 用最后一个实体填补空位
6. 更新 EntityInfo：记录新 Archetype + 新 Slot
7. 如果旧 Archetype 删除时搬家了别的实体，更新被搬家实体的 EntityInfo
```

### 7.8.2 关键源码：CopyComponents

源码位置：[L621-L641](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L621)

```csharp
internal static void CopyComponents(ref Chunk source, int index, ref Signature sourceSignature,
                                    ref Chunk destination, int destinationIndex, int length)
{
    var sourceComponents = source.Components;

    for (var i = 0; i < sourceComponents.Length; i++)
    {
        var sourceArray = sourceComponents[i];
        var sourceType = sourceSignature.Components[i];

        // 目标 Chunk 没有这个组件？跳过（如源有 Sprite，目标无）
        if (!destination.TryIndex(sourceType.Id, out var arrayIndex))
        {
            continue;
        }

        var destinationArray = destination.Components.DangerousGetReferenceAt(arrayIndex);
        Array.Copy(sourceArray, index, destinationArray, destinationIndex, length);
    }
}
```

🔥 **组件匹配逻辑**：
- 遍历源 Chunk 的所有组件数组。
- 对每个组件，查询目标 Chunk 是否也有（通过 `TryIndex`）。
- 有则拷贝，没有则跳过——这天然支持"添加组件"和"删除组件"两种场景。
- 新组件（如 `BossTag`）的初值由调用方在迁移后通过 `Set<T>` 写入。

### 7.8.3 EntityInfo的更新

迁移完成后，被迁移实体的 `EntityInfo`（含 `Archetype` 引用与 `Slot`）需要更新；如果旧 Archetype 删除时搬家了别的实体，那个被搬家实体的 `Slot` 也要更新。

`Archetype.Remove` 返回的 `movedEntityId` 就是用来通知上层"还有别的实体 Slot 变了"。World 层会通过 `EntityInfo` 表（用 `SparseJaggedArray` 或类似结构）批量更新。

---

## 7.9 性能考量

### 7.9.1 为什么16KB？

🔥 **L1d cache 友好**：
- Intel/AMD 现代 CPU 的 L1d cache 通常是 **32KB / 核**。
- 16KB Chunk 占据 L1d 的一半，**至少半个 Chunk 始终驻留**。
- 线性遍历 Chunk 时，CPU prefetcher 能完美预测访问模式，缓存命中率高。

**反例**：如果 Chunk 是 1MB，那么遍历时会出现大量 L1d/L2 cache miss，性能急剧下降。

**ChunkSize 计算**：见 [L806-L810](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L806)：

```csharp
public unsafe static int GetChunkSizeInBytesFor(int baseChunkSize, int entityAmount, Span<ComponentType> types)
{
    var entityBytes = (sizeof(Entity) + types.ToByteSize()) * entityAmount;
    return (int)Math.Ceiling((float)entityBytes / baseChunkSize) * baseChunkSize;
    // 向上取整到 baseChunkSize 的整数倍
}
```

💡 即使你指定 `MinEntityCount = 64`，实际 `ChunkSize` 仍会被**向上取整**到 16KB 的倍数，确保 L1d 友好性。

### 7.9.2 为什么用ArrayPool？

🔥 **避免 GC**：
- Archetype 在游戏运行中会频繁创建/销毁（实体组件变化触发）。
- 如果每次都 `new Chunk[...]`，会触发大量 Gen0 GC，可能引起帧率抖动。
- `ArrayPool<Chunk>.Shared` 是 .NET 内置的线程安全池，**租借和归还是 O(1)**，且**不会触发 GC**。

源码：[Chunk.cs L28, L70-L75, L87-L92](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L28)

```csharp
// 租借
Items = ArrayPool<Chunk>.Shared.Rent(capacity);

// 扩容时归还旧数组
ArrayPool<Chunk>.Shared.Return(sourceArray, true);

// TrimExcess 时也归还
var newChunks = ArrayPool<Chunk>.Shared.Rent(minimalSize);
Array.Copy(Items, newChunks, minimalSize);
ArrayPool<Chunk>.Shared.Return(Items, true);
```

### 7.9.3 为什么Chunk是struct而不是class？

源码：[Chunk.cs L145](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L145)

```csharp
[SkipLocalsInit]
public partial struct Chunk
```

- struct 是值类型，**不会触发 GC**。
- `[SkipLocalsInit]` 跳过局部变量零初始化，进一步提升性能。
- `Chunks` 数组里存的就是 struct 实例（虽然内部字段都是引用，但 struct 本身的"壳"是连续内存）。

⚠️ 注意：Chunk 内部的 `Entities`、`Components` 等数组依然是引用类型，它们本身在堆上。但 Chunk **结构本身** 在 Chunks 数组中是连续布局的，这对缓存局部性有帮助。

### 7.9.4 EnsureCapacity 与 TrimExcess

源码：[Chunk.cs L62-L93](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L62)

```csharp
public void EnsureCapacity(int newCapacity)
{
    if (newCapacity <= Capacity) return;
    // 借新数组 → 拷贝 → 还旧数组
}

public void TrimExcess()
{
    // 至少留一个 Chunk
    var minimalSize = Count > 0 ? Count : 1;

    var newChunks = ArrayPool<Chunk>.Shared.Rent(minimalSize);
    Array.Copy(Items, newChunks, minimalSize);
    ArrayPool<Chunk>.Shared.Return(Items, true);

    Items = newChunks;
    Capacity = minimalSize;
}
```

Archetype 的 `TrimExcess` 调用 Chunks 的 `TrimExcess`（[L788-L792](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L788)）：

```csharp
internal void TrimExcess()
{
    Chunks.Count = Count + 1;   // 先把 Count 设为"实际需要的"
    Chunks.TrimExcess();
}
```

💡 **使用场景**：
- 大量实体被删除后，Archetype 持有许多空 Chunk，内存浪费严重。
- 调用 `TrimExcess` 归还多余 Chunk，节省内存。
- 适合在关卡切换、加载完成等"内存整理"时机调用。

---

## 7.10 配套示例

完整示例代码位于：`Assets/Scripts/Chapter07/ArchetypeDemo.cs`

示例涵盖：
1. 创建 World 与多个实体。
2. 给一组实体相同组件组合，验证它们落入同一 Archetype。
3. 通过 `world.Add<T>` / `world.Remove<T>` 触发实体迁移，验证 Slot 变化。
4. 用 `archetype.ChunkCount`、`archetype.EntityCount`、`archetype.EntitiesPerChunk` 观察内存布局。
5. 演示 `TrimExcess` 在大批量删除后的内存回收效果。

```csharp
// 示例片段（完整代码见配套脚本）
public readonly record struct Position(float X, float Y, float Z);
public readonly record struct Velocity(float X, float Y, float Z);
public readonly record struct Health(int Current, int Max);

var world = World.Create();

// 创建 1000 个相同组件结构的实体 → 落入同一 Archetype
var entities = new Entity[1000];
for (int i = 0; i < 1000; i++)
{
    entities[i] = world.Create(
        new Position(i, 0, 0),
        new Velocity(1, 0, 0),
        new Health(100, 100)
    );
}

// 查看第一个实体所在的 Archetype
ref var info = ref world.GetEntityInfo(entities[0]);
var archetype = info.Archetype;

Debug.Log($"Archetype: {archetype}");
Debug.Log($"  ChunkCount: {archetype.ChunkCount}");
Debug.Log($"  EntitiesPerChunk: {archetype.EntitiesPerChunk}");
Debug.Log($"  EntityCount: {archetype.EntityCount}");
Debug.Log($"  ChunkSize: {archetype.ChunkSize} bytes");

// 给某个实体添加新组件 → 触发迁移到新 Archetype
world.Add(new BossTag(), entities[0]);

// 验证 entities[0] 现在在新 Archetype 中
ref var newInfo = ref world.GetEntityInfo(entities[0]);
Debug.Assert(newInfo.Archetype != archetype, "Should have moved to a new archetype!");
```

---

## 本章小结

| 概念 | 位置 | 作用 |
|------|------|------|
| `Archetype` | [L261](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L261) | 同组件组合实体的逻辑集合 |
| `Chunk` | [Chunk.cs L145](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L145) | 16KB 固定大小的物理存储块 |
| `Slot` | [L19](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L19) | 实体在 Archetype 中的二维坐标 (Index, ChunkIndex) |
| `Slot.Wrap` | [L68](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L68) | 跨 Chunk 边界时的坐标折叠 |
| `Slot.Shift` | [L91, L106](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L91) | 实体迁移时的坐标换算 |
| `Archetypes` | [L122](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L122) | World 内所有 Archetype 的集合，带缓存哈希 |
| `Archetype.BitSet` | [L322](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L322) | 组件位图，O(1) 判定是否持有某组件 |
| `Archetype.Signature` | [L317](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L317) | 组件签名（含 ComponentType[]） |
| `_componentIdToArrayIndex` | [L267](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L267) | 组件 Id → Chunk 内组件数组下标的稀疏查找表 |
| `_addEdges` / `_removeEdges` | [Edges L20, L27](file:///d:/Unity/Arch/Arch/src/Arch/Core/Edges/Archetype.Edges.cs#L20) | 边缘缓存，加速结构变更 |
| `Archetype.Add` | [L430](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L430) | 添加实体，三级容量策略 |
| `Archetype.Remove` | [L507](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L507) | 删除实体，用最后一个实体填补空位 |
| `Chunks` 管理类 | [Chunk.cs L20](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L20) | Chunk 数组池化管理 |
| `Chunk.Transfer` | [Chunk.cs L650](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L650) | 跨 Chunk 实体搬家 |
| `Chunk.CopyComponents` | [Chunk.cs L621](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L621) | Archetype 间组件数据拷贝 |
| `ArrayPool<Chunk>` | [Chunk.cs L28](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L28) | 池化分配，避免 GC |
| SoA 布局 | [Chunk.cs L185-L192](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L185) | 每种组件一个数组，缓存友好 |
| 16KB ChunkSize | [L806](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L806) | L1d cache（32KB）的一半，线性遍历友好 |

📖 **下一章**：我们将探讨 World 是如何管理 Archetype 集合、EntityInfo 表，以及 Query 是如何用 BitSet 在所有 Archetype 中筛选出匹配的。
