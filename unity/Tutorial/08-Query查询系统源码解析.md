# 第08章 Query查询系统源码解析

## 8.1 概述

在 ECS 框架中，**Query（查询）** 是连接 System 与 Entity 的核心桥梁。Arch 通过 `QueryDescription` 描述要查找的组件组合，由 `Query` 类在所有 `Archetype` 中过滤出匹配项，并通过 `BitSet` 位集进行高效比对。本章将带你深入 [Query.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs) 源码，理解每个类型的职责。

> 💡 阅读本章前，建议先复习第 04 章关于 Archetype 与 BitSet 的内容。Query 的核心机制就建立在它们之上。

## 8.2 Signature 结构体：组件签名的封装

[Signature](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L34) 是一个 `struct`，用于描述一组 `ComponentType`。它既可以表示一个 Entity 的组件构成，也可以作为查找 Archetype 或 Query 的标识。

### 8.2.1 字段与构造

```csharp
public struct Signature : IEquatable<Signature>
{
    public static readonly Signature Null = new();

    // 缓存的哈希码，因为每次重算代价极高
    private int _hashCode;

    public Signature()
    {
        ComponentsArray = [];
        _hashCode = -1;
    }

    public Signature(params ComponentType[] components)
    {
        ComponentsArray = components;
        _hashCode = -1;
        _hashCode = GetHashCode();  // 构造时即缓存
    }

    internal ComponentType[] ComponentsArray { get; set; } = [];
    public Span<ComponentType> Components { get; }
    public int Count { get; }
}
```

注意 [Query.cs L44](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L44) 中的 `_hashCode` 字段，它通过 `-1` 表示"未计算"。这是一个非常重要的优化点。

> 🔥 **设计哲学**：Signature 在构造时一次性计算哈希并缓存，后续的相等性比较只需比较哈希值，避免了反复遍历数组。

### 8.2.2 哈希缓存机制

[GetHashCode](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L127-L142) 方法实现了缓存逻辑：

```csharp
public override int GetHashCode()
{
    // 本地副本，减少属性访问开销
    var hash = _hashCode;
    if (hash != -1)
    {
        return hash;  // 命中缓存，直接返回
    }

    unchecked
    {
        hash = Component.GetHashCode(Components);
        _hashCode = hash;  // 写回缓存
        return hash;
    }
}
```

第一次调用时计算并通过字段写回，之后所有调用都走缓存分支。`Equals` 直接比较哈希：

```csharp
public bool Equals(Signature other)
{
    return GetHashCode() == other.GetHashCode();
}
```

> ⚠️ 这种基于哈希的相等比较在极端情况下可能存在哈希冲突，但 Arch 的 `Component.GetHashCode` 对组件 ID 数组计算得足够分散，实际使用中冲突概率极低。

### 8.2.3 Add / Remove 静态方法

[Add](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L160-L169) 与 [Remove](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L178-L187) 通过 `HashSet<ComponentType>` 去重，生成新 Signature：

```csharp
public static Signature Add(Signature first, Signature second)
{
    var set = new HashSet<ComponentType>(first.Count + second.Count);
    set.UnionWith(first.ComponentsArray);
    set.UnionWith(second.ComponentsArray);
    return new Signature(set.ToArray());
}

public static Signature Remove(Signature first, Signature second)
{
    var set = new HashSet<ComponentType>(first.Count + second.Count);
    set.UnionWith(first.ComponentsArray);
    set.ExceptWith(second.ComponentsArray);
    return new Signature(set.ToArray());
}
```

它们在 World 内部被频繁调用，例如 [World.cs L922](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L922) 添加组件时计算新 Archetype 签名：

```csharp
var newSignature = Signature.Add(archetype.Signature, Component<T>.Signature);
newArchetype = GetOrCreate(newSignature);
```

### 8.2.4 运算符重载

为了语义清晰，[Query.cs L218-L232](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L218-L232) 提供了 `+` / `-` 运算符：

