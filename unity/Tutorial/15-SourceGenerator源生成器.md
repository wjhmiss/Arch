# 第15章 Arch.System.SourceGenerator 源生成器

## 15.1 为什么需要源生成器？

在第 14 章，我们写了一个手动调用 `World.Query` 的 `MovementSystem`：

```csharp
public override void Update(in float dt)
{
    World.Query(in _moveQuery, (ref Position pos, ref Velocity vel) =>
    {
        pos.X += vel.X * dt;  // ❌ dt 无法在静态 lambda 中捕获
    });
}
```

这种写法有几个明显痛点：

1. **闭包捕获开销**：如果 lambda 内要访问 `dt` 等局部变量，会产生闭包分配，引发 GC
2. **手写查询描述**：每个查询都得手写 `QueryDescription().WithAll<...>()`，重复样板
3. **API 繁琐**：必须显式获取 chunk、提取 entity、调用回调
4. **可读性差**：真正的业务逻辑被淹没在 ECS API 中

**Arch.System.SourceGenerator** 模块通过 **Roslyn 增量源生成器** 在编译时分析你标记的方法，自动生成对应的查询调用代码。这一切发生在编译期，运行时**零开销**。

🔥 核心收益：

| 痛点 | 源生成器方案 |
|------|---------------|
| 闭包分配 | 生成的方法直接是 `partial` 的一部分，无 lambda |
| 重复查询描述 | 由特性自动推导 `QueryDescription` |
| API 繁琐 | 你只需写一个普通方法，源生成器生成 `XxxQuery(World)` |
| 可读性 | 业务逻辑直接呈现，无 ECS 噪声 |

## 15.2 安装与启用

### 15.2.1 引用 Arch.System 与源生成器包

在 `.csproj` 中添加：

```xml
<ItemGroup>
  <PackageReference Include="Arch.System" Version="*" />
  <PackageReference Include="Arch.System.SourceGenerator" Version="*">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>analyzers</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

### 15.2.2 在 Unity 中启用 Roslyn 分析器

Unity 默认禁用第三方 Roslyn 分析器。需要做以下配置：

1. 把 `Arch.System.SourceGenerator.dll` 放到 Unity 工程的 `Assets/Plugins/` 目录下（或任意子目录）
2. 在 Unity Inspector 中选中该 DLL，勾选以下选项：
   - ✅ `Roslyn Analyzer`（这是关键，否则源生成器不会运行）
3. 把对应平台的 `Any CPU` 勾选上

⚠️ Unity 对源生成器的支持从 **Unity 2021.2+** 开始。低于此版本需要升级。

💡 升级到 Unity 2022 LTS 或更高版本可获得最稳定的源生成器支持。

## 15.3 核心特性概览

源生成器的所有入口特性都定义在 [Attributes.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Attributes.cs) 中。

### 15.3.1 `[Query]` —— 标记查询方法

```csharp
[AttributeUsage(AttributeTargets.Method)]
public class QueryAttribute : Attribute
{
    public bool Parallel { get; set; }  // 是否并行执行
}
```

把 `[Query]` 标记在一个方法上，源生成器就会为它生成对应的 `XxxQuery(World)` 方法。

### 15.3.2 查询过滤特性

这一组特性用来筛选实体，对应 `QueryDescription` 的四种过滤方式：

| 特性 | 含义 | 对应原生 API |
|------|------|--------------|
| `[All(typeof(T1), typeof(T2))]` | 实体必须同时拥有这些组件 | `WithAll<T1, T2>()` |
| `[Any(typeof(T1), typeof(T2))]` | 实体至少拥有其中一个 | `WithAny<T1, T2>()` |
| `[None(typeof(T1), typeof(T2))]` | 实体必须不拥有这些组件 | `WithNone<T1, T2>()` |
| `[Exclusive(typeof(T1), typeof(T2))]` | 实体只能拥有这些组件 | `WithExclusive<T1, T2>()` |

📖 完整定义见 [Attributes.cs#L25](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Attributes.cs#L25) 至 [Attributes.cs#L107](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Attributes.cs#L107)。

### 15.3.3 组件访问特性 —— 通过参数修饰符表达

源生成器通过**方法参数的修饰符**推导出组件的访问模式，而非通过特性。这是 Arch.System.SourceGenerator 的巧妙设计：

| 参数形式 | 含义 |
|----------|------|
| `ref T component` | 读 + 写（Get/Set） |
| `in T component` | 只读（Get） |
| `out T component` | 只写（Set） |
| `T component`（无修饰） | 拷贝访问，通常用于过滤 |
| `[Data] T param` | 非 ECS 数据，原样传入 |

`[Data]` 特性定义见 [Attributes.cs#L20](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Attributes.cs#L20)：

```csharp
[AttributeUsage(AttributeTargets.Parameter)]
public class DataAttribute : Attribute { }
```

被 `[Data]` 标记的参数不会被当作组件，而是从外部直接传入。常见的用法是传入 `GameTime`、`deltaTime`、相机矩阵等数据。

### 15.3.4 `[Query]` 的并行选项

```csharp
[Query(Parallel = true)]
public static void Move([Data] float dt, ref Position pos, ref Velocity vel)
{
    pos.X += vel.X * dt;
}
```

设置 `Parallel = true` 后，源生成器会生成并行查询代码（基于 `Parallel.For`），适合处理大量实体。但要注意线程安全。

## 15.4 使用示例

### 15.4.1 最简单的查询

```csharp
using Arch.Core;
using Arch.System;

