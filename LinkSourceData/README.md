# LinkSourceData - C# 源码链接与构建打包工具

一个用于创建 C# 源代码文件硬链接/符号链接，以及快速编译打包 DLL 到指定目录的命令行工具。

## 功能特性

### link 命令（源码链接）

- ✅ 递归搜索源文件夹中的所有 `.cs` 文件
- ✅ **自动过滤 `bin` 和 `obj` 目录**（不链接编译输出）
- ✅ 在目标文件夹下创建以源文件夹名称命名的子目录
- ✅ 保持源文件夹内的相对目录结构
- ✅ 支持硬链接和符号链接两种模式
- ✅ **自动处理超长路径（超过260字符）** - 使用扩展路径语法，无需修改注册表
- ✅ 自动跳过已存在的文件
- ✅ 详细的执行日志和统计信息

### pack 命令（编译打包）

- ✅ 指定 `.csproj` 或 `.sln` 快速编译项目
- ✅ 自动将构建输出的**所有文件和子目录**复制到指定目录下的项目名子目录
- ✅ 支持 `.sln` 解决方案，自动收集所有子项目输出
- ✅ 可指定目标框架（默认 `netstandard2.1`）
- ✅ 自动过滤测试/示例/基准测试相关的文件
- ✅ 保持原始目录结构，遇到相同文件直接覆盖

## 使用方法

### 基本语法

```bash
# 源码链接
LinkSourceData link <源文件夹> <目标文件夹> [链接类型]

# 编译打包
LinkSourceData pack <csproj或sln路径> <输出文件夹> [目标框架] [运行时]
```

---

## link 命令

### 参数说明

| 参数       | 说明                                           |
| ---------- | ---------------------------------------------- |
| 源文件夹   | 包含`.cs` 文件的源目录路径                   |
| 目标文件夹 | 创建链接的目标目录路径                         |
| 链接类型   | `hard`（硬链接，默认）或 `sym`（符号链接） |

### 目录结构说明

程序会自动在目标目录下创建以源文件夹名称命名的子目录：

```bash
# 示例命令
LinkSourceData link D:\Projects\Arch\Src D:\Unity\Assets\Plugins hard

# 结果：在 D:\Unity\Assets\Plugins\Src\ 下创建链接
# 保持源文件夹内的相对目录结构
```

### 链接类型对比

| 特性       | 硬链接 (hard)        | 符号链接 (sym)             |
| ---------- | -------------------- | -------------------------- |
| 管理员权限 | 不需要               | Windows 可能需要           |
| 跨分区支持 | ❌ 仅限同一分区      | ✅ 支持跨分区              |
| 文件共享   | 完全共享数据块       | 指向源文件路径             |
| 适用场景   | 同一分区内的项目共享 | 跨分区或需要符号链接的场景 |

### 使用示例

```bash
# 使用硬链接（默认）
LinkSourceData link D:\Unity\Arch\Arch\src\Arch D:\Unity\Arch\unity\Assets\ArchSource\Core

# 显式指定 hard
LinkSourceData link D:\Project1\Src D:\Project2\Src hard

# 使用符号链接
LinkSourceData link D:\Project1\Src D:\Project2\Src sym
```

### 输出示例

```
源目录: D:\Unity\Arch\Arch\src\Arch
目标目录: D:\Unity\Arch\TestLink\Arch
链接类型: 硬链接

✓ 创建链接: Buffer\CommandBuffer.cs
✓ 创建链接: Buffer\SparseSet.cs
✓ 创建链接: Core\Archetype.cs
✓ 创建链接: Core\Chunk.cs

=== 执行结果 ===
创建链接: 114 个文件
跳过已存在: 0 个文件
失败: 0 个文件
```

### 验证硬链接

```bash
fsutil hardlink list "D:\Unity\Arch\TestLink\Core\World.cs"
```

---

## pack 命令

### 参数说明