```csharp
public static Signature operator +(Signature a, Signature b) => Add(a, b);
public static Signature operator -(Signature a, Signature b) => Remove(a, b);
```

### 8.2.5 隐式转换到 BitSet

这是 Signature 最关键的转换之一，[Query.cs L289-L300](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L289-L300)：

```csharp
public static implicit operator BitSet(Signature signature)
{
    if (signature.Count == 0)
    {
        return new BitSet();
    }

    var bitSet = new BitSet();
    bitSet.SetBits(signature.Components);
    return bitSet;
}
```

这一转换在 `Query` 构造函数中被大量使用（下文会看到），它把组件 ID 列表压成位向量，使匹配算法能用 SIMD 指令一次比对几十个组件。

此外 Signature 还支持与 `ComponentType`、`ComponentType[]`、`Span<ComponentType>` 之间的双向隐式转换，配合 `[CollectionBuilder]` 特性，可使用集合表达式：

```csharp
Signature sig = [typeof(Position), typeof(Velocity)];
```

## 8.3 QueryDescription 结构体：查询描述

[QueryDescription](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L314) 是用户面对的主要 API，它由四个 `Signature` 组成：

```csharp
public partial struct QueryDescription : IEquatable<QueryDescription>
{
    public Signature All { get; private set; } = Signature.Null;
    public Signature Any { get; private set; } = Signature.Null;
    public Signature None { get; private set; } = Signature.Null;
    public Signature Exclusive { get; private set; } = Signature.Null;
}
```

| 字段 | 语义 | 示例 |
|------|------|------|
| `All` | 必须拥有的全部组件 | "同时有 Position 和 Velocity" |
| `Any` | 至少拥有其中之一 | "有 Sprite 或 Mesh" |
| `None` | 不能拥有的组件 | "没有 Disabled 标记" |
| `Exclusive` | 精确匹配，组件列表完全一致 | "只有 Position、Velocity 两个组件" |

> ⚠️ `Exclusive` 与 `All`/`Any`/`None` 互斥。`Query` 构造函数有 `Debug.Assert` 验证（[Query.cs L551-L557](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L551-L557)）。

### 8.3.1 链式 API

[WithAll\<T\>()](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L395-L400) 等方法返回 `ref QueryDescription`，支持链式调用：

```csharp
[UnscopedRef]
public ref QueryDescription WithAll<T>()
{
    All = Component<T>.Signature;
    Build();
    return ref this;
}

public ref QueryDescription WithAny<T>()  { Any = Component<T>.Signature; Build(); return ref this; }
public ref QueryDescription WithNone<T>() { None = Component<T>.Signature; Build(); return ref this; }
public ref QueryDescription WithExclusive<T>() { Exclusive = Component<T>.Signature; Build(); return ref this; }
```

使用方式：

```csharp
var desc = new QueryDescription()
    .WithAll<Position, Velocity>()
    .WithNone<Disabled>();
```

注意 `[UnscopedRef]` 特性，它告诉编译器返回的 ref 不会逃逸到方法外，避免误判为悬空引用。

### 8.3.2 Build 方法：刷新哈希

如果用户在构造 QueryDescription 之后修改了内部 Signature（虽然不是公开 API 推荐做法），需要手动调用 [Build](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L382-L386)：

```csharp
public void Build()
{
    _hashCode = -1;          // 失效缓存
    _hashCode = GetHashCode();  // 重新计算
}
```

### 8.3.3 GetHashCode 组合

[Query.cs L473-L493](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L473-L493) 用经典的 `17 / 23` 素数组合四个签名的哈希：

```csharp
public override int GetHashCode()
{
    var hash = _hashCode;
    if (hash != -1) return hash;

    unchecked
    {
        hash = 17;
        hash = (hash * 23) + All.GetHashCode();
        hash = (hash * 23) + Any.GetHashCode();
        hash = (hash * 23) + None.GetHashCode();
        hash = (hash * 23) + Exclusive.GetHashCode();
        _hashCode = hash;
        return hash;
    }
}
```

