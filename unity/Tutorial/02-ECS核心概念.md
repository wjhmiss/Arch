# 第02章 ECS核心概念

> 📖 本章目标：理解 ECS（Entity Component System）架构模式的核心思想，掌握 Arch 框架采用的 Archetype + Chunk 内存模型，为后续编写 Arch 程序打下理论基础。

---

## 2.1 什么是 ECS

ECS 是 **Entity Component System**（实体-组件-系统）的缩写，是一种面向数据的设计模式，最早在游戏开发领域流行起来（如《守望先锋》、Unity DOTS）。它将传统的"对象 + 行为"拆解为三部分：

| 概念 | 职责 | 类比 |
| --- | --- | --- |
| **Entity（实体）** | 一个唯一的 ID，本身不含数据也不含行为 | 数据库中的主键 |
| **Component（组件）** | 纯数据，可被挂载到实体上 | 数据库中的列 |
| **System（系统）** | 纯逻辑，按组件类型筛选实体并批量处理 | SQL 查询 + 业务逻辑 |

### 与传统 OOP 的对比

在传统面向对象编程（OOP）中，我们习惯把数据和逻辑封装在一起：

```csharp
// 传统 OOP 写法
class Enemy : MonoBehaviour
{
    public Vector3 Position;
    public float Health;

    void Update()
    {
        Position += Vector3.forward * Time.deltaTime;
        if (Health <= 0) Destroy(gameObject);
    }
}
```

这种写法直观，但当场景里有 10000 个 Enemy 时，每个对象都有自己的 `Update` 调用，对象在堆内存中分散分布，CPU 访问时会发生大量 **缓存未命中（Cache Miss）**，性能急剧下降。

ECS 的反思路是：**把同类数据放在一起，让 CPU 一次读取一大块**。系统不关心单个实体，而是"给我所有拥有 Position 的实体，我一次性更新它们"。

```csharp
// ECS 写法（伪代码）
// 数据
struct Position { public float X, Y, Z; }
struct Health   { public float Value; }

// 系统：批量处理所有拥有 Position 的实体
void MoveSystem(World world)
{
    foreach (ref var pos in world.Query<Position>())
    {
        pos.X += 0.1f;
    }
}
```

💡 **核心差异**：OOP 是"对象拥有行为"，ECS 是"系统处理数据"。

---

## 2.2 数据导向设计

### 2.2.1 CPU 缓存与内存布局

现代 CPU 读取内存并不是按字节读取，而是按 **缓存行（Cache Line，通常 64 字节）** 读取。如果下一步要访问的数据已经在缓存里，就叫 **缓存命中（Cache Hit）**；否则要从主存重新加载，叫 **缓存未命中（Cache Miss）**，后者比前者慢 100~300 倍。

```
主存访问延迟:    ~100 ns
L3 缓存延迟:     ~10 ns
L1 缓存延迟:     ~1 ns   ← 我们希望数据都在这里
```

### 2.2.2 AoS vs SoA

传统 OOP 中，对象通常以 **AoS（Array of Structures）** 方式存储：

```
[Enemy0{Pos, Hp, Velocity}][Enemy1{Pos, Hp, Velocity}]...
```

如果系统只想更新 `Position`，但每个 Enemy 对象里的 `Hp`、`Velocity` 也被一并加载进缓存，造成浪费。

ECS 采用 **SoA（Structure of Arrays）** 方式：

```
Position[]: [Pos0][Pos1][Pos2]...
Health[]:   [Hp0][Hp1][Hp2]...
Velocity[]: [V0][V1][V2]...
```

更新 Position 时，CPU 连续读取 `Position[]`，缓存命中率极高，性能可以提升数十倍。

🔥 **这就是 ECS 在性能上碾压 OOP 的根本原因**：不是魔法，而是顺应硬件设计。

---

## 2.3 Arch 的核心架构

