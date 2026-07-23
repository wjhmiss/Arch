# 第06章 Component组件与ComponentRegistry

> 📖 本章我们将深入 Arch ECS 框架的"数据基石"——Component（组件）。组件是 ECS 架构中"数据"的载体，理解组件如何被注册、查找和索引，是掌握后续 Archetype、Query、World 等机制的前提。

源码参考：[ComponentRegistry.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs)

---

## 6.1 Component的本质

在 ECS 架构里，**Entity（实体）**只是 ID，**System（系统）**负责行为，而 **Component（组件）**则承担"数据"。Arch 框架对组件有一个非常硬性的要求：**必须是值类型（struct 或 record struct）**。

```csharp
// ✅ 推荐：使用 record struct，自动获得值相等性
public readonly record struct Position(float X, float Y, float Z);

// ✅ 推荐：使用普通 struct
public struct Health
{
    public int Current;
    public int Max;
}

// ❌ 避免：class 会触发 GC，并且无法被 Chunk 内的数组直接 SoA 存储
public class BadComponent { public int Value; }
```

💡 **为什么必须用 struct？**
- struct 在 C# 中是值类型，分配在栈或连续数组中，**不会触发 GC**。
- struct 数组在内存中是连续布局的，CPU 缓存命中率高（这对 Archetype 的 Chunk 机制至关重要）。
- struct 的拷贝是 bitwise 的，可以通过 `Unsafe.Add` 进行零开销索引访问。

⚠️ 组件应保持**小而专注**——一个组件只表达一个维度的数据。如果一个组件塞了 20 个字段，往往意味着你应当拆分它，让 System 能更精确地查询所需数据。

---

## 6.2 ComponentType结构体 —— 组件的"身份证"

每个被注册的组件都会获得一个 `ComponentType` 实例，它就像组件的身份证，记录了组件的 **唯一 ID** 和 **字节大小**。

源码位置：[ComponentRegistry.cs L14-L67](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L14)

```csharp
public readonly record struct ComponentType
{
    /// <summary> Represents a unique Id for this component. </summary>
    public readonly int Id;          // L20

    /// <summary> Its size in bytes. </summary>
    public readonly int ByteSize;    // L25

    public ComponentType(int id, int byteSize)
    {
        Id = id;
        ByteSize = byteSize;
    }
    ...
}
```

### 6.2.1 Id字段（L20）—— 全局唯一 ID

`Id` 是组件在全局的索引号，从 0 开始递增。它被用作：
- **BitSet 中的位偏移**：Archetype 的 `BitSet` 通过 `Id` 标记自己持有哪些组件。
- **`_componentIdToArrayIndex` 查找数组的下标**：通过组件 Id 一步跳到 Chunk 内的组件数组。
- **签名哈希计算**：用位图模拟无序哈希时，`Id` 决定了位的设置位置。

🔥 **关键点**：`Id` 是按注册顺序递增的，**一旦分配不会再变**（除非显式 `Replace`）。这意味着同一个组件在 World 内任何地方引用，`Id` 都一致。

### 6.2.2 ByteSize字段（L25）—— 组件大小

`ByteSize` 通过 `Unsafe.SizeOf<T>()` 计算（见 [L314-L317](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L314)），用于：
- 计算 Chunk 内每个组件数组需要分配多少字节。
- 计算 `EntitiesPerChunk`（一个 16KB Chunk 能装下多少实体）。

```csharp
private static int SizeOf<T>()
{
    return typeof(T).IsValueType ? Unsafe.SizeOf<T>() : IntPtr.Size;
}
```

⚠️ 注意：对于引用类型，`SizeOf` 返回 `IntPtr.Size`（指针大小），**但这不代表框架鼓励使用引用类型组件**——引用类型会带来 GC 风险，详见 6.8 节。

### 6.2.3 Type属性（L42-L46）—— 反查 Type

`ComponentType` 只存了 `Id` 和 `ByteSize`，那如何反向拿到 `System.Type`？答案是通过 `ComponentRegistry.Types` 数组反查：

```csharp
public Type Type
{
    get => ComponentRegistry.Types[Id]!;   // L45
}
```

💡 这是一个 O(1) 的反查——`Id` 直接作为数组下标。这种"正向 `Dictionary<Type, ComponentType>` + 反向 `Type[]`"的双向映射设计，在 ECS 框架中极为常见，因为它避免了任何方向查找时退化为 O(log n) 或 O(1) 但有 hash 开销。

