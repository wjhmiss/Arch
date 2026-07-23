# 第18章 Arch.Persistence 持久化

## 18.1 序列化需求

游戏存档是几乎所有项目都会遇到的功能。在 ECS 中，"存档"意味着：

- 把整个 `World` 的所有 `Entity` 和 `Component` 转换成字节
- 把字节写到磁盘文件
- 启动时读回字节，重建出与之前完全相同的 `World`

但 ECS 的 World 结构很复杂——它由 `Archetype`、`Chunk`、`Signature`、`EntityMap` 等组成。手动写序列化代码非常容易出错。

**Arch.Persistence** 模块提供了一组高层次的序列化器，把整个 World 转换成二进制或 JSON。

📖 模块源码位于 [Arch.Persistence 目录](file:///d:/Unity/Arch/Arch.Extended/Arch.Persistence)，只有 4 个核心文件。

## 18.2 IArchSerializer 接口

所有序列化器都实现统一的 `IArchSerializer` 接口（[Serializer.cs#L17](file:///d:/Unity/Arch/Arch.Extended/Arch.Persistence/Serializer.cs#L17)）：

```csharp
public interface IArchSerializer
{
    // 单个实体（字节/流/IBufferWriter 三种重载）
    byte[] Serialize(World world, Entity entity);
    void Serialize(Stream stream, World world, Entity entity);
    void Serialize(IBufferWriter<byte> writer, World world, Entity entity);

    // 整个 World
    byte[] Serialize(World world);
    void Serialize(Stream stream, World world);
    void Serialize(IBufferWriter<byte> writer, World world);

    // 反序列化
    Entity Deserialize(World world, byte[] entity);
    Entity Deserialize(Stream stream, World world);
    World Deserialize(byte[] world);
    World Deserialize(Stream stream);
}
```

💡 注意 `Deserialize(byte[])` 返回 `World`——会创建全新的 World 实例。而 `Deserialize(World, byte[])` 是把单个 entity 反序列化进**已有的 World**。

⚠️ 官方注释里说明：**反序列化后的实体 ID 会不同**！因为新 World 有自己的 ID 生成器，老 ID 在反序列化时会被映射到新的 ID。如果需要保留 ID，要自己额外维护一个映射表。

## 18.3 ArchBinarySerializer

二进制序列化器是最紧凑、最快的选项（[Serializer.cs#L99](file:///d:/Unity/Arch/Arch.Extended/Arch.Persistence/Serializer.cs#L99)）。它基于 **MessagePack** 实现，注册了一组 Formatter：

```csharp
public class ArchBinarySerializer : IArchSerializer
{
    private readonly IMessagePackFormatter[] _formatters =
    {
        new WorldFormatter(),            // World 顶层
        new ArchetypeFormatter(),        // Archetype
        new ChunkFormatter(),            // Chunk
        new ArrayFormatter(),            // 普通 Array
        new ComponentTypeFormatter(),    // ComponentType
        new SignatureFormatter(),        // Signature（组件类型集合）
        new EntitySlotFormatter(),       // Entity 在 chunk 中的位置
        new EntityFormatter(),           // Entity 结构体
        new JaggedArrayFormatter<int>(-1),  // 内部稀疏数组
        // ...
    };
}
```

### 18.3.1 使用示例

```csharp
using Arch.Core;
using Arch.Persistence;

// 1. 创建并填充 World
var world = World.Create();
for (int i = 0; i < 100; i++)
    world.Create(new Position { X = i, Y = i });

// 2. 序列化
var serializer = new ArchBinarySerializer();
byte[] bytes = serializer.Serialize(world);

// 3. 反序列化
World loaded = serializer.Deserialize(bytes);

Console.WriteLine(loaded.Size);  // 100
```

### 18.3.2 写入文件

更常见的场景是直接写到文件：

```csharp
using (var fs = File.Create("save.bin"))
{
    serializer.Serialize(fs, world);
}

using (var fs = File.OpenRead("save.bin"))
{
    World loaded = serializer.Deserialize(fs);
}
```

🔥 用 `Stream` 重载比 `byte[]` 更省内存——避免一次性把整个 World 加载到内存缓冲区。

## 18.4 ArchJsonSerializer

JSON 序列化器输出人类可读的文本，便于调试。它基于 **Utf8Json**（[Serializer.cs#L238](file:///d:/Unity/Arch/Arch.Extended/Arch.Persistence/Serializer.cs#L238)）。

### 18.4.1 基本用法

```csharp
var jsonSerializer = new ArchJsonSerializer();
string json = jsonSerializer.ToJson(world);
Console.WriteLine(json);

World loaded = jsonSerializer.FromJson(json);
```

输出的 JSON 大致长这样：

```json
{
  "baseChunkSize": 16000,
  "baseChunkEntityCount": 100,
  "slots": { ... },
  "recycledEntityIDs": [],
  "archetypes": [
    {
      "types": {
        "count": 2,
        "components": [
          {"id": 0, "byteSize": 8},
          {"id": 1, "byteSize": 8}
        ]
      },
      "lookup": [...],
      "chunkCount": 1,
      "chunks": [
        {
          "count": 100,
          "capacity": 100,
          "entities": [...],
          "arrays": [...]
        }
      ]
    }
  ]
}
```

📖 完整的 JSON 序列化逻辑见 [Json.cs#L445](file:///d:/Unity/Arch/Arch.Extended/Arch.Persistence/Json.cs#L445) 的 `WorldFormatter`。

### 18.4.2 二进制 vs JSON 对比

| 维度 | ArchBinarySerializer | ArchJsonSerializer |
|------|----------------------|---------------------|
| 文件大小 | 小（紧凑） | 大（含字段名） |
| 速度 | 快 | 慢约 2-3 倍 |
| 人类可读 | 否 | 是 |
| 跨版本兼容 | 较脆弱 | 较灵活 |
| 推荐用途 | 正式发布 | 调试 / 编辑器工具 |

⚠️ Utf8Json 在 Unity 中需要安装对应 NuGet 包，或者在 Unity 中用 `Newtonsoft.Json` 自己写一层适配。Arch.Persistence 的官方包不会自动带入 Utf8Json 依赖。

## 18.5 StreamBufferWriter

`StreamBufferWriter` 是一个**桥接器**——它实现了 `IBufferWriter<byte>`，把数据缓冲写入 `Stream`（[StreamBufferWriter.cs#L10](file:///d:/Unity/Arch/Arch.Extended/Arch.Persistence/StreamBufferWriter.cs#L10)）：

```csharp
public sealed class StreamBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private readonly Stream _destination;
    private int _position, _leased;

    public StreamBufferWriter(Stream destination, int bufferSize = 1024, bool ownsStream = true)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        _destination = destination;
    }
}
```

🔥 它解决了 MessagePack 直接写 `Stream` 性能差的问题——通过内部缓冲批量写入，减少系统调用次数。

### 18.5.1 工作原理

1. **租借缓冲区**：从 `ArrayPool<byte>.Shared` 租借，避免分配
2. **批量写入**：先把数据写到内存缓冲，缓冲满时一次性 `Flush` 到 Stream
3. **归还缓冲**：`Dispose` 时归还到 ArrayPool

```csharp
public void Flush(bool flushUnderlyingStream = false)
{
    if (_position != 0)
    {
        _destination.Write(_buffer, 0, _position);
        _position = 0;
    }
    if (flushUnderlyingStream)
        _destination.Flush();
}
```

### 18.5.2 在大存档场景下使用

```csharp
using var fs = File.Create("large_save.bin");
using var writer = new StreamBufferWriter(fs, bufferSize: 8192);
serializer.Serialize(writer, world);
// Dispose 自动 Flush 并归还 buffer
```

📖 完整源码见 [StreamBufferWriter.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.Persistence/StreamBufferWriter.cs)，仅 130 行。

## 18.6 完整示例：保存 World 到文件并加载

下面是一个完整的"游戏存档"示例：

```csharp
using System.IO;
using Arch.Core;
using Arch.Persistence;

public class SaveSystem
{
    private readonly ArchBinarySerializer _serializer = new();
    private readonly string _savePath;

    public SaveSystem(string savePath)
    {
        _savePath = savePath;
    }

    public void Save(World world)
    {
        using var fs = File.Create(_savePath);
        using var writer = new StreamBufferWriter(fs, bufferSize: 8192);
        _serializer.Serialize(writer, world);
    }

    public World Load()
    {
        if (!File.Exists(_savePath))
            return World.Create();

        using var fs = File.OpenRead(_savePath);
        return _serializer.Deserialize(fs);
    }
}
```

## 18.7 组件注册与自定义序列化

默认情况下，`ArchBinarySerializer` 和 `ArchJsonSerializer` 会用 MessagePack / Utf8Json 的标准格式化器处理你的组件。但如果组件包含：

- 引用类型字段（如 `string`、`List<T>`）
- Unity 类型（如 `Vector3`、`Quaternion`）
- 自定义序列化逻辑

你需要注册**自定义 Formatter**。

### 18.7.1 注册构造函数

两个序列化器都接受 `params` 自定义 Formatter：

```csharp
public ArchBinarySerializer(params IMessagePackFormatter[] custFormatters);

public ArchJsonSerializer(params IJsonFormatter[] custFormatters);
```

### 18.7.2 自定义 Unity Vector3 序列化

```csharp
using MessagePack;
using MessagePack.Formatters;
using UnityEngine;

public class Vector3Formatter : IMessagePackFormatter<Vector3>
{
    public void Serialize(ref MessagePackWriter writer, Vector3 value, MessagePackSerializerOptions options)
    {
        writer.WriteArrayHeader(3);
        writer.WriteSingle(value.x);
        writer.WriteSingle(value.y);
        writer.WriteSingle(value.z);
    }

    public Vector3 Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        reader.ReadArrayHeader();
        return new Vector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle()
        );
    }
}

// 注册
var serializer = new ArchBinarySerializer(new Vector3Formatter());
```

### 18.7.3 注册 JSON 自定义 Formatter

```csharp
using Utf8Json;
using Utf8Json.Formatters;
using UnityEngine;

public class Vector3JsonFormatter : IJsonFormatter<Vector3>
{
    public void Serialize(ref JsonWriter writer, Vector3 value, IJsonFormatterResolver resolver)
    {
        writer.WriteBeginArray();
        writer.WriteSingle(value.x);
        writer.WriteValueSeparator();
        writer.WriteSingle(value.y);
        writer.WriteValueSeparator();
        writer.WriteSingle(value.z);
        writer.WriteEndArray();
    }

    public Vector3 Deserialize(ref JsonReader reader, IJsonFormatterResolver resolver)
    {
        reader.ReadIsBeginArray();
        var x = reader.ReadSingle();
        reader.ReadIsValueSeparator();
        var y = reader.ReadSingle();
        reader.ReadIsValueSeparator();
        var z = reader.ReadSingle();
        reader.ReadIsEndArray();
        return new Vector3(x, y, z);
    }
}

var jsonSerializer = new ArchJsonSerializer(new Vector3JsonFormatter());
```

⚠️ 如果你的组件包含 Unity 的 `UnityEngine.Object` 引用（如 `Texture2D`、`GameObject`），无法直接序列化对象本身——通常做法是序列化它们的 GUID/路径，加载时再通过 `Resources.Load` 恢复引用。

## 18.8 序列化单个 Entity

有时候你不想保存整个 World，只想保存一个实体（例如导出角色）：

```csharp
var entity = world.Create(new Health { Value = 100 }, new Position { X = 5, Y = 5 });

// 序列化单个实体
byte[] bytes = serializer.Serialize(world, entity);

// 反序列化到新 World（注意：实体会获得新 ID！）
var newWorld = World.Create();
Entity loadedEntity = serializer.Deserialize(newWorld, bytes);
Console.WriteLine(newWorld.Get<Health>(loadedEntity).Value);  // 100
```

📖 单实体序列化实现见 [Binary.cs#L18](file:///d:/Unity/Arch/Arch.Extended/Arch.Persistence/Binary.cs#L18) 的 `SingleEntityFormatter`，它会：

1. 写入 Entity ID 和 WorldId
2. 写入组件类型列表
3. 依次序列化每个组件

## 18.9 完整的存档示例

下面是一个真实场景的"游戏存档管理器"：

```csharp
public class GameSaveManager
{
    private readonly ArchBinarySerializer _binarySerializer = new();
    private readonly string _saveDir;

    public GameSaveManager(string saveDir)
    {
        _saveDir = saveDir;
        Directory.CreateDirectory(saveDir);
    }

    public void SaveSlot(World world, string slotName)
    {
        var path = Path.Combine(_saveDir, $"{slotName}.sav");
        using var fs = File.Create(path);
        using var writer = new StreamBufferWriter(fs);
        _binarySerializer.Serialize(writer, world);
        Debug.Log($"Saved to {path}");
    }

    public World LoadSlot(string slotName)
    {
        var path = Path.Combine(_saveDir, $"{slotName}.sav");
        if (!File.Exists(path))
        {
            Debug.LogWarning($"Save slot {slotName} not found");
            return World.Create();
        }

        using var fs = File.OpenRead(path);
        return _binarySerializer.Deserialize(fs);
    }

    public void DeleteSlot(string slotName)
    {
        var path = Path.Combine(_saveDir, $"{slotName}.sav");
        if (File.Exists(path)) File.Delete(path);
    }

    public IEnumerable<string> ListSlots()
    {
        return Directory.GetFiles(_saveDir, "*.sav")
            .Select(Path.GetFileNameWithoutExtension);
    }
}
```

## 18.10 配套示例

本章的配套 Unity 示例代码位于 `Assets/Scripts/Chapter18/PersistenceDemo.cs`，其中包含：

- 一个 `Position` + `Name` 组件组合
- 一个 `SaveSystem`：把 World 写入 `Application.persistentDataPath`
- 一个 `LoadSystem`：从文件加载 World 并打印所有实体
- 一个 JSON 调试工具：把 World 转成 JSON 字符串显示在 UI 上
- 一个 `MonoBehaviour` 入口：演示保存/加载/删除存档槽

运行该示例后，你能在文件系统中找到生成的 `.sav` 文件，重启游戏后状态完全恢复。

## 本章小结

| 概念 / API | 类型 | 说明 |
|-----------|------|------|
| `IArchSerializer` | 接口 | 统一的序列化接口，支持 byte[]、Stream、IBufferWriter |
| `ArchBinarySerializer` | 类 | 基于 MessagePack 的二进制序列化器，紧凑高效 |
| `ArchJsonSerializer` | 类 | 基于 Utf8Json 的 JSON 序列化器，人类可读 |
| `StreamBufferWriter` | 类 | 桥接 `IBufferWriter<byte>` 与 `Stream`，批量写入 |
| `Serialize(World)` | 方法 | 序列化整个 World |
| `Serialize(World, Entity)` | 方法 | 序列化单个实体 |
| `Deserialize(byte[])` | 方法 | 反序列化为新 World（实体 ID 会变） |
| `Deserialize(World, byte[])` | 方法 | 反序列化单个实体到已有 World |
| 自定义 Formatter | 注册 | 处理非托管类型字段（如 Unity Vector3） |
| `ArrayPool<byte>.Shared` | 内部 | StreamBufferWriter 借用数组避免 GC |

下一章我们将学习 **Arch.EventBus**——通过编译时源生成器，实现零开销的跨系统事件总线。