[Arch](https://github.com/genaray/Arch) 是一个为 .NET 设计的高性能 ECS 库，Unity 中可通过 NuGet 引用。它采用 **Archetype + Chunk** 模型（与 Unity ECS、Entitas 思路一致），在内存布局与查询效率之间取得了很好的平衡。

📖 参考：[World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L170) 中对 World 的注释明确说明 *"stores Entitys in Archetypes and Chunks, manages them, and provides methods to query for specific Entitys"*。

### 2.3.1 Entity —— 仅是一个 ID

在 Arch 中，Entity 是一个 `readonly struct`，**只包含三个字段，没有任何方法逻辑**：

```csharp
public readonly struct Entity : IEquatable<Entity>, IComparable<Entity>
{
    public readonly int Id;        // 在 World 中的唯一 ID
    public readonly int WorldId;   // 所属 World 的 ID
    public readonly int Version;   // 版本号，用于检测实体是否已被销毁
}
```

📖 详见 [Entity.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L145)。

几个关键点：

- 💡 **`Id` 在 World 内唯一**，但不同 World 可能有相同 Id 的实体。
- 💡 **`Version` 用于" generation "机制**：当实体被销毁时，它的 Id 会被回收再利用，但 Version 会自增（见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L279) 的 `DestroyEntityInternal`），这样旧的 Entity 句柄会因 Version 不匹配而被识别为"已死亡"。
- 💡 **`Entity.Null` 是空实体**，常用于初始化或比较（`new(-1, 0, -1)`）。

⚠️ 不要尝试给 Entity 添加方法或字段——它必须保持极致轻量（12 字节），复制传递几乎零开销。

### 2.3.2 Component —— 纯数据

Component 可以是 `struct` 也可以是 `class`，但**强烈推荐使用 `struct`（或 C# 10 的 `record struct`）**，因为值类型可以被紧凑地放进 Chunk 数组里。

```csharp
// 推荐：record struct，简洁且为值类型
public readonly record struct Position(float X, float Y, float Z);

// 也合法，但不推荐：class 会产生引用开销
public class PlayerTag { public string Name; }
```

⚠️ 组件不应该包含任何逻辑方法（如 `Update`、`Move`），逻辑应放在系统（System）里。组件越"哑"，性能越好。

### 2.3.3 World —— 实体的容器

World 是所有实体的宿主，它管理着一组 Archetype，并提供创建、销毁、查询实体的 API。

📖 详见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L180)。

```csharp
public partial class World : IDisposable
{
    public int Id { get; }
    public int Size { get; }          // 当前实体数量
    public int Capacity { get; }      // 实体容量
    public Archetypes Archetypes { get; }  // 所有 Archetype
}
```

World 通过静态方法创建（[World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L119)）：

```csharp
public static World Create(
    int chunkSizeInBytes = 16_384,             // Chunk 的基础大小（字节），默认 16KB
    int minimumAmountOfEntitiesPerChunk = 100, // 每个 Chunk 至少容纳多少实体
    int archetypeCapacity = 2,                 // 初始 Archetype 容量
    int entityCapacity = 64);                  // 初始实体容量
```

💡 默认 16KB 的 Chunk 大小正好对应常见 CPU 的 L1 缓存容量，是性能与碎片率的平衡点。

### 2.3.4 Archetype —— 相同组件组合的集合

**Archetype（原型）** 是 Arch 的灵魂概念：**所有组件类型完全相同的实体，会被归类到同一个 Archetype 中**。

例如：

- Entity A 拥有 `{Position, Velocity}` → Archetype 1
- Entity B 拥有 `{Position, Velocity}` → Archetype 1（与 A 同原型）
- Entity C 拥有 `{Position, Velocity, Health}` → Archetype 2（不同原型）

📖 详见 [Archetype.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L260)。

Archetype 的关键属性：

```csharp
public sealed partial class Archetype
{
    public Signature Signature { get; }      // 组件签名（组件类型集合）
    public int ChunkSize { get; }            // 单个 Chunk 的字节数
    public int EntitiesPerChunk { get; }     // 每个 Chunk 能装多少实体
    public Chunks Chunks { get; }            // 它管理的所有 Chunk
}
```

🔥 当你对实体调用 `world.Add<T>` 或 `world.Remove<T>` 时，实体会从一个 Archetype **搬移**到另一个 Archetype，这就是所谓的"结构变更（Structural Change）"，是比较昂贵的操作。

📖 见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L348) 中的 `Move` 方法。

### 2.3.5 Chunk —— 16KB 内存块

**Chunk（块）** 是 Archetype 内部存放数据的物理单元，是一个固定大小的连续内存块（默认 16KB）。每个 Chunk 内部以 **SoA** 方式存储组件：

```
Chunk (16KB) for Archetype{Position, Velocity}:
┌─────────────────────────────────────────┐
│ Entity[]:    [E0][E1][E2]...            │  ← 实体 ID 数组
│ Position[]:  [P0][P1][P2]...            │  ← 连续的 Position 数据
│ Velocity[]:  [V0][V1][V2]...            │  ← 连续的 Velocity 数据
└─────────────────────────────────────────┘
```

