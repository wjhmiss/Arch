# 第19章 Arch.EventBus 事件总线

## 19.1 为什么需要事件总线？

在第 14 章我们看到，系统之间通过 `Group` 组合可以按顺序执行。但有些场景下，系统之间是**响应式**的：

- 输入系统检测到"按下 A 键"，要通知移动系统开始移动
- 战斗系统造成伤害，要通知 UI 系统显示血条
- AI 系统决定切换状态，要通知动画系统切换播放片段

如果直接互相调用：

```csharp
// 输入系统
class InputSystem : BaseSystem<World, float>
{
    public MovementSystem Movement;  // ❌ 强引用
    public void Update(in float t)
    {
        if (Input.GetKeyDown(KeyCode.A))
            Movement.StartMove();  // 紧耦合
    }
}
```

这种写法有诸多问题：

1. **紧耦合**：InputSystem 必须知道 MovementSystem 的存在
2. **循环依赖**：A 调 B、B 调 A 时无法编译
3. **难以扩展**：增加新响应者要修改发送方代码
4. **难以测试**：单元测试时需要 mock 一堆系统

**事件总线**（Event Bus）是经典的解耦方案：发送方只"广播"事件，不关心谁接收；接收方"订阅"自己感兴趣的事件，不关心谁发送。

📖 Arch.EventBus 的源码位于 [Arch.EventBus 目录](file:///d:/Unity/Arch/Arch.Extended/Arch.EventBus)，只有 4 个文件——但这套设计**全部由源生成器在编译时生成**，运行时零开销。

## 19.2 与 Arch.Core.Events 的区别

Arch 核心包（`Arch.Core`）自带一个简单的事件系统：

- `world.Subscribe<T>(handler)` 订阅实体事件
- `world.Publish<T>(@event)` 发布事件
- 主要用于**实体级**事件（如 `OnCreate`、`OnDestroy`、`OnAdd<T>`、`OnRemove<T>`）

而 Arch.EventBus 是一个**应用级**事件总线，区别如下：

| 维度 | Arch.Core.Events | Arch.EventBus |
|------|------------------|---------------|
| 事件来源 | 实体生命周期 | 任意业务逻辑 |
| 订阅方式 | 运行时 `world.Subscribe` | 编译时 `[Event]` 特性 |
| 调用开销 | 委托调用 | 直接静态方法调用，可内联 |
| 跨系统 | 限于单 World | 全局，跨 World |
| 排序支持 | 无 | 通过 `order` 参数控制顺序 |
| 推荐场景 | 实体增删改组件 | 业务逻辑通信 |

🔥 关键差异：Arch.EventBus 在**编译时**通过源生成器生成所有调度代码，运行时**零虚函数调用、零装箱、零委托分配**。

## 19.3 EventBus 源生成器实现

Arch.EventBus 没有运行时类型——它通过 Roslyn 增量源生成器（`IIncrementalGenerator`）在编译时扫描标记了 `[Event]` 特性的方法，自动生成 `EventBus` 类和 `Hook`/`Unhook` 方法。

源生成器入口见 [SourceGenerator.cs#L15](file:///d:/Unity/Arch/Arch.Extended/Arch.EventBus/SourceGenerator.cs#L15)：

```csharp
[Generator]
public class QueryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. 注册 [Event] 特性（编译时立即生成）
        var attributes = """
            namespace Arch.Bus
            {
                [global::System.AttributeUsage(global::System.AttributeTargets.Method)]
                public class EventAttribute : global::System.Attribute
                {
                    public EventAttribute(int order = 0) { Order = order; }
                    public int Order { get; }
                }
            }
        """;
        context.RegisterPostInitializationOutput(ctx =>
            ctx.AddSource("Attributes.g.cs", SourceText.From(attributes, Encoding.UTF8)));

        // 2. 扫描所有标记 [Event] 的方法
        var methodDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
            static (s, _) => s is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
            static (ctx, _) => GetMethodSymbolIfAttributeof(ctx, "Arch.Bus.EventAttribute")
        ).Where(static m => m is not null)!;

        // 3. 生成 EventBus.g.cs 和 Hooks.g.cs
        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(methodDeclarations.Collect()),
            static (spc, source) => Generate(source.Item1, source.Item2, spc)
        );
    }
}
```

📖 完整实现见 [SourceGenerator.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.EventBus/SourceGenerator.cs)。

## 19.4 EventBus 模型

生成器内部用一组模型组织数据（[EventBus.cs#L9](file:///d:/Unity/Arch/Arch.Extended/Arch.EventBus/EventBus.cs#L9)）：

```csharp
public struct EventBus
{
    public string Namespace { get; set; }
    public IList<Method> Methods;
}

public struct Method
{
    public RefKind RefKind;             // 参数修饰符（in/ref/out）
    public ITypeSymbol EventType;      // 事件类型
    public IList<ReceivingMethod> EventReceivingMethods;
}

public struct ReceivingMethod
{
    public bool Static;                 // 是否静态方法
    public IMethodSymbol MethodSymbol; // 方法符号
    public int Order;                   // 执行顺序
}
```

源生成器收集所有 `[Event]` 方法后，按事件类型分组、按 Order 排序，然后生成最终代码。

### 19.4.1 生成的 EventBus 类

```csharp
namespace Arch.Bus
{
    public partial class EventBus
    {
        // 静态接收者列表（实例方法需要）
        public static List<SomeClass> SomeClass_OnKeyA_KeyEvent = new(128);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Send(in KeyEvent @event)
        {
            // 静态方法直接调用
            SomeStaticClass.OnKeyA(in @event);

            // 实例方法遍历列表
            for (var i = 0; i < SomeClass_OnKeyA_KeyEvent.Count; i++)
                SomeClass_OnKeyA_KeyEvent[i].OnKeyA(in @event);
        }
    }
}
```

🔥 这就是"零开销"的真相——所有调度逻辑都是直接的方法调用，没有字典查找、没有委托、没有装箱。

## 19.5 Hooks 钩子机制

对于实例方法（非静态），源生成器会为所属类生成 `Hook()` 和 `Unhook()` 方法（[Hooks.cs#L54](file:///d:/Unity/Arch/Arch.Extended/Arch.EventBus/Hooks.cs#L54)）：

```csharp
public static class HookExtensions
{
    public static StringBuilder Hook(this StringBuilder sb, IList<EventHook> receivingMethods)
    {
        foreach (var m in receivingMethods)
        {
            sb.AppendLine($"EventBus.{containingSymbol.Name}_{methodName}_{eventType}.Add(this);");
        }
        return sb;
    }

    public static StringBuilder Unhook(this StringBuilder sb, IList<EventHook> receivingMethods)
    {
        foreach (var m in receivingMethods)
        {
            sb.AppendLine($"EventBus.{containingSymbol.Name}_{methodName}_{eventType}.Remove(this);");
        }
        return sb;
    }
}
```

最终在 `partial class` 上生成的代码长这样：

```csharp
public partial class CombatSystem
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Hook()
    {
        EventBus.CombatSystem_OnDamage_DamageEvent.Add(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unhook()
    {
        EventBus.CombatSystem_OnDamage_DamageEvent.Remove(this);
    }
}
```

📖 生成器入口见 [Hooks.cs#L109](file:///d:/Unity/Arch/Arch.Extended/Arch.EventBus/Hooks.cs#L109) 的 `AppendHookList` 方法。

## 19.6 使用示例

### 19.6.1 定义事件

事件就是一个普通的 struct：

```csharp
public readonly record struct DamageEvent
{
    public Entity Target { get; init; }
    public int Amount { get; init; }
    public Entity Source { get; init; }
}

public readonly record struct KeyboardEvent
{
    public World World { get; init; }
    public KeyboardState State { get; init; }
}
```

💡 使用 `record struct` 让事件不可变，并自动获得 `Equals`/`GetHashCode`。

### 19.6.2 定义接收者

```csharp
public partial class DamageSystem : BaseSystem<World, float>
{
    public DamageSystem(World world) : base(world)
    {
        Hook();  // ⭐ 构造时订阅事件
    }

    [Event(order: 0)]
    public void OnDamage(in DamageEvent evt)
    {
        ref var health = ref World.Get<Health>(evt.Target);
        health.Value -= evt.Amount;
        Console.WriteLine($"{evt.Target} took {evt.Amount} damage!");
    }

    public override void Dispose()
    {
        Unhook();  // ⭐ 释放时取消订阅
    }
}

public static partial class DamageLogger
{
    [Event(order: 1)]  // 在 DamageSystem 之后执行
    public static void LogDamage(in DamageEvent evt)
    {
        Console.WriteLine($"[LOG] Damage applied: {evt.Amount}");
    }
}
```

⚠️ 几个要点：

1. 类必须 `partial`，让源生成器注入 `Hook`/`Unhook` 方法
2. 静态方法不需要 `Hook()`，源生成器会直接调用
3. `order` 越小越早执行，相同 order 的执行顺序未定义
4. 实例方法的类必须**不是静态类**，否则无法 `Hook`

### 19.6.3 发送事件

```csharp
// 任何地方都可以发送事件
EventBus.Send(new DamageEvent
{
    Target = enemyEntity,
    Amount = 50,
    Source = playerEntity
});
```

这一行代码会：

1. 调用所有标记了 `[Event]` 且参数为 `DamageEvent` 的方法
2. 按 `order` 排序依次执行
3. 静态接收者直接调用，实例接收者遍历已 Hook 的实例列表

### 19.6.4 完整的输入事件示例

下面是一个完整的输入系统：

```csharp
public readonly record struct KeyEvent
{
    public World World;
    public KeyCode Key;
    public bool IsDown;
}

public partial class InputSystem : BaseSystem<World, float>
{
    public InputSystem(World world) : base(world)
    {
        Hook();
    }

    public override void Update(in float t)
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            EventBus.Send(new KeyEvent
            {
                World = World,
                Key = KeyCode.Space,
                IsDown = true
            });
        }
    }

    public override void Dispose() => Unhook();
}

public partial class JumpSystem : BaseSystem<World, float>
{
    public JumpSystem(World world) : base(world)
    {
        Hook();
    }

    [Event(order: 0)]
    public void OnJumpInput(in KeyEvent evt)
    {
        if (evt.Key != KeyCode.Space || !evt.IsDown) return;

        var query = new QueryDescription().WithAll<Controllable>();
        evt.World.Query(in query, (ref Velocity vel) =>
        {
            vel.Y = 10f;  // 跳起来！
        });
    }

    public override void Dispose() => Unhook();
}

public static partial class SoundSystem
{
    [Event(order: 1)]  // 在 JumpSystem 之后播放声音
    public static void OnJumpSound(in KeyEvent evt)
    {
        if (evt.Key != KeyCode.Space || !evt.IsDown) return;
        Debug.Log("🔊 Jump sound played");
    }
}
```

🔥 注意这里的设计精髓：

- `InputSystem` 不知道有谁在监听
- `JumpSystem` 不知道事件来自哪里
- `SoundSystem` 是静态类，连 Hook 都不需要
- 通过 `order` 控制执行顺序，确保物理逻辑先于音效

## 19.7 官方示例参考

官方 Sample 项目给出了非常完整的 EventBus 用法（[Arch.Extended.Sample/Systems.cs#L196](file:///d:/Unity/Arch/Arch.Extended/Arch.Extended.Sample/Systems.cs#L196)）：

```csharp
public partial class DebugSystem : BaseSystem<World, GameTime>
{
    public DebugSystem(World world) : base(world)
    {
        Hook();  // 在构造时订阅
    }

    [Event(order: 0)]
    public void OnKeyboardEventPrint(ref (World world, KeyboardState state) tuple)
    {
        if (!tuple.state.IsKeyDown(Keys.A)) return;
        Console.WriteLine($"Key a was pressed.");
    }
}

public static partial class EventHandler
{
    [Event(order: 1)]
    public static void OnDeleteStopEntities(ref (World world, KeyboardState state) tuple)
    {
        if (!tuple.state.IsKeyDown(Keys.Delete)) return;

        var queryDesc = new QueryDescription().WithAll<Velocity>();
        tuple.world.Query(in queryDesc, entity => entity.Remove<Velocity>());
    }
}
```

🔥 注意这个例子用 `ValueTuple` 作为事件类型——不需要专门定义 struct！源生成器会自动处理元组类型（去掉括号、点号等特殊字符作为字段名）。

## 19.8 调试与排错

### 19.8.1 查看生成的代码

源生成器会生成两个文件：

- `EventBus.g.cs` —— 包含 `Send` 方法
- `Hooks.g.cs` —— 包含各 partial 类的 `Hook()` / `Unhook()`

在 IDE 中可以通过"分析器 → 查看生成的文件"查看。

### 19.8.2 常见错误

| 症状 | 原因 | 解决方案 |
|------|------|----------|
| `EventBus.Send` 不存在 | 源生成器未运行 | 检查 DLL 是否勾选 `Roslyn Analyzer` |
| `Hook` 方法找不到 | 类不是 `partial` | 加 `partial` 关键字 |
| 静态类无法 Hook | 静态类无实例 | 静态方法不需要 Hook，直接生效 |
| 事件没收到 | 忘记调用 `Hook()` | 在构造函数调用 `Hook()` |
| 事件泄漏 | 忘记调用 `Unhook()` | 实现 `IDisposable` 并调用 `Unhook()` |
| order 错乱 | 多个事件依赖顺序 | 显式设置 `[Event(order: N)]` |

⚠️ 实例方法忘记 `Unhook` 会导致对象无法被 GC 回收（EventBus 仍持有引用）。这是最常见的内存泄漏源！

## 19.9 性能特性

Arch.EventBus 的设计极其注重性能：

1. **编译时调度**：所有 `Send` 调用都是直接的方法调用，可被 JIT 内联
2. **预分配 List**：实例接收者列表预分配 128 容量，避免运行时扩容
3. **`AggressiveInlining`**：所有生成的方法都标记了内联
4. **零装箱**：事件用 `in` 修饰符传递，避免值类型拷贝

📖 实例 List 的预分配见 [EventBus.cs#L197](file:///d:/Unity/Arch/Arch.Extended/Arch.EventBus/EventBus.cs#L197)：

```csharp
sb.AppendLine($"public static List<{containingSymbol}> {containingSymbol.Name}_{methodName}_{eventType} = new List<{containingSymbol}>(128);");
```

## 19.10 配套示例

本章的配套 Unity 示例代码位于 `Assets/Scripts/Chapter19/EventBusDemo.cs`，其中包含：

- 一个 `DamageEvent` 事件定义
- 一个 `DamageSystem`（实例方法接收者，演示 `Hook`/`Unhook`）
- 一个 `DamageLogger`（静态方法接收者，演示无 Hook 调用）
- 一个 `InputRouter`（每隔一段时间发送模拟事件）
- 一个 `MonoBehaviour` 入口：演示完整事件流

运行后控制台会输出：

```
[Damage] Entity#1 took 50 damage (HP: 50)
[Log] Damage applied: 50
[Damage] Entity#1 took 30 damage (HP: 20)
[Log] Damage applied: 30
```

## 本章小结

| 概念 / API | 说明 |
|-----------|------|
| `EventBus.Send(in T)` | 源生成器生成的全局事件发送方法 |
| `[Event(order)]` | 标记一个方法为事件接收者 |
| `Hook()` | 实例方法接收者注册到 EventBus（构造时调用） |
| `Unhook()` | 实例方法接收者从 EventBus 注销（Dispose 时调用） |
| 静态接收者 | 不需要 Hook/Unhook，直接调用 |
| `order` | 控制同一事件的多个接收者执行顺序 |
| `partial class` | 必需，让源生成器注入 Hook/Unhook |
| `in` 参数 | 推荐使用，避免值类型装箱/拷贝 |
| `record struct` | 推荐作为事件类型，不可变 + 自带 Equals |

## 全书结语

至此，我们已经走完了 Arch ECS 框架的全部 19 章。从最基础的 Entity/Component/World，到 System、SourceGenerator、LowLevel、Relationships、Persistence、EventBus——这套生态覆盖了游戏开发中几乎所有常见需求。

回顾全书的核心思想：

1. **数据与逻辑分离**：组件是数据，系统是逻辑，Entity 只是 ID
2. **Archetype 内存布局**：相同组件组合的实体连续存储，缓存友好
3. **编译时优化**：源生成器在编译期生成查询代码，运行时零开销
4. **非托管优先**：关键路径用 `unmanaged` 集合，避免 GC
5. **解耦设计**：事件总线、关系建模让系统之间松耦合

希望本书能帮助你掌握 Arch ECS 框架，并在 Unity 项目中构建出高性能、易维护的游戏架构。

祝编码愉快！🚀