这个哈希是 World 中 `QueryCache`（`Dictionary<QueryDescription, Query>`）的查找键，相同描述的查询只会被创建一次。

## 8.4 Query 类：匹配引擎

[Query](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L526) 是一个 `partial class`，它持有 World 的所有 Archetype 引用，并维护一个匹配列表。核心字段如下：

```csharp
public partial class Query : IEquatable<Query>
{
    private readonly Archetypes _allArchetypes;             // World 的全部原型
    private readonly NetStandardList<Archetype> _matchingArchetypes;  // 缓存的匹配原型
    private int _allArchetypesHashCode;                     // 上次扫描时的原型列表哈希

    private readonly QueryDescription _queryDescription;
    private readonly BitSet _any;
    private readonly BitSet _all;
    private readonly BitSet _none;
    private readonly BitSet _exclusive;
    private readonly bool _isExclusive;
}
```

### 8.4.1 构造：Signature → BitSet

[构造函数](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L545-L572) 利用 Signature 到 BitSet 的隐式转换：

```csharp
internal Query(Archetypes allArchetypes, QueryDescription description)
{
    _allArchetypes = allArchetypes;
    _matchingArchetypes = new NetStandardList<Archetype>();
    _allArchetypesHashCode = -1;

    Debug.Assert(
        !((description.Any.Count != 0 ||
           description.All.Count != 0 ||
           description.None.Count != 0) &&
          description.Exclusive.Count != 0),
        "If Any, All or None have items then Exclusive may not have any items"
    );

    // 隐式转换：Signature -> BitSet
    _all = description.All;
    _any = description.Any;
    _none = description.None;
    _exclusive = description.Exclusive;

    if (description.Exclusive.Count != 0)
    {
        _isExclusive = true;
    }

    _queryDescription = description;
}
```

> 💡 把 Signature 转成 BitSet 是性能关键：BitSet 内部是 `uint[]`，可以用 `System.Numerics.Vector<uint>` 进行 SIMD 并行比对，一条指令同时检查 8/16/32 个组件位。

### 8.4.2 Matches：核心匹配算法

[Matches](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L579-L582) 是查询的心脏：

```csharp
public bool Matches(BitSet bitset)
{
    return _isExclusive
        ? _exclusive.Exclusive(bitset)
        : _all.All(bitset) && _any.Any(bitset) && _none.None(bitset);
}
```

它在 `BitSet` 上调用四个语义方法，详见 [BitSet.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Utils/BitSet.cs)：

| 方法 | 语义 | 空集合行为 | 实现要点 |
|------|------|-----------|----------|
| `All(other)` | 本集合的所有位都在 other 中被设置 | 空集合视为通过 | `(bit & otherBit) == bit` 逐 uint 比较 |
| `Any(other)` | 本集合至少一个位在 other 中被设置 | **空集合返回 true** | `(bit & otherBit) > 0` 即匹配 |
| `None(other)` | 本集合的所有位在 other 中都未设置 | 空集合视为通过 | `(bit & otherBit) == 0` |
| `Exclusive(other)` | 两个位集完全相等 | 空集合视为通过 | `(bit ^ otherBit) == 0` |

> 🔥 `Any` 在空时返回 `true` 是个常见易错点。这意味着 `new QueryDescription().WithAll<Position>()` 不指定 `Any` 时，每个有 `Position` 的 Archetype 都会通过 `Any` 检查。这与"逻辑与空集合"为真的数学直觉一致。

### 8.4.3 Match：增量扫描

[Match](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L588-L610) 通过缓存的原型列表哈希判断是否需要重新扫描：

```csharp
private void Match()
{
    // 原型列表是否变化？
    var newArchetypesHashCode = _allArchetypes.GetHashCode();
    if (_allArchetypesHashCode == newArchetypesHashCode)
    {
        return;  // 没变化，复用旧列表
    }

    // 重新扫描
    var allArchetypes = _allArchetypes.AsSpan();
    _matchingArchetypes.Clear();
    foreach (var archetype in allArchetypes)
    {
        if (Matches(archetype.BitSet))
        {
            _matchingArchetypes.Add(archetype);
        }
    }

    _allArchetypesHashCode = newArchetypesHashCode;
}
```