public partial class MovementSystem : BaseSystem<World, float>
{
    public MovementSystem(World world) : base(world) {}

    [Query]
    public void Move(ref Position pos, ref Velocity vel)  // ⚠️ 注意没有 [Data] dt
    {
        pos.X += vel.X;
        pos.Y += vel.Y;
    }

    public override void Update(in float t)
    {
        MoveQuery(World);  // ⭐ 源生成器自动生成的方法
    }
}
```

⚠️ 类必须标记 `partial`，否则源生成器无法注入生成的方法。

源生成器会自动生成一个 `MoveQuery(World world)` 方法，它会：

1. 创建一个 `QueryDescription`（根据参数推断 `WithAll<Position, Velocity>`）
2. 缓存 `Query` 对象避免重复分配
3. 遍历所有匹配的 chunk 并调用你写的 `Move` 方法

### 15.4.2 使用过滤特性

```csharp
[Query]
[All(typeof(Position), typeof(Velocity))]
[None(typeof(Frozen))]  // 排除被冻结的实体
public void Move(ref Position pos, ref Velocity vel)
{
    pos.X += vel.X;
}
```

### 15.4.3 传入数据参数

```csharp
[Query]
public void Move([Data] float dt, ref Position pos, ref Velocity vel)
{
    pos.X += vel.X * dt;
}

public override void Update(in float t)
{
    MoveQuery(World, t);  // 把 dt 传给生成的查询方法
}
```

📖 更多示例可参考官方测试 [AttributeQuerySystem.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.System.SourceGenerator.Tests/AttributeQueryCompilation/AttributeQuerySystem.cs) 与 [ParamQuerySystem.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.System.SourceGenerator.Tests/ParamQueryCompilation/ParamQuerySystem.cs)。

## 15.5 生成的代码解析

让我们看看源生成器到底为我们生成了什么。下面这个方法：

```csharp
[Query]
[All(typeof(IntComponentA))]
public void IncrementA(Entity e)
{
    ref var a = ref World.Get<IntComponentA>(e);
    a.Value++;
}
```

源生成器会在编译时生成如下 `.g.cs` 文件（实际产物见 [AttributeQuerySystem.IncrementA(Entity).g.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.System.SourceGenerator.Tests/AttributeQueryCompilation/ExpectedGeneration/AttributeQuerySystem.IncrementA%28Entity%29.g.cs)）：

```csharp
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Arch.Core;
using Arch.Core.Extensions;
using Arch.Core.Utils;