### 6.2.4 隐式转换运算符（L53-L66）

为了让 API 更顺手，`ComponentType` 提供了两个隐式转换：

```csharp
// Type -> ComponentType
public static implicit operator ComponentType(Type value)
{
    return Component.GetComponentType(value);   // L55: 自动注册并返回
}

// ComponentType -> Type
public static implicit operator Type(ComponentType value)
{
    return value.Type;   // L65: 反查
}
```

有了这两个隐式转换，编写代码时可以无缝在 `Type` 与 `ComponentType` 之间切换：

```csharp
ComponentType ct = typeof(Position);   // 隐式注册
Type t = ct;                           // 隐式反查
```

---

## 6.3 ComponentRegistry静态类 —— 全局组件登记处

源码位置：[ComponentRegistry.cs L79-L338](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L79)

`ComponentRegistry` 是一个静态类，整个进程内只有一个实例。它负责**登记所有被使用过的组件类型**。

### 6.3.1 核心字段

```csharp
public static class ComponentRegistry
{
    // 正向映射：Type -> ComponentType
    private static readonly Dictionary<Type, ComponentType> _typeToComponentType = new(64);   // L81

    // 反向映射：Id -> Type（数组下标即 Id）
    private static Type?[] _types = new Type[64];   // L82
    ...
}
```

这两个字段是 Registry 的全部状态：
- `_typeToComponentType`：用 `Dictionary` 实现 O(1) 查找，键是 `Type`，值是 `ComponentType`（含 Id 和 ByteSize）。
- `_types`：用数组实现 O(1) 反查，下标就是 `ComponentType.Id`，元素是对应的 `Type`。

🔥 **为什么同时维护两个结构？** 这是为了在不同场景下都能 O(1) 查找：
- 用户写 `world.Create<Position>(...)` 时，编译器传入 `typeof(Position)`，需要正向查 Id → 用 `_typeToComponentType`。
- 框架内部用 Id 查找时（如 Chunk 反序列化），需要反向查 Type → 用 `_types`。

### 6.3.2 Add<T>() 方法 —— 注册组件

源码位置：[L161-L164](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L161)

```csharp
public static ComponentType Add<T>()
{
    return Add(typeof(T), SizeOf<T>());
}
```

实际逻辑在私有的 `Add(Type, int)` 中（[L121-L135](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L121)）：

```csharp
private static ComponentType Add(Type type, int typeSize)
{
    if (TryGet(type, out var meta))
    {
        return meta;          // 已注册，直接返回
    }

    // 注册并分配 component id
    meta = new ComponentType(Size, typeSize);   // Size 是当前已注册数量，作为新 Id
    _typeToComponentType.Add(type, meta);
    _types = _types.Add(Size, type);            // 在 Size 位置插入

    Size++;
    return meta;
}
```

💡 **Id 分配规则**：新组件的 `Id = ComponentRegistry.Size`（当前已注册数量），然后 `Size++`。这是一种**单调递增**的分配策略，永远不会冲突。

### 6.3.3 Has<T>() / TryGet<T>() 方法 —— 查询组件

```csharp
public static bool Has<T>() => Has(typeof(T));          // L184

public static bool Has(Type type)
{
    return TypeToComponentType.ContainsKey(type);       // L198
}

public static bool TryGet<T>(out ComponentType componentType)
{
    return TryGet(typeof(T), out componentType);        // L291
}

public static bool TryGet(Type type, out ComponentType componentType)
{
    return TypeToComponentType.TryGetValue(type, out componentType);   // L305
}
```

这两个 API 都是 O(1) 字典查找，性能开销极小。

### 6.3.4 Size 属性 —— 已注册组件数

```csharp
public static int Size
{
    get;
    private set;
}
```

`Size` 既是当前已注册组件数量，也是下一个新组件将获得的 `Id`。它只增不减（即使 `Remove`，也只是把 `_types[Id]` 置 null，不会回收 Id）。

### 6.3.5 线程安全性说明

源码注释 [L74-L78](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L74)：

> *Simultaneous readers are supported, but simultaneous readers and writers are not. Ensure that modification happens on an isolated thread. In World this is implemented via marked structural-change methods.*

