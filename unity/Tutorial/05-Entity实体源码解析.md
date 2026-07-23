# 第05章 Entity 实体源码解析

> 📖 本章基于 [Entity.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs) 进行逐行解析。Entity 是 ECS 中最容易被误解的概念——它**不是对象**，只是一个 12 字节的结构体，承载着"这个实体是谁"的全部信息。

---

## 5.1 Entity 的本质：只是一个 ID

在传统 OOP 中，"敌人"是一个对象，包含位置、血量、AI 状态等字段。而在 Arch ECS 中，**Entity 只是一个标识符**，组件数据全部存放在 `Chunk` 数组里，Entity 本身不持有任何业务数据。

📖 [Entity.cs L139-146](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L139)：

```csharp
[DebuggerTypeProxy(typeof(EntityDebugView))]
[SkipLocalsInit]
public readonly struct Entity : IEquatable<Entity>, IComparable<Entity>
{
    public readonly int Id;
    public readonly int WorldId;
    public readonly int Version;
    // ...
}
```

`Entity` 是一个 `readonly struct`，三个 `int` 字段共 12 字节（PURE_ECS 模式下 8 字节）。它的全部工作就是回答两个问题：

1. **我是谁？** → `Id`（在 `World` 内唯一）+ `WorldId`（哪个世界）
2. **我是否还活着？** → `Version`（与 `EntityInfo` 中存的版本号比对）

> 💡 把 Entity 设计成值类型结构体有三个好处：
> - **零分配**：传递 Entity 不进堆，没有 GC 压力。
> - **值语义**：拷贝即副本，无需担心引用别名。
> - **缓存友好**：连续存放的 Entity 数组能充分利用 CPU 缓存。

> 🔥 一个常被忽略的细节：`Entity` 不存任何组件数据。所以 `entity.Position` 这种写法在 Arch 中**不存在**。要拿数据必须通过 `world.Get<Position>(entity)`，由 World 查 `EntityInfo` 找到组件所在内存。

---

## 5.2 字段解析

### 5.2.1 `Id` —— 实体在世界中的唯一 ID

📖 默认模式 [Entity.cs L150](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L150)：

```csharp
/// <summary>
///      Its Id, unique in its <see cref="World"/>.
/// </summary>
public readonly int Id;
```

📖 PURE_ECS 模式 [Entity.cs L20](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L20)：

```csharp
public readonly int Id = -1;
```

`Id` 是 `int` 类型（4 字节），**在所属 World 内唯一**。注意它**不是全局唯一**——两个不同 World 中的实体可能 Id 相同，因此默认模式下还需要 `WorldId` 来全局区分。

`Id` 的来源见 [World.cs L266-272](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L266) 的 `GetOrCreateEntityInternal`：优先从 `RecycledIds` 复用已销毁实体的 Id，否则用当前 `Size` 作为新 Id。

### 5.2.2 `WorldId` —— 仅在非 PURE_ECS 模式下存在

📖 [Entity.cs L155](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L155)：

```csharp
/// <summary> Its <see cref="World"/> id. </summary>
public readonly int WorldId;
```

`WorldId` 仅在默认模式下存在。它的核心用途是**通过 Entity 反查 World**：