📖 详见 [Chunk.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L145)：

```csharp
public partial struct Chunk
{
    public readonly Entity[] Entities;       // 实体数组
    public readonly Array[] Components;      // 每种组件一个数组
    public int Count { get; }                // 当前已用槽位
    public int Capacity { get; }             // 总容量
    public readonly int Buffer => Capacity - Count;  // 剩余空间
}
```

💡 同一 Archetype 的所有 Chunk 大小、容量、组件布局完全一致，这让批量遍历变得极其高效。

⚠️ Chunk 不会在实体销毁时立即释放，而是留出空位等待新实体复用，避免频繁分配。

### 2.3.6 Query —— 通过组件类型筛选实体

**Query（查询）** 让系统只处理"拥有特定组件组合"的实体。Arch 使用 `QueryDescription` 描述查询条件，再由 `World.Query` 转化为可迭代的 `Query` 对象。

📖 详见 [Query.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L314)：

```csharp
public partial struct QueryDescription : IEquatable<QueryDescription>
{
    public Signature All;        // 必须拥有的全部组件
    public Signature Any;        // 至少拥有其中之一
    public Signature None;       // 不能拥有的组件
    public Signature Exclusive;  // 精确匹配（不多不少）
}
```

四种筛选条件的语义：

| 字段 | 语义 | 示例 |
| --- | --- | --- |
| `All` | 必须同时拥有这些组件 | `All = {Position, Velocity}` → 有 P 和 V 的实体 |
| `Any` | 至少拥有其中之一 | `Any = {Alive, Ghost}` → 活着或幽灵 |
| `None` | 不能拥有这些组件 | `None = {Dead}` → 排除已死亡 |
| `Exclusive` | 组件集合精确匹配 | `Exclusive = {Position}` → 只有 P，不能有别的 |

📖 见 [World.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L411) 的 `Query` 方法：

```csharp
public Query Query(in QueryDescription queryDescription)
{
    // 利用 QueryCache 缓存查询，避免重复分配
    if (queryCache.TryGetValue(queryDescription, out var query))
        return query;

    query = new Query(Archetypes, queryDescription);
    queryCache[queryDescription] = query;
    return query;
}
```

💡 Arch 会对 Query 结果做缓存，第二次相同查询几乎零开销。

---

## 2.4 与传统 Unity MonoBehaviour 对比

| 维度 | MonoBehaviour（OOP） | Arch（ECS） |
| --- | --- | --- |
| **数据布局** | 对象散落在堆上（AoS） | Chunk 内连续存储（SoA） |
| **缓存友好性** | 差，大量 Cache Miss | 极佳，几乎全命中 L1 |
| **实体表示** | GameObject 引用（重量级） | Entity struct（12 字节） |
| **逻辑入口** | 每对象一个 `Update` | 系统批量遍历 |
| **多线程** | 难（UnityEngine 受限） | 友好（数据无共享） |
| **学习曲线** | 低（直观） | 中高（思维转换） |
| **调试便利** | 高（Inspector 可视化） | 中（需要自建调试视图） |
| **适合规模** | 中小项目 / UI | 大量实体 / 高性能场景 |
| **GC 压力** | 高（每对象分配） | 低（Chunk 池化） |
| **迭代速度（10w 实体）** | 慢（毫秒级） | 快（微秒级） |

⚠️ **注意**：ECS 并不是要完全取代 MonoBehaviour，Unity 项目中二者常常共存——UI、相机、场景管理用 MonoBehaviour，海量同类实体（敌人、子弹、粒子）用 ECS。

---

## 2.5 何时使用 Arch

### ✅ 适合使用 Arch 的场景

- 🎮 **海量同类实体**：万级以上的子弹、粒子、敌人、NPC。
- 🧪 **仿真与可视化**：粒子系统、流体模拟、群体行为（Boids）。
- 🏗️ **策略/模拟经营**：建筑网格、单位管理、经济系统。
- ⚡ **高性能服务端逻辑**：服务器权威游戏、帧同步。
- 🧩 **需要多线程并行**的批量计算。

### ❌ 不适合使用 Arch 的场景

- 🖼️ **UI 系统**：元素数量少、变化频繁、与 MonoBehaviour 集成紧密。
- 🎬 **过场动画、剧情系统**：状态复杂、流程驱动，非数据驱动。
- 🕹️ **单一角色控制器**：玩家角色只有一个，ECS 收益微乎其微。
- 📦 **小型 Demo / 原型**：开发速度优先时，MonoBehaviour 更快上手。
- 🔧 **重度依赖 Unity 组件**（Rigidbody、MeshRenderer）：需要 Bridge 层，得不偿失。