| 参数            | 说明                                                      |
| --------------- | --------------------------------------------------------- |
| csproj或sln路径 | 要编译的项目文件（`.csproj`）或解决方案文件（`.sln`） |
| 输出文件夹      | DLL 复制的目标目录路径                                    |
| 目标框架        | 编译的目标框架，默认`netstandard2.1`                    |
| 运行时          | 目标运行时，默认`win-x64`                                |

### 常用目标框架

| 框架               | 说明                                        |
| ------------------ | ------------------------------------------- |
| `netstandard2.1` | .NET Standard 2.1（默认，兼容 Unity 2021+） |
| `netstandard2.0` | .NET Standard 2.0（兼容 Unity 2018+）       |
| `net6.0`         | .NET 6                                      |
| `net8.0`         | .NET 8                                      |

### 常用运行时

| 运行时         | 说明              |
| -------------- | ----------------- |
| `win-x64`    | Windows x64（默认）|
| `win-arm64`  | Windows ARM64     |
| `linux-x64`  | Linux x64         |
| `osx-x64`    | macOS x64         |

### 使用示例

```bash
# 编译单个项目，DLL 输出到 目标目录/项目名/ 下（默认 netstandard2.1, win-x64）
LinkSourceData pack D:\Arch\src\Arch\Arch.csproj D:\Unity\Assets\Plugins

# 指定目标框架
LinkSourceData pack D:\Arch\src\Arch\Arch.csproj D:\Unity\Assets\Plugins net8.0

# 指定目标框架和运行时
LinkSourceData pack D:\Arch\src\Arch\Arch.csproj D:\Unity\Assets\Plugins net8.0 win-x64
LinkSourceData pack D:\Arch\src\Arch\Arch.csproj D:\Unity\Assets\Plugins net6.0 linux-x64

# 编译解决方案
LinkSourceData pack D:\Arch\Arch.sln D:\Unity\Assets\Plugins net8.0 win-x64
```

### 工作流程

1. 执行 `dotnet publish -r {运行时}` 以 Release 配置发布项目
2. 从发布输出目录（`bin/Release/{目标框架}/{运行时}/publish`）收集所有文件和子目录
3. 自动过滤测试、示例、基准测试相关的文件
4. 在目标目录下创建**以项目名/解决方案名命名的子目录**
5. 保持原始目录结构复制所有文件，相同文件直接覆盖

### 自动过滤规则

以下文件会被自动排除：

- `*.Test.dll` / `*.Tests.dll`（测试项目）
- `*.Benchmark.dll` / `*.Benchmarks.dll`（基准测试项目）
- `*.Sample.dll` / `*.Samples.dll`（示例项目）
- `testhost.*.dll`、`Microsoft.NET.Test.*.dll`（测试运行器）
- `NUnit*.dll`、`xunit*.dll`、`Moq.*.dll`、`Shouldly.*.dll`（测试框架）
- `BenchmarkDotNet*.dll`（基准测试框架）

### 输出示例

```
项目: D:\Arch\src\Arch\Arch.csproj
输出: D:\Unity\Assets\Plugins
框架: netstandard2.1
类型: 单个项目

=== 开始发布 ===
  已成功生成。

=== 复制文件到输出目录 ===
  输出子目录: Arch/
  ✓ Arch.dll
  ✓ Arch.xml
  ✓ Arch.deps.json
  ✓ runtimes/win/lib/netstandard2.0/System.dll

=== 执行结果 ===
复制文件: 4 个

输出根目录: D:\Unity\Assets\Plugins
  Arch\Arch.dll (256KB)
  Arch\Arch.xml (12KB)
  Arch\Arch.deps.json (3KB)
  Arch\runtimes\win\lib\netstandard2.0\System.dll (128KB)
```

---

## 典型使用场景

### 场景 1：Unity 项目共享 C# 源码（link）