> 💡 这是一个**写时刷新**策略：原型列表未变时，迭代查询的代价几乎为 0（一次哈希比较）。只有当 World 创建/销毁 Archetype 时（例如给 Entity 添加新组件组合），`Archetypes.GetHashCode()` 才会变化，触发一次全量扫描。

### 8.4.4 三种迭代器入口

Query 暴露三个迭代器接口（[Query.cs L616-L640](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L616-L640)）：

```csharp
public QueryArchetypeIterator GetArchetypeIterator()
{
    Match();
    return new QueryArchetypeIterator(_matchingArchetypes.AsSpan());
}

public QueryChunkIterator GetChunkIterator()
{
    Match();
    return new QueryChunkIterator(_matchingArchetypes.AsSpan());
}

public QueryChunkEnumerator GetEnumerator()
{
    Match();
    return new QueryChunkEnumerator(_matchingArchetypes.AsSpan());
}
```

三者都先调用 `Match()` 保证列表最新，再返回迭代器。

## 8.5 迭代器：ref struct 与零分配

[Enumerators.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Enumerators.cs) 中定义了三个相关的 ref struct：

### 8.5.1 QueryArchetypeIterator / Enumerator

[QueryArchetypeIterator](file:///d:/Unity/Arch/Arch/src/Arch/Core/Enumerators.cs#L145) 是一个 `readonly ref struct`，包装了 `Span<Archetype>`：

```csharp
public readonly ref struct QueryArchetypeIterator
{
    private readonly Span<Archetype> _archetypes;

    public QueryArchetypeIterator(Span<Archetype> archetypes) => _archetypes = archetypes;

    public QueryArchetypeEnumerator GetEnumerator()
        => new QueryArchetypeEnumerator(_archetypes);
}
```

`QueryArchetypeEnumerator` 内部用 `Enumerator<Archetype>` 倒序遍历（从后往前），这种顺序对 Arch ECS 的"swap-back"删除很重要——可以从尾部移除而不打乱前面索引。

### 8.5.2 QueryChunkEnumerator

[QueryChunkEnumerator](file:///d:/Unity/Arch/Arch/src/Arch/Core/Enumerators.cs#L174) 是双重循环的状态机：外层遍历 Archetype，内层遍历 Chunk：

```csharp
public ref struct QueryChunkEnumerator
{
    private QueryArchetypeEnumerator _archetypeEnumerator;
    private int _index;

    public bool MoveNext()
    {
        unchecked
        {
            if (--_index >= 0) return true;             // 当前原型还有 chunk
            if (!_archetypeEnumerator.MoveNext()) return false;  // 切换下一个原型
            _index = _archetypeEnumerator.Current.Count;
            return true;
        }
    }

    public readonly ref Chunk Current => ref _archetypeEnumerator.Current.GetChunk(_index);
}
```

> ⚠️ 注意 `_index` 初始化为 `Count + 1`，这样首次 `MoveNext` 后 `_index == Count`，正好指向最后一个有效 chunk。

### 8.5.3 QueryChunkIterator

[QueryChunkIterator](file:///d:/Unity/Arch/Arch/src/Arch/Core/Enumerators.cs#L252) 只是 `QueryChunkEnumerator` 的 foreach 适配器。所有这些类型都是 `ref struct`，不能装箱、不能做字段，确保零堆分配。

## 8.6 World.Query 的多种重载

### 8.6.1 直接 Query：获取 Query 对象

[World.cs L411-L424](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L411-L424) 是入口：

```csharp
public Query Query(in QueryDescription queryDescription)
{
    var queryCache = QueryCache;
    if (queryCache.TryGetValue(queryDescription, out var query))
    {
        return query;  // 命中缓存
    }

    query = new Query(Archetypes, queryDescription);
    queryCache[queryDescription] = query;
    return query;
}
```

> 💡 `QueryCache`（[World.cs L247](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L247)）会按 `QueryDescription.GetHashCode()` 复用 Query 对象。**重复使用相同的 QueryDescription 不会重复构建。**

### 8.6.2 Lambda 查询：ForEach 委托

[World.cs L758-L770](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L758-L770) 提供 `ForEach` 委托版本：

```csharp
public void Query(in QueryDescription queryDescription, ForEach forEntity)
{
    var query = Query(in queryDescription);
    foreach (ref var chunk in query)
    {
        ref var entityLastElement = ref chunk.Entity(0);
        foreach (var entityIndex in chunk)
        {
            var entity = Unsafe.Add(ref entityLastElement, entityIndex);
            forEntity(entity);
        }
    }
}
```

[Templates/World.Query.cs](file:///d:/Unity/Arch/Arch/src/Arch/Templates/World.Query.cs) 通过 T4 模板生成最多 9 个泛型参数的重载：

```csharp
world.Query(in desc, (Entity e, ref Position p, ref Velocity v) =>
{
    p.X += v.X * dt;
});
```

### 8.6.3 IForEach 接口查询：内联

[InlineQuery\<T\>](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L778-L792) 用接口约束 `where T : struct, IForEach`：

```csharp
public void InlineQuery<T>(in QueryDescription queryDescription) where T : struct, IForEach
{
    var t = new T();
    var query = Query(in queryDescription);
    foreach (ref var chunk in query)
    {
        ref var entityFirstElement = ref chunk.Entity(0);
        foreach (var entityIndex in chunk)
        {
            var entity = Unsafe.Add(ref entityFirstElement, entityIndex);
            t.Update(entity);
        }
    }
}
```

`IForEach` 接口定义在 [World.cs L50-L58](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L50-L58)：

```csharp
public interface IForEach
{
    public void Update(Entity entity);
}
```

> 🔥 因为 `T` 是 `struct`，JIT 会为每个具体类型生成一份代码，`Update` 调用被**完全内联**。这避免了委托的间接调用开销和捕获闭包导致的堆分配。

### 8.6.4 并行查询 ParallelQuery

[Templates/World.ParallelQuery.cs](file:///d:/Unity/Arch/Arch/src/Arch/Templates/World.ParallelQuery.cs) 模板生成并行版本，借助 `JobScheduler` 把 chunk 切片分发到多个工作线程，签名形如：

```csharp
public void ParallelQuery<T0>(in QueryDescription description, IForEach<T0> forEach, JobScheduler scheduler)
```

使用方式：

```csharp
world.ParallelQuery(in desc, new MoveForwardJob { Dt = Time.deltaTime }, JobScheduler.Shared);
```

> ⚠️ 并行查询中修改的组件必须是独立的（不同 Entity 之间无引用共享），否则会出现数据竞争。位置、速度这种"按 Entity 隔离"的数据适合并行处理。

## 8.7 性能技巧

### 8.7.1 缓存 QueryDescription

`QueryDescription` 是 `struct`，但内部的 `Signature` 字段引用了 `ComponentType[]` 数组。**避免在每帧的 Update 循环中 `new QueryDescription()`**：

```csharp
// ❌ 不推荐：每帧分配
void Update()
{
    var desc = new QueryDescription().WithAll<Position, Velocity>();
    world.Query(in desc, ...);  // 虽然 QueryCache 命中，但 desc 仍要重新计算哈希
}

// ✅ 推荐：静态缓存
private static readonly QueryDescription MoveDesc = new QueryDescription()
    .WithAll<Position, Velocity>();

void Update()
{
    world.Query(in MoveDesc, ...);
}
```

### 8.7.2 内联查询：struct + IForEach

对于热路径，用 `struct` 实现 `IForEach`：

```csharp
public readonly struct MoveForward : IForEach
{
    public readonly float Dt;
    public MoveForward(float dt) => Dt = dt;

    public void Update(Entity entity) { /* ... */ }
}

world.InlineQuery<MoveForward>(in MoveDesc);
```

`InlineQuery<T>` 还有一个重载接受 `ref T iForEach`，允许带状态的结构体复用：

```csharp
var job = new MoveForward(Time.deltaTime);
world.InlineQuery(in MoveDesc, ref job);
```

### 8.7.3 批量操作优于逐实体

[World.cs L832-L863](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L832-L863) 的 `Destroy(in QueryDescription)` 直接遍历原型 chunk，不逐个回调，速度远快于 `Query` + `Destroy(entity)`：

```csharp
world.Destroy(in desc);  // 一次性销毁所有匹配实体
```

同理有 `world.Set<T>(in desc, in T value)`、`world.Add<T>(in desc, in T value)`、`world.Remove<T>(in desc)` 等批量 API。

## 8.8 配套示例

完整的演示代码见 `Assets/Scripts/Chapter08/QueryDemo.cs`，它演示了：

1. 构造多种 QueryDescription（All/Any/None/Exclusive）
2. 用 Lambda 查询遍历实体
3. 用 IForEach struct 实现内联查询
4. 通过 `world.CountEntities` 与 `world.GetEntities` 获取统计
5. 验证 `QueryCache` 复用机制（同一 QueryDescription 第二次返回相同 Query 对象）

```csharp
// 示例片段
var world = World.Create();
for (int i = 0; i < 1000; i++)
{
    world.Create(new Position { X = i, Y = i }, new Velocity { X = 1, Y = 0 });
}

var moveDesc = new QueryDescription().WithAll<Position, Velocity>();

// Lambda 查询
world.Query(in moveDesc, (ref Position p, ref Velocity v) =>
{
    p.X += v.X;
});

// 内联查询
world.InlineQuery<MoveJob>(in moveDesc);

public readonly struct MoveJob : IForEach<Position, Velocity>
{
    public void Update(ref Position p, ref Velocity v) => p.X += v.X;
}
```

> 📖 完整代码请运行 Chapter08 场景观察输出，对比不同查询方式的耗时。

## 本章小结

| 概念 | 所在位置 | 作用 |
|------|----------|------|
| `Signature` | [Query.cs L34](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L34) | 组件类型集合 + 缓存哈希 |
| `Signature._hashCode` | [Query.cs L44](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L44) | 避免重复哈希计算 |
| `Signature → BitSet` | [Query.cs L289](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L289) | 把组件列表转位向量以走 SIMD |
| `QueryDescription` | [Query.cs L314](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L314) | All/Any/None/Exclusive 四组过滤 |
| `WithAll<T>()` | [Query.cs L395](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L395) | 链式 API |
| `QueryDescription.Build()` | [Query.cs L382](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L382) | 修改后刷新缓存哈希 |
| `Query.Matches` | [Query.cs L579](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L579) | All∧Any∧None 或 Exclusive |
| `Query.Match` | [Query.cs L588](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L588) | 通过原型列表哈希做增量刷新 |
| `BitSet.All/Any/None/Exclusive` | [BitSet.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Utils/BitSet.cs) | 位集匹配原语，支持向量化 |
| `QueryArchetypeIterator` | [Enumerators.cs L145](file:///d:/Unity/Arch/Arch/src/Arch/Core/Enumerators.cs#L145) | 倒序遍历原型 |
| `QueryChunkEnumerator` | [Enumerators.cs L174](file:///d:/Unity/Arch/Arch/src/Arch/Core/Enumerators.cs#L174) | 双重循环遍历 chunk |
| `World.QueryCache` | [World.cs L247](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L247) | 复用 Query 实例 |
| `InlineQuery<T>` | [World.cs L778](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L778) | struct + IForEach 内联零分配 |
| `ParallelQuery` | [World.ParallelQuery.cs](file:///d:/Unity/Arch/Arch/src/Arch/Templates/World.ParallelQuery.cs) | 多线程并行遍历 chunk |

下一章我们将解析 Arch 的事件系统——一个默认关闭、需要源码集成的可选机制。