📖 `EntityExtensions.IsAlive` [L72-76](file:///d:/Unity/Arch/Arch/src/Arch/Core/Extensions/EntityExtensions.cs#L72)：

```csharp
public static bool IsAlive(this in Entity entity)
{
    var world = World.Worlds.DangerousGetReferenceAt(entity.WorldId);
    return world.IsAlive(entity);
}
```

这种设计让扩展方法可以无参数反查 World，API 更简洁。代价是每个 Entity 多 4 字节。

> 💡 PURE_ECS 模式下没有 `WorldId`，因此**无法**使用 `entity.IsAlive()` 这种扩展方法。开发者必须显式传 `World`：`world.IsAlive(entity)`。详见 5.3 节。

### 5.2.3 `Version` —— 防止悬空引用

📖 [Entity.cs L160](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L160)：

```csharp
/// <summary> The version of an entity. </summary>
public readonly int Version;
```

`Version` 是 Arch 防止"悬空引用"的核心机制。详见 5.9 节。

---

## 5.3 PURE_ECS vs 默认模式

`Entity.cs` 用 `#if PURE_ECS / #else / #endif` 把整个结构体定义拆成两份：

| 维度 | PURE_ECS（[L8-136](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L8)） | 默认模式（[L137-282](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L137)） |
|------|-----------------|----------------|
| 字段 | `Id`、`Version`（8 字节） | `Id`、`WorldId`、`Version`（12 字节） |
| `Entity.Null` | `new(-1, 0, -1)` | `new(-1, 0, -1)` |
| `Equals` | 比较 `Id`、`Version` | 比较 `Id`、`WorldId`、`Version` |
| `GetHashCode` | 2 字段哈希 | 3 字段哈希 |
| `CompareTo` | `(Version << 8) \| Id` | `(WorldId << 16) \| (Version << 8) \| Id` |
| `DebuggerTypeProxy` | ❌ 无 | ✅ `EntityDebugView` |
| 多 World 共存 | ❌ 不支持 | ✅ 支持 |
| `entity.IsAlive()` 扩展 | ❌ 不可用 | ✅ 可用 |

> 🔥 PURE_ECS 牺牲便利性换性能：每个 Entity 省 4 字节，海量实体场景下能显著降低缓存压力。但在 Unity 中，需要多 World（如游戏世界 + UI 世界）时必须用默认模式。

---

## 5.4 构造函数 —— `internal` 访问级别

📖 默认模式 [Entity.cs L183-201](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L183)：

```csharp
internal Entity(int id, int worldId)
{
    Id = id;
    WorldId = worldId;
    Version = 1;
}

internal Entity(int id, int worldId, int version)
{
    Id = id;
    WorldId = worldId;
    Version = version;
}
```

两个构造函数都是 `internal`，**禁止外部代码 `new Entity(...)`**。这是 Arch 强制约束的"工厂模式"——实体只能通过 `World.Create(...)` 创建，保证：

1. 每个 Entity 都被正确登记到 `EntityInfo`。
2. `WorldId` 与创建它的 World 一致，不会出现"撒谎的 Entity"。
3. `Version` 从 1 开始（默认构造函数）或来自回收队列（三参构造函数）。

> ⚠️ 如果 Entity 构造函数是 `public`，开发者可能写出 `new Entity(999, 0, 1)` 这种"幽灵实体"——它在 World 中根本不存在，但拿到引用的代码无法立刻察觉。`internal` 从源头杜绝了这种 bug。

> 💡 注意 [Entity.cs L170-175](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L170) 还有一个 `public Entity()` 无参构造函数（仅 C# 10+ 支持结构体显式无参构造）。它把字段初始化为 `Id=-1, WorldId=0, Version=-1`，等同于 `Entity.Null`，用于支持 `default(Entity)` 语义。

---

## 5.5 相等性比较 —— 位运算技巧

📖 [Entity.cs L208-211](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L208)：

```csharp
public bool Equals(Entity other)
{
    return ((Id ^ other.Id) | (WorldId ^ other.WorldId) | (Version ^ other.Version)) == 0;
}
```

这行代码用 **XOR + OR** 的位运算实现三字段相等比较，而非更直观的：

```csharp
// 直观写法（不推荐）
return Id == other.Id && WorldId == other.WorldId && Version == other.Version;
```

### 为什么用位或而不是逻辑或？

> 🔥 **性能！** 这是关键考量：
> - 逻辑或 `||` 是**短路**的：第一个 `==` 为 false 时跳过后续比较。但短路意味着**分支预测**，CPU 流水线在分支预测失败时代价高昂。
> - 位或 `|` 是**无分支**的：三个 XOR 都执行，结果 OR 起来再判零。整段代码无跳转，CPU 流水线顺畅。
>
> 对于三个 `int` 字段（共 12 字节）的比较，位运算版本在现代 CPU 上更快、更可预测。同时编译器更容易把它向量化成 SIMD 指令。

### 运算原理

- `a ^ b == 0` 当且仅当 `a == b`。
- `(x | y | z) == 0` 当且仅当 `x == 0 && y == 0 && z == 0`。
- 因此 `((Id^other.Id) | (WorldId^other.WorldId) | (Version^other.Version)) == 0` 等价于三字段全等。

PURE_ECS 版本 [L61-64](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L61) 只比较两字段：

```csharp
return ((Id ^ other.Id) | (Version ^ other.Version)) == 0;
```

---

## 5.6 `GetHashCode` 实现

📖 [Entity.cs L238-249](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L238)：

```csharp
public override int GetHashCode()
{
    unchecked
    {
        // Overflow is fine, just wrap
        var hash = 17;
        hash = (hash * 23) + Id;
        hash = (hash * 23) + WorldId;
        hash = (hash * 23) + Version;
        return hash;
    }
}
```

这是经典的 `hash * 23 + field` 模式，由 Jon Skelet 在《Effective C#》中推广。要点：

1. **`unchecked`**：允许乘加运算溢出回绕，不抛 `OverflowException`。哈希值本就允许溢出。
2. **种子 `17`**：非零起点，避免第一个字段为 0 时哈希退化。
3. **乘数 `23`**：一个**质数**，让每个字段的影响力扩散到所有位。质数能减少哈希碰撞。
4. **顺序敏感**：`hash = hash*23 + A; hash = hash*23 + B;` 与交换 A、B 顺序结果不同，让 `(Id=1, WorldId=2)` 与 `(Id=2, WorldId=1)` 哈希不同。

> 💡 这个模式在 .NET BCL 中也广泛使用，例如匿名类型的 `GetHashCode`。它在分布均匀性和计算速度间取得了良好平衡。

PURE_ECS 版本 [L92-102](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L92) 只哈希 `Id` 和 `Version` 两个字段。

---

## 5.7 `CompareTo` 实现 —— 位运算排序技巧

📖 [Entity.cs L229-232](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L229)：

```csharp
public int CompareTo(Entity other)
{
    return (WorldId.CompareTo(other.WorldId) << 16)
         | (Version.CompareTo(other.Version) << 8)
         | Id.CompareTo(other.Id);
}
```

这行代码用**位拼接**实现三级排序，思路精巧：

1. `int.CompareTo` 返回 `-1`、`0` 或 `1`（实际可能是任意符号的 int，但 .NET 实现通常保证在 -1..1 之间）。
2. 把三个结果**移到不同的字节位置**再 OR 起来，形成一个"复合比较值"。
3. 高位优先级最高：先按 `WorldId` 排，相同则按 `Version`，再相同按 `Id`。

> ⚠️ 这个技巧的前提是 `CompareTo` 返回值在 `[-1, 0, 1]` 范围内。.NET 标准的 `int.CompareTo` 满足此条件（返回 `Math.Sign(diff)`），但并非所有 `IComparable` 实现都保证。Arch 这里依赖了 `int.CompareTo` 的具体行为。

> 💡 PURE_ECS 版本 [L82-85](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L82) 只有两级：`(Version.CompareTo(other.Version) << 8) | Id.CompareTo(other.Id)`。

### 为什么用位拼接而不是 `if-else`？

> 🔥 同样是**避免分支**。直观写法是：

```csharp
if (WorldId != other.WorldId) return WorldId.CompareTo(other.WorldId);
if (Version != other.Version) return Version.CompareTo(other.Version);
return Id.CompareTo(other.Id);
```

这有 2 个分支，分支预测失败时流水线刷洗代价大。位拼接版本无分支，对随机分布的实体排序更稳定。

---

## 5.8 `Entity.Null` —— 空实体的定义与用途

📖 [Entity.cs L165](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L165)：

```csharp
public readonly static Entity Null = new(-1, 0, -1);
```

`Entity.Null` 是一个静态只读字段，表示"无效实体"。三个字段的取值都精心设计：

- `Id = -1`：合法实体 Id 从 0 开始，`-1` 永远不会分配。
- `WorldId = 0`：占位值（默认模式下 Id=0 的 World 通常存在）。
- `Version = -1`：**关键！** 合法实体 Version 从 1 开始，`-1` 必然不匹配。

> 💡 `Version = -1` 让 `Entity.Null` 自动被 `IsAlive` 判定为 false。看 [World.cs L1663-1672](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L1663)：

```csharp
public bool IsAlive(Entity entity)
{
    if (entity.Version <= 0) return false;
    ref var entityData = ref EntityInfo.TryGetEntityData(entity.Id, out var entityDataExists);
    return entityDataExists && entityData.Version == entity.Version;
}
```

第一行 `entity.Version <= 0` 直接拦截 `Null`（Version=-1）和任何被销毁后 Version 回绕到非正数的实体。

### 使用场景

```csharp
Entity FindPlayer(World world)
{
    var found = Entity.Null;
    var desc = new QueryDescription().WithAll<PlayerTag>();
    world.Query(desc, entity => found = entity);
    return found;  // 调用方检查 if (found != Entity.Null) { ... }
}
```

---

## 5.9 版本号机制 —— 防止悬空引用

### 问题场景

假设没有 Version，会发生什么？

```csharp
var e1 = world.Create(typeof(Position));   // Id = 5
world.Destroy(e1);                          // Id 5 进入回收队列
var e2 = world.Create(typeof(Position));   // 复用 Id = 5（新实体）

// 此时 e1 仍然记录 Id = 5
world.Set(e1, new Position { X = 100 });   // 💥 错误！e1 已销毁，但 Id 被复用
// 实际上修改了 e2 的 Position
```

这就是经典的**悬空引用**问题——和 C 中的 `use-after-free` 同源。

### Version 的解决方案

每次销毁实体时，把 `(Id, Version+1)` 入回收队列。下次创建时取出的 Version 是递增后的值。

📖 [World.cs L281](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L281)：

```csharp
var recycledEntity = new RecycledEntity(entity.Id, unchecked(entity.Version + 1));
RecycledIds.Enqueue(recycledEntity);
```

📖 [World.cs L268-270](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L268)：

```csharp
var recycle = RecycledIds.TryDequeue(out var recycledId);
var recycled = recycle ? recycledId : new RecycledEntity(Size, 1);
entity = new Entity(recycled.Id, Id, recycled.Version);
```

再看 `IsAlive` 的检查：

```csharp
return entityDataExists && entityData.Version == entity.Version;
```

- `e1` 的 Version=1，`EntityInfo` 中 e2 的 Version=2 → `1 != 2` → `IsAlive(e1) == false`。

### 用 `IsAlive` 防御性编程

```csharp
if (world.IsAlive(cachedEntity))
{
    ref var pos = ref world.Get<Position>(cachedEntity);
    pos.X += 1;
}
```

> ⚠️ `unchecked` 让 Version 可以溢出到 `int.MinValue`。理论上极端场景下 Version 回绕可能让一个很久以前的 Version 重新匹配——但需要 2^31 次销毁同一 Id，实际项目中几乎不可能触发。Arch 的测试套件也接受这一权衡。

---

## 5.10 `EntityDebugView` —— `[DebuggerTypeProxy]` 的作用

📖 [Entity.cs L143](file:///d:/Unity/Arch/Arch/src/Arch/Core/Entity.cs#L143)：

```csharp
[DebuggerTypeProxy(typeof(EntityDebugView))]
[SkipLocalsInit]
public readonly struct Entity : IEquatable<Entity>, IComparable<Entity>
```

`[DebuggerTypeProxy]` 是 .NET 的诊断特性，告诉 Visual Studio / Rider / VSCode 调试器：**在 Watch / Locals 窗口中展开这个结构体时，用 `EntityDebugView` 来显示，而不是直接看字段**。

### 为什么需要？

Entity 只有三个 int 字段，调试时看到的就只是 `Id=5, WorldId=0, Version=1`，对开发者毫无意义。`EntityDebugView` 把与 Entity 相关的所有上下文（World、Archetype、Chunk、Components、EntityInfo、IsAlive）一次性展示出来。

📖 [EntityDebugView.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Utils/EntityDebugView.cs) 关键部分：

```csharp
internal sealed class EntityDebugView
{
    private readonly Entity _entity;

    public EntityDebugView(Entity entity)
    {
        _entity = entity;
        Components = IsAlive ? entity.GetAllComponents() : null;
    }

    public int Id => _entity.Id;
    public bool IsAlive => _entity.IsAlive();
    public int Version => IsAlive ? _entity.Version : -1;
    public object?[]? Components { get; }
    public World? World => IsAlive ? World.Worlds[_entity.WorldId] : null;
    public Archetype? Archetype => IsAlive ? World.Worlds[_entity.WorldId].GetArchetype(_entity) : null;
    public Chunk Chunk => IsAlive ? World.Worlds[_entity.WorldId].GetChunk(_entity) : default;
    public EntityData EntityInfo => IsAlive ? World?.EntityInfo.GetEntityData(_entity.Id) ?? new EntityData() : new EntityData();
}
```

调试器展开 Entity 时会看到：

```
Id: 5
IsAlive: true
Version: 1
Components: { Position, Velocity, NameTag }
World: { Arch.Core.World ... }
Archetype: { Arch.Core.Archetype ... }
Chunk: { Arch.Core.Chunk ... }
EntityInfo: { Arch.Core.EntityData ... }
```

> 💡 `EntityDebugView` 标注为 `internal sealed`，且整个文件被 `#if !PURE_ECS` 包裹（[L1](file:///d:/Unity/Arch/Arch/src/Arch/Core/Utils/EntityDebugView.cs#L1)）。因为 PURE_ECS 下没有 `WorldId`，无法反查 World，调试视图就没意义了。

> ⚠️ DebugView 只在调试器附加时构造，不会影响运行时性能。但要注意 `Components` getter 调用了 `entity.GetAllComponents()`，会分配数组，**不要在 DebugView 中展开大量实体**以免卡顿。

---

## 5.11 配套示例

📖 完整示例见 `Assets/Scripts/Chapter05/EntityDemo.cs`，演示 Entity 的相等性、Version 机制与悬空引用检测：

```csharp
using Arch.Core;
using System;

public class EntityDemo
{
    public static void Run()
    {
        using var world = World.Create();

        // 1. 创建实体
        var e1 = world.Create(typeof(Position));
        Console.WriteLine($"e1 = {e1}");          // Entity = { Id = 0, WorldId = 0, Version = 1 }
        Console.WriteLine($"IsAlive(e1) = {world.IsAlive(e1)}");  // True

        // 2. 相等性：同实体相等，不同实体不等
        var e1Copy = e1;
        Console.WriteLine($"e1 == e1Copy: {e1 == e1Copy}");  // True

        var e2 = world.Create(typeof(Position));
        Console.WriteLine($"e1 == e2: {e1 == e2}");          // False

        // 3. Entity.Null 与 IsAlive
        Console.WriteLine($"Null == default: {Entity.Null == default(Entity)}");  // True
        Console.WriteLine($"IsAlive(Null): {world.IsAlive(Entity.Null)}");        // False

        // 4. Version 机制：销毁后悬空引用
        var e1StaleRef = e1;
        world.Destroy(e1);
        Console.WriteLine($"After destroy, IsAlive(staleRef) = {world.IsAlive(e1StaleRef)}");  // False

        // 5. Id 复用，但 Version 不同
        var e3 = world.Create(typeof(Position));
        Console.WriteLine($"e3 = {e3}");                       // Id = 0, Version = 2
        Console.WriteLine($"e1 == e3: {e1 == e3}");            // False（Version 不同）
        Console.WriteLine($"IsAlive(e3) = {world.IsAlive(e3)}"); // True

        // 6. 排序演示
        var entities = new[] { e2, e3, e1 };
        Array.Sort(entities);
        foreach (var e in entities) Console.WriteLine(e);
    }
}

public struct Position { public float X, Y; }
```

运行结果预期：
- `e1` 与 `e1Copy` 相等（Id/WorldId/Version 三者一致）。
- 销毁 `e1` 后，`e1StaleRef` 的 `IsAlive` 返回 `false`——Version 不匹配 `EntityInfo` 中的记录。
- `e3` 复用了 `e1` 的 Id=0，但 Version=2，所以 `e1 != e3`，且操作 `e1` 不会误伤 `e3`。

---

## 本章小结

| 主题 | 关键点 |
|------|--------|
| **Entity 本质** | 12 字节 `readonly struct`，只是 ID，不持数据 |
| **`Id`** | World 内唯一的 int，可回收复用 |
| **`WorldId`** | 仅默认模式存在，用于反查 World |
| **`Version`** | 防"悬空引用"——销毁时 +1，`IsAlive` 比对 |
| **PURE_ECS** | 省 4 字节，禁多 World；`Entity.Null` 仍可用 |
| **构造函数** | `internal`，强制走 `World.Create` 工厂 |
| **`Equals` 位运算** | `((a^b)\|(c^d)\|(e^f))==0`，无分支更快 |
| **`GetHashCode`** | `hash * 23 + field` 经典模式，质数扩散 |
| **`CompareTo` 位拼接** | 三级排序 `(W<<16)\|(V<<8)\|I`，无 if |
| **`Entity.Null`** | `new(-1, 0, -1)`，Version=-1 自动判死 |
| **Version 机制** | 销毁时 `unchecked(Version+1)` 入队，复用时取出 |
| **`EntityDebugView`** | `[DebuggerTypeProxy]` 让调试器展示完整上下文 |
| **`[SkipLocalsInit]`** | 跳过局部变量零初始化，减少开销 |

> 📖 下一章我们将进入 [Component 与 ComponentRegistry](06-Component组件与ComponentRegistry.md)，看 Arch 如何用 `Component<T>` 泛型缓存把组件类型映射到稳定的 `int` Id。