⚠️ **重要**：
- **多线程读** 是安全的（如并行 Job 中查询 `Component<T>.ComponentType.Id`）。
- **多线程读写混合** 是 **不安全** 的。结构性变更（Add/Remove 组件）必须在主线程或隔离线程上完成。
- 框架在 `World` 的结构变更方法上用 `[StructuralChange]` 特性标记，便于调试和验证。

---

## 6.4 Component<T>静态类 —— 编译期静态缓存

源码位置：[L457-L479](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L457)

```csharp
public static class Component<T>
{
    /// <summary> A static reference to information about the compile time static registered class. </summary>
    public static readonly ComponentType ComponentType;     // L463

    /// <summary> An Signature for this given set of components. </summary>
    public static readonly Signature Signature;             // L468

    static Component()   // L474 - 静态构造函数
    {
        ComponentType = ComponentRegistry.Add<T>();
        Signature = new Signature(ComponentType);
    }
}
```

### 6.4.1 静态构造函数的延迟初始化机制

C# 的静态构造函数有一个非常重要的特性：**首次访问类的任何静态成员时才会执行**。这意味着：

```csharp
// 第一次访问 Component<Position>.ComponentType 时：
// 1. 触发静态构造函数
// 2. 调用 ComponentRegistry.Add<Position>() 注册组件
// 3. 缓存 ComponentType 和 Signature 到静态字段
// 4. 后续访问直接读静态字段，零开销
```

🔥 **这是 ECS 性能优化的关键技巧**：
- 第一次使用某组件时，自动完成注册（lazy registration）。
- 后续所有访问都是 **静态字段读取**，JIT 编译后通常会被内联为直接的内存访问。
- `Component<T>.ComponentType.Id` 这种调用在 Release 模式下基本是 **零开销** 的。

💡 这也解释了为什么框架能优雅地处理"用户首次创建一个 `Position` 实体"的场景——不需要显式注册，访问 `Component<Position>` 的瞬间就完成了。

### 6.4.2 Signature 字段

每个组件类型还会预先构造一个**只包含自己**的 `Signature`（签名）。这个 Signature 后续会被用于组合成更复杂的查询签名或 Archetype 签名。预先缓存避免了每次创建时的重复分配。

---

## 6.5 Component静态类 —— 运行时查找入口

源码位置：[L385-L447](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L385)

### 6.5.1 GetComponentType 方法

```csharp
public static ComponentType GetComponentType(Type type)
{
    return !ComponentRegistry.TryGet(type, out var index) 
        ? ComponentRegistry.Add(type)   // 未注册则注册
        : index;                         // 已注册则返回
}
```

这是运行时通过 `Type` 拿 `ComponentType` 的入口，被 `ComponentType` 的隐式转换运算符调用（[L55](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L55)）。

⚠️ 注意它的注释：*"Not thread-safe; ensure no other threads are accessing or modifying the ComponentRegistry."* —— 在并行 Job 中应使用 `Component<T>.ComponentType`（已缓存），不要走这条路径。

### 6.5.2 GetHashCode方法 —— 无序哈希的BitSet思想

源码位置：[L410-L434](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L410)

```csharp
public static int GetHashCode(Span<ComponentType> obj)
{
    // 1. 找出最大 Id，决定需要多少个 uint 来装位图
    var highestId = 0;
    foreach (ref var cmp in obj)
    {
        if (cmp.Id > highestId) highestId = cmp.Id;
    }

    // 2. 在栈上分配 uint 数组，模拟一个 BitSet
    var length = BitSet.RequiredLength(highestId + 1);
    Span<uint> stack = stackalloc uint[length];
    var spanBitSet = new SpanBitSet(stack);

    // 3. 把每个组件 Id 对应的位设为 1
    foreach (ref var type in obj)
    {
        spanBitSet.SetBit(type.Id);
    }

    // 4. 对位图数组求哈希
    return GetHashCode(stack);
}
```

🔥 **这是一段非常精妙的代码**。它要解决的问题是：**给定一个组件类型数组，计算一个与顺序无关的唯一哈希**。

为什么要"与顺序无关"？因为 `{Position, Velocity}` 和 `{Velocity, Position}` 应该映射到**同一个** Archetype（它们描述的是同一种实体结构）。