namespace Arch.System.SourceGenerator.Tests
{
    partial class AttributeQuerySystem
    {
        // 1. 缓存查询描述（避免每帧分配）
        private QueryDescription IncrementA_QueryDescription = new QueryDescription(
            all: new Signature(typeof(global::Arch.System.SourceGenerator.Tests.IntComponentA)),
            any: Signature.Null,
            none: Signature.Null,
            exclusive: Signature.Null
        );

        // 2. 缓存 World 引用，避免重复创建 Query
        private World? _IncrementA_Initialized;
        private Query? _IncrementA_Query;

        // 3. 生成的查询入口方法
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementAQuery(World world)
        {
            if (!ReferenceEquals(_IncrementA_Initialized, world))
            {
                _IncrementA_Query = world.Query(in IncrementA_QueryDescription);
                _IncrementA_Initialized = world;
            }

            foreach (ref var chunk in _IncrementA_Query!)
            {
                ref var entityFirstElement = ref chunk.Entity(0);
                foreach (var entityIndex in chunk)
                {
                    ref readonly var e = ref Unsafe.Add(ref entityFirstElement, entityIndex);
                    IncrementA(@e);  // ⭐ 调用你写的方法
                }
            }
        }
    }
}
```

🔥 关键设计点解读：

1. **查询描述是 `static` 字段**：避免每帧分配
2. **`Query` 对象按 World 缓存**：同一个系统切换 World 也能正确工作
3. **使用 `Unsafe.Add` 直接索引**：跳过边界检查，最大化性能
4. **`AggressiveInlining`**：所有生成方法都被标记内联，消除调用开销

### 15.5.1 多参数方法的生成代码

当方法包含多个组件参数时，源生成器会从 chunk 中批量提取数组：

```csharp
[Query]
public static void IncrementAAndB(ref IntComponentA a, ref IntComponentB b)
{
    a.Value++;
    b.Value++;
}
```

生成的代码大致如下：

```csharp
foreach (ref var chunk in _IncrementAAndB_Query!)
{
    var arrayA = chunk.GetArray<IntComponentA>();
    var arrayB = chunk.GetArray<IntComponentB>();
    var chunkCount = chunk.Count;

    for (var i = 0; i < chunkCount; i++)
    {
        IncrementAAndB(ref arrayA[i], ref arrayB[i]);
    }
}
```

这就是为什么源生成器比手写 lambda 更快——它直接遍历**数组**而非使用委托，**没有闭包，没有委托调用开销**。

## 15.6 系统级聚合：自动生成 Update

如果你给一个 `BaseSystem` 标记了多个 `[Query]` 方法，源生成器还可以为你生成一个聚合的 `Update` 方法（见测试用例 [GeneratedUpdateSystem.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.System.SourceGenerator.Tests/GeneratedUpdateCompilation/GeneratedUpdateSystem.cs)）：

```csharp
public partial class GeneratedUpdateSystem : BaseSystem<World, float>
{
    [Query]
    public void AutoRunA() { /* ... */ }

    [Query]
    public void AutoRunB() { /* ... */ }
}
```

源生成器会生成一个 `Update` 方法依次调用 `AutoRunAQuery(World)` 和 `AutoRunBQuery(World)`，省去你手动重写 `Update` 的麻烦。

## 15.7 实战示例：完整的 SourceGenerator 系统

下面是一个综合运用各种特性的完整示例：

```csharp
using Arch.Core;
using Arch.System;
using Arch.System.SourceGenerator;

public partial class CombatSystem : BaseSystem<World, float>
{
    public CombatSystem(World world) : base(world) {}

    // 普通查询：所有有 Health 的实体每秒掉 1 点血（毒伤）
    [Query]
    [None(typeof(Immunity))]
    public void PoisonDamage(ref Health health)
    {
        health.Value -= 1;
    }

