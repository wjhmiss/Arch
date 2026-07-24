# LinkSourceData - C# 文件链接工具

一个用于创建 C# 源代码文件硬链接/符号链接的命令行工具，允许多个项目共享同一份源代码。

## 功能特性

- ✅ 递归搜索源文件夹中的所有 `.cs` 文件
- ✅ **自动过滤 `bin` 和 `obj` 目录**（不链接编译输出）
- ✅ 在目标文件夹下创建以源文件夹名称命名的子目录
- ✅ 保持源文件夹内的相对目录结构
- ✅ 支持硬链接和符号链接两种模式
- ✅ **自动处理超长路径（超过260字符）** - 使用扩展路径语法，无需修改注册表
- ✅ 自动跳过已存在的文件
- ✅ 详细的执行日志和统计信息
- ✅ 友好的错误提示和解决方案建议

## 使用方法

### 基本语法

```bash
LinkSourceData <源文件夹> <目标文件夹> [链接类型]
```

### 参数说明

| 参数 | 说明 |
|------|------|
| 源文件夹 | 包含 `.cs` 文件的源目录路径 |
| 目标文件夹 | 创建链接的目标目录路径 |
| 链接类型 | `hard`（硬链接，默认）或 `sym`（符号链接） |

### 目录结构说明

程序会自动在目标目录下创建以源文件夹名称命名的子目录：

```bash
# 示例命令
LinkSourceData D:\Projects\Arch\Src D:\Unity\Assets\Plugins hard

# 结果：在 D:\Unity\Assets\Plugins\Arch\ 下创建链接
# 保持源文件夹内的相对目录结构
```

### 链接类型对比

| 特性 | 硬链接 (hard) | 符号链接 (sym) |
|------|--------------|---------------|
| 管理员权限 | 不需要 | Windows 可能需要 |
| 跨分区支持 | ❌ 仅限同一分区 | ✅ 支持跨分区 |
| 文件共享 | 完全共享数据块 | 指向源文件路径 |
| 适用场景 | 同一分区内的项目共享 | 跨分区或需要符号链接的场景 |

## 使用示例

### 1. 使用硬链接（默认）

```bash
# 将 Arch 源码链接到 Unity 项目
LinkSourceData D:\Unity\Arch\Arch\src\Arch D:\Unity\Arch\unity\Assets\ArchSource\Core

# 或显式指定 hard
LinkSourceData D:\Project1\Src D:\Project2\Src hard
```

### 2. 使用符号链接

```bash
LinkSourceData D:\Project1\Src D:\Project2\Src sym
```

## 典型使用场景

### 场景 1：Unity 项目共享 C# 源码

将一个 C# 库的源码链接到 Unity 项目中，使得 Unity 可以直接编译和使用：

```bash
LinkSourceData D:\Libraries\MyLib\Src D:\Unity\MyGame\Assets\MyLib\Src
```

这样修改 `D:\Libraries\MyLib\Src` 中的代码，Unity 项目会立即看到变化，无需手动拷贝。

### 场景 2：多个项目共享代码

多个 C# 项目共享同一份核心代码：

```bash
# 项目A
LinkSourceData D:\Shared\Core D:\ProjectA\Core

# 项目B
LinkSourceData D:\Shared\Core D:\ProjectB\Core
```

所有项目编辑的都是同一份物理文件。

## 输出示例

```
创建目标目录: D:\Unity\Arch\TestLink
源目录: D:\Unity\Arch\Arch\src\Arch
目标目录: D:\Unity\Arch\TestLink
链接类型: 硬链接

✓ 创建链接: Buffer\CommandBuffer.cs
✓ 创建链接: Buffer\SparseSet.cs
✓ 创建链接: Core\Archetype.cs
✓ 创建链接: Core\Chunk.cs
...

=== 执行结果 ===
创建链接: 114 个文件
跳过已存在: 0 个文件
失败: 0 个文件
```

## 验证硬链接

使用 Windows 内置工具验证硬链接是否创建成功：

```bash
# 查看文件的所有硬链接
fsutil hardlink list "D:\Unity\Arch\TestLink\Core\World.cs"
```

输出示例：
```
\Unity\Arch\Arch\src\Arch\Core\World.cs
\Unity\Arch\TestLink\Core\World.cs
```

## 注意事项

1. **硬链接限制**：硬链接只能在同一分区内创建。如果源文件和目标文件在不同分区，请使用符号链接。

2. **文件修改**：硬链接的文件是完全共享数据块的，修改任何一个链接都会影响所有链接。

3. **删除源文件**：删除源文件不会影响硬链接，硬链接会继续存在直到所有链接都被删除。

4. **已存在文件**：如果目标位置已存在同名文件，程序会自动跳过并记录。

5. **Git 仓库**：在 Git 仓库中使用硬链接需要注意：
   - Git 会将硬链接视为普通文件
   - 推荐在 `.gitignore` 中忽略链接的目录
   - 或者将链接目录作为子模块

## 系统要求

- Windows 操作系统
- .NET 10.0 或更高版本

## 编译

```bash
cd d:\Unity\Arch\LinkSourceData
dotnet build -c Release
```

编译后的可执行文件位于：`bin\Release\net10.0\LinkSourceData.exe`

## 许可证

本工具为开源工具，可自由使用和修改。