**算法思想（BitSet 模拟）：**
1. 假设组件 Id 最大是 5，那么我们需要 6 个 bit 位。
2. 对于 `{Position(Id=0), Velocity(Id=2)}`，把第 0 位和第 2 位置 1，得到 `0b101 = 5`。
3. 对于 `{Velocity(Id=2), Position(Id=0)}`，结果同样是 `0b101 = 5` —— **顺序无关！**
4. 然后对这个 bit 数组用 `HashCode.AddSpan` 求最终哈希。

💡 **为什么用 `stackalloc`？** 因为组件数量通常很少（< 32 个），所需的 uint 数组也很小（通常 1-2 个 uint），完全可以在栈上分配，避免 GC 压力。这就是 `SpanBitSet` 存在的意义——它是一个**栈上的不可变 BitSet**。

最终调用 [L441-L446](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L441)：

```csharp
public static int GetHashCode(Span<uint> span)
{
    var hashCode = new HashCode();
    hashCode.AddSpan(span);
    return hashCode.ToHashCode();
}
```

`HashCode.AddSpan` 是 .NET 内置的高性能哈希组合方法，能很好地分散输入。

---

## 6.6 ArrayRegistry —— 数组工厂池化机制

源码位置：[L343-L375](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L343)

```csharp
public static class ArrayRegistry
{
    private static readonly JaggedArray<Func<int, Array>> _createFactories = new(128);

    public static void Add<T>()
    {
        _createFactories.Add(Component<T>.ComponentType.Id, ArrayFactory<T>.Create);
    }

    public static Array GetArray(ComponentType type, int capacity)
    {
        return _createFactories.TryGetValue(type.Id, out Func<int, Array> func)
            ? func(capacity)                                  // 走预注册的工厂
            : Array.CreateInstance(type.Type, capacity);       // 退化到反射
    }

    private static class ArrayFactory<T>
    {
        public static readonly Func<int, Array> Create = 
            capacity => capacity == 0 ? Array.Empty<T>() : new T[capacity];
    }
}
```

### 6.6.1 设计动机

Chunk 在创建时需要为每个组件类型分配一个数组：`new Position[capacity]`、`new Velocity[capacity]`…。但 **`new T[capacity]` 是泛型方法**，必须 JIT 编译才能生成具体类型的数组。

问题来了：当 Chunk 持有的是 `ComponentType`（运行时信息）而非 `T`（编译时类型）时，如何创建数组？常规做法是 `Array.CreateInstance(type.Type, capacity)`，但**这会走反射，慢且无法被 JIT 优化**。

### 6.6.2 解决方案：预编译的数组工厂

`ArrayRegistry.Add<T>()` 在初始化阶段（通常由 Source Generator 或启动代码调用）把 `ArrayFactory<T>.Create` 这个委托缓存起来。后续 `GetArray` 通过 Id 查找委托，直接调用 `new T[capacity]`，**完全避免反射**。

💡 这是 ECS 框架的常见技巧：**把泛型信息"具象化"为委托**，让运行时代码能享受编译时泛型的性能。Unity DOTS 也有类似机制（TypeManager）。

---

## 6.7 最佳实践

### ✅ DO：使用 struct 或 record struct

```csharp
public readonly record struct Position(float X, float Y, float Z);
public readonly record struct Velocity(float X, float Y, float Z);
public struct Health { public int Current; public int Max; }
```

### ✅ DO：保持组件小而专注

```csharp
// 好：拆分组件
public readonly record struct Position(float X, float Y, float Z);
public readonly record struct Rotation(float X, float Y, float Z, float W);
public readonly record struct Scale(float X, float Y, float Z);

// 不好：塞一个大组件
public struct Transform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;
    public Vector3 WorldPosition;   // 缓存字段，应当作为 System 计算结果
}
```

### ⚠️ DON'T：在组件中放引用类型

```csharp
// ❌ 危险
public struct Inventory
{
    public Item[] Items;       // 数组是引用类型
    public string Name;        // string 是引用类型
}
```

为什么危险？
1. **GC 风险**：每次创建新实体都会复制组件，引用类型字段会增加堆分配压力。
2. **缓存不友好**：Chunk 内的数组虽然连续，但里面存的引用指向堆上随机位置，CPU 缓存失效。
3. **并行化困难**：多个 Job 同时修改同一引用对象的字段会引发数据竞争，难以检测。