💡 **决策原则**：当你能回答"我为什么需要 1 万个这个对象？"时，再考虑用 ECS。

---

## 2.6 架构图解

下面用 ASCII 图展示 Arch 的层次关系：**World → Archetype → Chunk → Entity / Component**。

```
┌──────────────────────────────────────────────────────────────────┐
│                            World (id=0)                           │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Archetypes (List<Archetype>)                              │  │
│  │                                                             │  │
│  │  ┌───────────────────────────────────────────────────────┐ │  │
│  │  │ Archetype #1  Signature = {Position, Velocity}        │ │  │
│  │  │  Chunks:                                              │ │  │
│  │  │   ┌─────────────────────── Chunk 0 (16KB) ─────┐      │ │  │
│  │  │   │ Entity[]:   [E0][E1][E2]...[E_n]            │      │ │  │
│  │  │   │ Position[]: [P0][P1][P2]...[P_n]   ← SoA    │      │ │  │
│  │  │   │ Velocity[]: [V0][V1][V2]...[V_n]   ← SoA    │      │ │  │
│  │  │   └─────────────────────────────────────────────┘      │ │  │
│  │  │   ┌─────────────────────── Chunk 1 (16KB) ─────┐      │ │  │
│  │  │   │ Entity[]:   [E_n+1]...                      │      │ │  │
│  │  │   │ Position[]: [P_n+1]...                      │      │ │  │
│  │  │   │ Velocity[]: [V_n+1]...                      │      │ │  │
│  │  │   └─────────────────────────────────────────────┘      │ │  │
│  │  └───────────────────────────────────────────────────────┘ │  │
│  │                                                             │  │
│  │  ┌───────────────────────────────────────────────────────┐ │  │
│  │  │ Archetype #2  Signature = {Position, Velocity, Health}│ │  │
│  │  │  Chunks: ...                                          │ │  │
│  │  └───────────────────────────────────────────────────────┘ │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                   │
│  EntityInfo (id -> Archetype + Slot)   ← 快速查找实体的存储位置   │
│  QueryCache  (QueryDescription -> Query) ← 查询缓存               │
└──────────────────────────────────────────────────────────────────┘

       ▲
       │ 查询: world.Query(QueryDescription{All={Position,Velocity}})
       │
┌──────┴───────────────────────────────────────────────────────────┐
│  System: MoveSystem                                              │
│    foreach (ref var chunk in query)                              │
│       foreach (var i in chunk)                                   │
│           ref var pos = ref chunk.Get<Position>(i);              │
│           pos.X += 0.1f;                                         │
└──────────────────────────────────────────────────────────────────┘
```

🔥 **关键点**：
1. World 是顶层容器，持有所有 Archetype。
2. 每个 Archetype 对应一种唯一的组件组合。
3. Chunk 是 Archetype 内部的物理存储单元，固定大小、SoA 布局。
4. Entity 只是个 ID，真正的数据在 Chunk 的组件数组里。
5. System 通过 Query 拿到 Chunk，直接读写组件数组。

---

## 本章小结

| 概念 | 一句话理解 | 源码位置 |
| --- | --- | --- |
| **Entity** | 12 字节的轻量 ID（Id + WorldId + Version） | [Entity.cs#L145](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L145) |
| **Component** | 纯数据，推荐 `record struct` | 用户定义 |
| **World** | 实体容器，管理 Archetype 与查询 | [World.cs#L180](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L180) |
| **Archetype** | 相同组件组合实体的逻辑分组 | [Archetype.cs#L260](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs#L260) |
| **Chunk** | 16KB 连续内存块，SoA 存储组件 | [Chunk.cs#L145](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs#L145) |
| **QueryDescription** | 描述查询条件（All/Any/None/Exclusive） | [Query.cs#L314](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L314) |
| **SoA** | Structure of Arrays，缓存友好的数据布局 | — |
| **Structural Change** | 增删组件导致实体跨 Archetype 搬移，开销较大 | [World.cs#L348](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L348) |

📖 **下一章**：[第03章 第一个Arch程序](file:///d:/Unity/Arch/unity/Tutorial/03-第一个Arch程序.md) —— 我们将动手写一个"冒险者移动"的完整示例，把本章理论转化为可运行代码。