    // 带过滤和数据的查询：所有 Projectile 移动
    [Query]
    [All(typeof(Projectile), typeof(Position))]
    [None(typeof(Expired))]
    public void MoveProjectiles([Data] float dt, ref Position pos, ref Velocity vel)
    {
        pos.X += vel.X * dt;
        pos.Y += vel.Y * dt;
    }

    // 互斥查询：只有 Player 标签的实体（不允许有任何 AI 组件）
    [Query]
    [Exclusive(typeof(PlayerTag), typeof(Position))]
    public void UpdatePlayerInput(ref Position pos)
    {
        // 处理玩家输入...
    }

    // 并行查询：处理大量粒子
    [Query(Parallel = true)]
    public void UpdateParticles(ref Particle p)
    {
        p.Lifetime -= 1;
    }

    public override void Update(in float t)
    {
        PoisonDamageQuery(World);
        MoveProjectilesQuery(World, t);
        UpdatePlayerInputQuery(World);
        UpdateParticlesQuery(World);
    }
}
```

🔥 这段代码看起来几乎全是业务逻辑——没有任何 ECS API 的样板代码。这就是源生成器的威力。

## 15.8 调试技巧

### 15.8.1 查看生成的代码

在 Visual Studio 中：

1. 解决方案资源管理器 → 项目 → **依赖项** → **分析器**
2. 展开 `Arch.System.SourceGenerator` → `View Generated Files`

在 Rider 中：

1. Tools → **Roslyn source-generated files** → 选择对应文件

⚠️ 如果看不到生成的方法，先**重新生成解决方案**（Rebuild），让源生成器运行一次。

### 15.8.2 常见错误排查

| 症状 | 原因 | 解决方案 |
|------|------|----------|
| `MoveQuery` 不存在 | 类没标记 `partial` | 加上 `partial` 关键字 |
| 方法没被调用 | 忘记 `[Query]` 特性 | 检查特性是否正确应用 |
| 组件没被传递 | 参数没加 `ref`/`in` | 数据要可变用 `ref`，只读用 `in` |
| `Parallel` 时崩溃 | 并行修改了非线程安全资源 | 用锁或避免共享状态 |

## 15.9 配套示例

本章的配套 Unity 示例代码位于 `Assets/Scripts/Chapter15/SourceGeneratorDemo.cs`，其中包含：

- 一个 `MovementSystem`：使用 `[Query]` + `[Data]` 实现移动
- 一个 `SpawnerSystem`：演示带过滤的查询
- 一个 `RenderSystem`：演示只读访问（`in` 参数）
- 一个 `MonoBehaviour` 入口：演示如何调用源生成器方法

运行该示例后，你会在 Game 视图中看到 1000+ 实体高效移动，无任何 GC 分配。

## 本章小结

| 特性 / 概念 | 用法 | 说明 |
|-------------|------|------|
| `[Query]` | 方法特性 | 标记一个方法为查询入口，生成 `XxxQuery(World)` 方法 |
| `[Query(Parallel = true)]` | 并行执行 | 用 `Parallel.For` 并行处理 chunk |
| `[All(typeof(T))]` | 过滤 | 实体必须拥有所有指定组件 |
| `[Any(typeof(T))]` | 过滤 | 实体至少拥有其中一个 |
| `[None(typeof(T))]` | 过滤 | 实体必须不拥有这些组件 |
| `[Exclusive(typeof(T))]` | 过滤 | 实体只能拥有这些组件 |
| `[Data]` | 参数特性 | 标记非组件参数，原样传入 |
| `ref T` | 参数修饰符 | 读写访问组件 |
| `in T` | 参数修饰符 | 只读访问组件 |
| `partial class` | 类修饰符 | 必需，否则源生成器无法注入方法 |

下一章我们将学习 **Arch.LowLevel**——一组非托管集合，专为高性能 ECS 设计，避免 GC 压力。