🔥 **替代方案**：
- 用 `FixedString` 替代 `string`（如 Unity Collections 包）。
- 用 `NativeList<T>` / `PooledList<T>` 替代 `List<T>`，但要注意生命周期。
- 把"引用类型字段"重构为独立的实体（如 Inventory 拆为多个 Item 实体）。

### ✅ DO：用 readonly record struct 表达不可变数据

```csharp
public readonly record struct Tag { }   // 空标签组件，标记"具有某种属性"
public readonly record struct Speed(float Value);
```

`readonly record struct` 的好处：
- 值相等性自动实现（`Equals`/`GetHashCode`）。
- 不可变，避免误修改。
- `with` 表达式支持：`var newSpeed = speed with { Value = 10.0f };`

---

## 6.8 配套示例

完整的示例代码位于：`Assets/Scripts/Chapter06/ComponentDemo.cs`

示例涵盖：
1. 定义 `Position`、`Velocity`、`Health` 三种组件（struct / record struct）。
2. 创建 World 并实例化多个 Entity。
3. 通过 `World.Create<T1, T2, ...>(components)` 触发组件自动注册。
4. 验证 `ComponentRegistry.Size` 增长。
5. 用 `Component<Position>.ComponentType.Id` 获取组件 Id，并打印。
6. 演示 `ComponentRegistry.Has<T>()` 与 `TryGet<T>()` 的区别。
7. 用 `Unsafe.SizeOf<Position>()` 验证 `ByteSize` 字段。

```csharp
// 示例片段（完整代码见配套脚本）
public readonly record struct Position(float X, float Y, float Z);
public readonly record struct Velocity(float X, float Y, float Z);
public struct Health { public int Current; public int Max; }

var world = World.Create();

// 触发 Component<Position> 等的静态构造 → 自动注册到 ComponentRegistry
var entity = world.Create(new Position(0, 0, 0), new Velocity(1, 0, 0), new Health { Current = 100, Max = 100 });

Debug.Log($"Position Id = {Component<Position>.ComponentType.Id}");
Debug.Log($"Position ByteSize = {Component<Position>.ComponentType.ByteSize}");
Debug.Log($"Total registered components = {ComponentRegistry.Size}");
```

---

## 本章小结

| 概念 | 位置 | 作用 |
|------|------|------|
| `ComponentType` | [L14-L67](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L14) | 组件的"身份证"，含 Id 和 ByteSize |
| `ComponentType.Id` | [L20](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L20) | 全局唯一递增 ID，用于 BitSet 与数组反查 |
| `ComponentType.ByteSize` | [L25](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L25) | 组件字节数，用于 Chunk 容量计算 |
| `ComponentType.Type` | [L42-L46](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L42) | 通过 Id 反查 `System.Type` |
| 隐式转换运算符 | [L53-L66](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L53) | 在 `Type` 与 `ComponentType` 间无缝切换 |
| `ComponentRegistry` | [L79-L338](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L79) | 全局组件登记处，进程唯一 |
| `_typeToComponentType` | [L81](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L81) | 正向映射 `Type → ComponentType` |
| `_types` | [L82](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L82) | 反向映射 `Id → Type` |
| `Add<T>()` | [L161-L164](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L161) | 注册组件并分配 Id |
| `Size` 属性 | [L105-L111](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L105) | 已注册组件数（也即下一个 Id） |
| 线程安全性 | [L74-L78](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L74) | 多读单写，结构变更需隔离线程 |
| `Component<T>` | [L457-L479](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L457) | 编译期静态缓存，零开销访问 |
| `Component<T>.ComponentType` | [L463](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L463) | 静态字段，触发延迟注册 |
| `Component.GetComponentType` | [L398-L401](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L398) | 运行时通过 `Type` 查找/注册 |
| `Component.GetHashCode` | [L410-L434](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L410) | 用 BitSet 思想计算无序哈希 |
| `ArrayRegistry` | [L343-L375](file:///d:/Unity/Arch/Arch/src/Arch/Core/ComponentRegistry.cs#L343) | 预编译数组工厂，避免运行时反射 |

📖 **下一章**：我们将以 `ComponentType.Id` 为基础，深入 [Archetype.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Archetype.cs) 与 [Chunk.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Chunk.cs)，看看 Arch 是如何把同种结构的实体紧凑地放进 16KB 内存块的。