```bash
LinkSourceData link D:\Libraries\MyLib\Src D:\Unity\MyGame\Assets\MyLib\Src
```

修改源码后 Unity 项目立即看到变化，无需手动拷贝。

### 场景 2：多个项目共享代码（link）

```bash
LinkSourceData link D:\Shared\Core D:\ProjectA\Core
LinkSourceData link D:\Shared\Core D:\ProjectB\Core
```

所有项目编辑的都是同一份物理文件。

### 场景 3：编译 DLL 给 Unity 使用（pack）

```bash
# 将 C# 库编译为 DLL 并输出到 Unity Plugins 目录下的项目名子目录
# 结果：D:\Unity\MyGame\Assets\Plugins\Arch\Arch.dll
LinkSourceData pack D:\Arch\src\Arch\Arch.csproj D:\Unity\MyGame\Assets\Plugins
```

Unity 会自动识别 Plugins 目录下的 DLL 并将其作为程序集引用。

### 场景 4：编译整个解决方案（pack）

```bash
LinkSourceData pack D:\Arch\Arch.sln D:\Unity\MyGame\Assets\Plugins net6.0
# 结果：D:\Unity\MyGame\Assets\Plugins\Arch\*.dll
```

自动收集解决方案中所有项目的构建输出，过滤测试项目后复制到解决方案名子目录。

## 注意事项

### link 相关

1. **硬链接限制**：硬链接只能在同一分区内创建。如果源文件和目标文件在不同分区，请使用符号链接。
2. **文件修改**：硬链接的文件是完全共享数据块的，修改任何一个链接都会影响所有链接。
3. **删除源文件**：删除源文件不会影响硬链接，硬链接会继续存在直到所有链接都被删除。
4. **已存在文件**：如果目标位置已存在同名文件，程序会自动跳过并记录。
5. **Git 仓库**：推荐在 `.gitignore` 中忽略链接的目录。

### pack 相关

1. **依赖 .NET SDK**：pack 命令依赖本机安装的 `dotnet` CLI，需确保已安装对应版本的 .NET SDK。
2. **框架兼容性**：选择目标框架时需确保目标环境支持该框架（如 Unity 需使用 `netstandard2.0` 或 `netstandard2.1`）。
3. **第三方依赖**：pack 仅复制项目自身的 DLL 和 XML 文档，不会复制 NuGet 依赖包。如需包含依赖，请在项目 `.csproj` 中设置相关属性。

## 系统要求

- Windows 操作系统
- .NET 10.0 或更高版本
- pack 命令需要对应目标框架的 .NET SDK

## 编译

```bash
cd d:\Unity\ArchSource\LinkSourceData

# 发布为单文件（依赖 .NET Runtime）
dotnet publish -c Release -r win-x64 -p:PublishDir="d:\Unity\ArchSource" --nologo

# 发布为独立应用（包含 .NET Runtime，体积较大）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishDir="d:\Unity\ArchSource" --nologo

# 发布为跨平台应用（不指定运行时）
dotnet publish -c Release -p:PublishDir="d:\Unity\ArchSource" --nologo
```

编译后的可执行文件位于项目根目录：`LinkSourceData.exe`

### 发布参数说明

| 参数                       | 说明                                      |
| -------------------------- | ----------------------------------------- |
| `-r win-x64`             | 指定 Windows x64 运行时（单文件发布必需） |
| `-r win-arm64`           | 指定 Windows ARM64 运行时                 |
| `-r linux-x64`           | 指定 Linux x64 运行时                     |
| `-r osx-x64`             | 指定 macOS x64 运行时                     |
| `--self-contained true`  | 包含 .NET Runtime（独立部署）             |
| `--self-contained false` | 依赖系统安装的 .NET Runtime（默认）       |
| `-p:PublishDir=<路径>`   | 指定发布输出目录（避免与项目根目录冲突）  |

## 许可证

本工具为开源工具，可自由使用和修改。
