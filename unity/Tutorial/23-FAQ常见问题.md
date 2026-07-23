# 第23章 FAQ常见问题

## 23.1 概述

本章以 Q&A 形式汇总开发者在使用 Arch 时最常遇到的问题，覆盖：

1. 安装相关问题（5个）
2. 性能相关问题（5个）
3. API 使用问题（5个）
4. Unity 集成问题（5个）
5. 错误信息解读（5个）
6. 与其他 ECS 框架对比
7. 学习资源推荐

> 💡 标记说明：💡 提示 / ⚠️ 警告 / 🔥 重要 / 📖 参考。

---

## 23.2 安装相关问题

### Q1：Arch 支持 Unity 吗？支持哪些 Unity 版本？

**A**：支持。Arch 基于 .NET Standard 2.1，需要：

- Unity 2021.3 LTS 或更高
- `Api Compatibility Level` 设置为 `.NET Standard 2.1` 或 `.NET 4.x`
- 若使用 IL2CPP，建议同时用 [Arch.AOT.SourceGenerator](https://github.com/genaray/Arch.Extended/wiki/AOT-Source-Generator) 提前注册组件

📖 详见 [第01章 安装与环境搭建](01-安装与环境搭建.md) 与 [第20章 Unity集成指南](20-Unity集成指南.md)。

### Q2：安装后报 `The type or namespace 'Arch' could not be found`

**A**：常见原因有 3 个：

1. **DLL 平台设置错误**：选中 `Arch.dll`，Inspector 中确认 `Any Platform` ✅，`Exclude Platforms` 为空
2. **Api Compatibility Level 不对**：`Player Settings → Other → Api Compatibility Level` 必须为 `.NET Standard 2.1` 或 `.NET 4.x`
3. **依赖 DLL 缺失**：Arch 依赖 `CommunityToolkit.HighPerformance`、`Collections.Pooled`、`ZeroAllocJobScheduler`，必须一并安装

⚠️ 若用 NuGetForUnity，依赖会自动拉取；若手动拷贝 DLL，必须每个依赖都拷。

### Q3：使用 `Arch.System.SourceGenerator` 时找不到生成的方法

**A**：源生成器需要在 Unity 中特别配置：

1. 找到 `Arch.System.SourceGenerator.dll`
2. 在 Inspector 中勾选 `Roslyn Analyzer` ✅
3. 取消 `Any Platform`（这是 Editor-only）
4. 重启 Unity（强制重新编译）

📖 详见 [第01章](01-安装与环境搭建.md) 1.3.1 节与 [第15章 SourceGenerator](15-SourceGenerator源生成器.md)。

### Q4：Godot / Stride 等其他引擎能用 Arch 吗？

**A**：可以。Arch 任何支持 .NET Standard 2.1 的环境都能用：

- Godot 4.x (Mono)：通过 NuGet 直接安装
- Stride：通过 NuGet
- MonoGame / FNA：通过 NuGet
- 任何 .NET 6+ 应用：`dotnet add package Arch`

📖 [官方文档](file:///d:/Unity/Arch/Arch.Docs/unity.md) 介绍了 Unity 集成，其他引擎同理。

### Q5：如何升级 Arch 版本？

**A**：取决于安装方式：

- **NuGetForUnity**：菜单 `NuGet → Manage NuGet Packages → Updates`
- **DLL 拷贝**：重新从 [Arch CI/CD](https://github.com/genaray/Arch/actions) 下载 DLL 替换
- **源码引入**：`git pull` 后刷新 Unity

⚠️ 升级前请查看 [Release Notes](https://github.com/genaray/Arch/releases)。Arch 2.x 相比 1.x 有 **Breaking Changes**（如 QueryDescription API 调整）。

---

## 23.3 性能相关问题

### Q6：为什么我的 Arch 查询比预期慢？

**A**：常见原因排查顺序：

1. **是否每帧 new QueryDescription**：应缓存为 `static readonly`
2. **是否用了 Lambda 闭包**：会引发 GC，改用 `IForEach` 结构体
3. **是否遍历了过多组件**：用 `WithNone` 排除不需要的实体
4. **Chunk 大小是否合理**：默认 16KB，大量实体场景可调大
5. **是否在 PURE_ECS 模式**：8 字节 Entity 缓存更友好

📖 详见 [第21章 性能建议](21-最佳实践与陷阱.md) 21.4 节。

### Q7：Arch 与其他 ECS 框架性能对比如何？

**A**：根据官方 [FAQ](file:///d:/Unity/Arch/Arch.Docs/misc/faq.md)：

> 每个 benchmark 实现不同，有时使用过时的 Arch 版本。Arch 有很多 ECS 框架没有的特性——**Chunks**。Chunk 让你能在运行时创建/销毁海量实体并释放内存，但带来少量开销。这是一个值得的折衷。

🔥 关键不是看微基准，而是看**实际场景**：

- 静态实体（不增删）：纯 Archetype 模型更快
- 动态实体（频繁增删）：Chunk 模型更优（Arch 的设计）
- 多线程：Arch 的 ParallelQuery 表现优秀

### Q8：什么时候用 ParallelQuery？

**A**：当满足以下条件时使用：

1. **实体数量 > 10000**：太少时调度开销大于收益
2. **回调逻辑较重**：纯算术运算太少时收益不大
3. **不需要结构变更**：并行回调不能 `Add`/`Remove`/`Destroy`，要用 `CommandBuffer`

```csharp
_world.ParallelQuery(in query, (ref Position p, ref Velocity v) =>
{
    p.X += v.X * dt;
});
```

📖 详见 [第11章 多线程与 Jobs](11-多线程与Jobs.md)。

### Q9：如何减少 GC 分配？

**A**：常见手段：

1. **缓存 QueryDescription**：`static readonly`
2. **用 IForEach 结构体**：避免 Lambda 闭包
3. **避免 GetAllComponents**：装箱开销大，仅调试用
4. **批量创建实体**：用 `World.Create(Span<Entity>)` 而非循环 `Create`
5. **使用 CommandBuffer**：批量结构变更

📖 详见 [第21章](21-最佳实践与陷阱.md) 21.4–21.5 节。

### Q10：Unity Profiler 显示 Lambda 调用有 GC Alloc，怎么办？

**A**：Lambda 捕获局部变量会生成隐藏闭包类。改用 IForEach：

```csharp
// ❌ 闭包分配
float dt = Time.deltaTime;
_world.Query(in query, (ref Position p, ref Velocity v) =>
{
    p.X += v.X * dt;  // 捕获 dt
});

// ✅ 结构体内联
public struct MoveJob : IForEach
{
    public float Dt;
    public void Update(Entity e) { /* ... */ }
}

_world.InlineQuery<MoveJob>(in query);
```

📖 `InlineQuery<T>` 见 [World.cs#L778](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L778)。

---

## 23.4 API 使用问题

### Q11：`entity.Get<T>()` 抛 `Entity is not alive` 异常

**A**：原因有两种：

1. **Entity 已销毁**：用 `entity.IsAlive()` 检查
2. **Version 不匹配**：Entity 被销毁后 ID 被复用，你的旧引用的 Version 已过期

```csharp
if (entity.IsAlive())
{
    ref var pos = ref entity.Get<Position>();
}
```

📖 详见 [第22章](22-调试技巧与工具.md) 22.7.2 节"版本号错误"。

### Q12：如何在 Query 中删除实体？

**A**：**不能直接在 Query 回调中删除**——会破坏迭代器。用 CommandBuffer：

```csharp
var buffer = new CommandBuffer();
_world.Query(in query, (Entity e) =>
{
    if (e.Get<Health>().Value <= 0)
        buffer.Destroy(e);
});
buffer.Playback(_world);
```

📖 详见 [第10章 CommandBuffer 命令缓冲](10-CommandBuffer命令缓冲.md)。

### Q13：`WithAll<T>` 和 `WithAny<T>` 有什么区别？

**A**：

- `WithAll<T>`：实体**必须包含所有**这些组件
- `WithAny<T>`：实体**至少包含一个**这些组件
- `WithNone<T>`：实体**不包含任何一个**
- `WithExclusive<T>`：实体**只包含**这些组件（独占）

```csharp
var query = new QueryDescription()
    .WithAll<Position, Velocity>()    // 必须有这两个
    .WithAny<Alive, Dead>()          // 至少有一个
    .WithNone<Removed>();            // 不能有 Removed
```

📖 详见 [第08章 Query 查询系统](08-Query查询系统源码解析.md)。

### Q14：如何在系统中获取 `World` 和时间？

**A**：继承 `BaseSystem<World, float>`：

```csharp
public class MovementSystem : BaseSystem<World, float>
{
    public MovementSystem(World world) : base(world) { }

    public override void Update(in float t)
    {
        // World 通过 this.World 访问
        // 时间通过 t 参数访问
        World.Query(in _query, (ref Position p, ref Velocity v) =>
        {
            p.X += v.X * t;
        });
    }
}
```

📖 详见 [第14章 Arch.System](14-Arch.System系统框架.md)。

### Q15：如何遍历所有实体（不需要查询）？

**A**：用空的 `QueryDescription`：

```csharp
var allQuery = new QueryDescription();  // 空描述匹配所有
var count = _world.CountEntities(in allQuery);

// 或者遍历
_world.Query(in allQuery, (Entity e) =>
{
    // 处理每个实体
});
```

⚠️ `QueryDescription()` 默认构造函数会创建一个匹配所有实体的描述，见 [Query.cs#L354](file:///d:/Unity/Arch/Arch/src/Arch/Core/Query.cs#L354)。

---

## 23.5 Unity 集成问题

### Q16：在 Unity 中 World 应该在哪里创建？

**A**：在 MonoBehaviour 的 `Awake` 中创建，`OnApplicationQuit` 或 `OnDestroy` 中 Dispose：

```csharp
private World _world;

private void Awake() => _world = World.Create();

private void Update()
{
    var dt = Time.deltaTime;
    _world.Query(in _query, ...);
}

private void OnApplicationQuit() => _world?.Dispose();
```

📖 详见 [第20章 Unity集成指南](20-Unity集成指南.md) 20.5 节。

### Q17：在 Editor 中重启 Play 后报 `World with id 0 already exists`

**A**：上次 Play 没有正确 Dispose World。解决：

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
private static void ClearAllWorlds()
{
    foreach (var world in World.Worlds)
    {
        if (world != null) world.Dispose();
    }
}
```

把它放到任意静态类中即可。详见 [第22章](22-调试技巧与工具.md) 22.7.4 节。

### Q18：Unity Editor 中如何查看 Entity 的组件？

**A**：三种方式：

1. **VS / Rider 断点**：悬停 Entity 变量，展开 `EntityDebugView`（见 [EntityDebugView.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Utils/EntityDebugView.cs)）
2. **自定义 Editor 脚本**：用 `entity.GetAllComponents()` 在 Inspector 显示
3. **Arch.Unity 扩展**：提供 `EntityAuthoring` 组件，可视化编辑

⚠️ `GetAllComponents()` 会装箱 struct，仅用于调试，**不要在主循环调用**。

### Q19：GameObject 和 Entity 怎么同步？

**A**：推荐 Hybrid ECS 模式——**Arch 作为 Model，GameObject 作为 View**：

```csharp
public class ViewBinder : MonoBehaviour
{
    public Entity target;

    private void LateUpdate()
    {
        if (!target.IsAlive()) return;
        ref var pos = ref target.Get<Position>();
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }
}
```

📖 详见 [第20章](20-Unity集成指南.md) 20.8 节"Hybrid ECS 模式"。

### Q20：能用 Arch.Unity 扩展库吗？

**A**：可以，社区维护的 [Arch.Unity](https://github.com/AnnulusGames/Arch.Unity) 提供：

- `WorldManager`：自动管理 World 生命周期
- `EntityAuthoring`：Inspector 中可视化添加组件
- GameObject ↔ Entity 转换器
- Editor 窗口

通过 Package Manager 安装：

```
https://github.com/AnnulusGames/Arch.Unity.git
```

📖 详见 [第20章](20-Unity集成指南.md) 20.6 节。

---

## 23.6 错误信息解读

### Q21：`System.NullReferenceException: Object reference not set to an instance of an object`

**A**：在 Arch 上下文中常见原因：

1. **World 已 Dispose 后还在用**：检查 `_world` 是否为 null
2. **Entity 已销毁**：调用前 `entity.IsAlive()` 检查
3. **World.Worlds[id] 为 null**：通常因为 World 已 Dispose，但 Entity 引用还在

```csharp
// 防御性检查
if (_world == null) return;
if (!entity.IsAlive()) return;
```

### Q22：`Cannot perform structural change during iteration`

**A**：你在 Query 回调中修改了 Archetype。修复：

```csharp
// ❌ 错误
_world.Query(in query, (Entity e) =>
{
    e.Add<Dead>();  // 结构变更！
});

// ✅ 用 CommandBuffer
var buffer = new CommandBuffer();
_world.Query(in query, (Entity e) => buffer.Add(e, new Dead()));
buffer.Playback(_world);
```

📖 详见 [第10章 CommandBuffer](10-CommandBuffer命令缓冲.md)。

### Q23：`Entity is not alive` / `Entity version mismatch`

**A**：你持有的 Entity 引用已过期。原因：

- Entity 已被 `world.Destroy()` 销毁
- ID 被回收复用，但你的 Version 不匹配

```csharp
// 检查
if (entity.IsAlive())
{
    // 安全使用
}
```

⚠️ 销毁 Entity 后立即把变量置 `default`：

```csharp
_world.Destroy(entity);
entity = default;
```

### Q24：`The query description is null` / `Query returned no entities`

**A**：检查 QueryDescription 是否正确：

```csharp
// 检查匹配数
var query = new QueryDescription().WithAll<Position>();
var count = _world.CountEntities(in query);
Console.WriteLine($"Matched: {count}");

if (count == 0)
{
    // 1. 确认组件类型正确
    // 2. 确认实体已创建并包含该组件
    // 3. 确认 World 正确
}
```

### Q25：`Arch.Core.Extensions.Dangerous.* threw exception`

**A**：你使用了 Dangerous 扩展但参数非法。这些方法**不做合法性检查**——传错参数会直接内存损坏。

⚠️ **不要在生产代码使用 Dangerous 扩展**，除非你在写：

- 持久化/序列化
- 快照系统
- 热重载

📖 详见 [第22章](22-调试技巧与工具.md) 22.4 节。

---

## 23.7 与其他 ECS 框架对比

### 23.7.1 Arch vs Unity DOTS (ECS)

| 维度 | Arch | Unity DOTS |
|------|------|-----------|
| 语言 | C#（.NET Standard 2.1） | C#（Unity 专用，Burst 编译） |
| 兼容性 | 任意 .NET 项目 | 仅 Unity 2022+ |
| 性能 | 优秀（JIT 优化） | 极致（Burst + Jobs） |
| 易用性 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| 内存模型 | Archetype + Chunk | Archetype + Chunk |
| 多线程 | ParallelQuery + JobScheduler | C# Job System + Burst |
| 源生成器 | Arch.System.SourceGenerator | Unity ECS SourceGen |
| 序列化 | Arch.Persistence | 内置 |
| AOT | 需注册或 Arch.AOT.SourceGenerator | 原生支持 |

🔥 **选型建议**：

- 跨引擎/纯 C# 项目：**Arch**
- Unity 项目 + 追求极致性能 + 团队能接受 DOTS 复杂度：**Unity DOTS**
- Unity 项目 + 想快速上手 + 中等规模：**Arch**

### 23.7.2 Arch vs Entitas

| 维度 | Arch | Entitas |
|------|------|---------|
| 设计 | Archetype/Chunk | Reactive System + Matcher |
| 性能 | 更优（连续内存） | 良好（响应式有开销） |
| 学习曲线 | 较平缓 | 较陡 |
| Unity 集成 | DLL/源码 | 原生支持（Unity 特化） |
| 事件 | Arch.EventBus / World.Subscribe | 内置 Reactive System |
| 维护状态 | 活跃 | 维护中 |

📖 Entitas 是 Unity 社区早期的热门 ECS，已逐步被 DOTS 与 Arch 替代。

### 23.7.3 Arch vs LeoECS / LeoECSLite

| 维度 | Arch | LeoECSLite |
|------|------|-----------|
| 语言 | C# | C#（Unity 专用） |
| 性能 | 优秀 | 极致（无 GC） |
| 内存模型 | Archetype + Chunk | 紧凑数组 |
| 多 World | ✅ | ✅ |
| 源生成器 | ✅ | ❌ |
| 文档 | 完善（含中文） | 较少 |
| Unity 集成 | 通过 DLL | 原生 |

🔥 LeoECS 是俄罗斯社区的高性能 Unity ECS，比 Arch 更极致但生态较小。

### 23.7.4 Arch vs Svelto.ECS

| 维度 | Arch | Svelto.ECS |
|------|------|-----------|
| 设计 | Archetype/Chunk | Engines + Groups |
| 性能 | 优秀 | 优秀 |
| 学习曲线 | 平缓 | 陡峭（理念独特） |
| 适用范围 | 通用 .NET | 主要 Unity |
| 源生成器 | ✅ | ✅ |

📖 Svelto 强调分层架构与责任分离，更适合大型项目。

### 23.7.5 综合建议

| 场景 | 推荐 |
|------|------|
| Unity 中型项目 | **Arch** |
| Unity 大型项目 + 团队接受 DOTS | **Unity DOTS** |
| 跨引擎项目 | **Arch** |
| .NET 服务端 / 模拟 | **Arch** |
| 学习 ECS 概念 | **Arch** |

---

## 23.8 学习资源推荐

### 23.8.1 官方资源

- **GitHub 主仓库**：[github.com/genaray/Arch](https://github.com/genaray/Arch)
- **Arch.Extended**：[github.com/genaray/Arch.Extended](https://github.com/genaray/Arch.Extended)
- **官方 Wiki**：[github.com/genaray/Arch/wiki](https://github.com/genaray/Arch/wiki)
- **官方文档**（本仓库）：[d:\Unity\Arch\Arch.Docs](file:///d:/Unity/Arch/Arch.Docs)

### 23.8.2 本教程

本教程覆盖 23 章，分 5 部分：

1. **入门篇**（01-03）：环境搭建与第一个程序
2. **核心篇**（04-08）：World / Entity / Component / Archetype / Query 源码解析
3. **进阶篇**（09-13）：事件、CommandBuffer、多线程、性能优化
4. **扩展篇**（14-19）：System / SourceGen / LowLevel / Relationships / Persistence / EventBus
5. **实践篇**（20-23）：Unity 集成、最佳实践、调试、FAQ

📖 详见 [00-目录与导读](00-目录与导读.md)。

### 23.8.3 社区与示例项目

- **Arch.Samples**：[d:\Unity\Arch\Arch\src\Arch.Samples](file:///d:/Unity/Arch/Arch/src/Arch.Samples) — 控制台示例
- **Arch.Extended.Sample**：[d:\Unity\Arch\Arch.Extended\Arch.Extended.Sample](file:///d:/Unity/Arch/Arch.Extended/Arch.Extended.Sample) — 扩展库示例
- **使用 Arch 的项目**：[Arch.Docs/projects-using-arch](file:///d:/Unity/Arch/Arch.Docs/projects-using-arch) — 包含 Cubetory、SS14、SkylandKingdoms 等

### 23.8.4 相关学习资源

- **ECS 模式理论**：[ECS FAQ by Sander Mertens](https://github.com/SanderMertens/ecs-faq)
- **数据导向设计**：[Data-Oriented Design by Richard Fabian](https://www.dataorienteddesign.com/dodbook/)
- **C# 性能优化**：[Performance in .NET](https://learn.microsoft.com/dotnet/core/performance/)
- **C# 源生成器**：[Source Generators Cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md)

### 23.8.5 推荐学习路径

**新手（1-2 周）**：

```
第01章 → 第02章 → 第03章 → 第04章 → 第05章 → 第06章 → 第08章 → 第20章
```

**进阶（2-3 周）**：

```
第07章 → 第09章 → 第10章 → 第11章 → 第12章 → 第13章 → 第21章
```

**扩展（按需）**：

```
第14章 → 第15章 → 第16章 → 第17章 → 第18章 → 第19章 → 第22章 → 第23章
```

---

## 本章小结

| 章节 | 主题 | 问题数 |
|------|------|--------|
| 23.2 | 安装相关 | 5 |
| 23.3 | 性能相关 | 5 |
| 23.4 | API 使用 | 5 |
| 23.5 | Unity 集成 | 5 |
| 23.6 | 错误信息解读 | 5 |
| 23.7 | 框架对比 | Arch vs DOTS/Entitas/LeoECS/Svelto |
| 23.8 | 学习资源 | 官方+社区+推荐路径 |

🔥 **遇到问题时的排查顺序**：

1. 查看本 FAQ
2. 查看 [官方 Wiki](https://github.com/genaray/Arch/wiki)
3. 在 [GitHub Issues](https://github.com/genaray/Arch/issues) 搜索
4. 在 [Discord 社区](https://discord.gg/arch) 提问
5. 提交 Issue 时附上最小复现代码

---

## 全书结语

至此，你已经完成了 Unity Arch ECS 框架新手教程的全部 23 章。回顾全书：

1. **入门篇** 带你搭建环境并写出第一个 Arch 程序
2. **核心篇** 深入源码理解 World / Entity / Component / Archetype / Query 的实现
3. **进阶篇** 探讨事件、CommandBuffer、多线程、性能优化
4. **扩展篇** 介绍 System、源生成器、低级集合、关系建模、持久化、事件总线
5. **实践篇** 聚焦 Unity 集成、最佳实践、调试技巧与常见问题

Arch 的核心理念：

1. **数据与逻辑分离** — 组件是数据，系统是逻辑
2. **Archetype 内存布局** — 相同组件组合连续存储，缓存友好
3. **编译时优化** — 源生成器在编译期生成查询代码
4. **非托管优先** — 关键路径用 struct 与 unmanaged 集合，避免 GC
5. **解耦设计** — 事件总线、关系建模让系统之间松耦合

希望本教程能帮助你在 Unity 项目中构建出**高性能、易维护**的游戏架构。

🚀 **编码愉快，游戏开发顺利！**
