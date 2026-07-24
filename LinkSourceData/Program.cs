using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LinkSourceData;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1 || args[0] is "--help" or "-h")
        {
            PrintUsage();
            return args.Length > 0 ? 0 : 1;
        }

        var command = args[0].ToLower();

        return command switch
        {
            "link" => CmdLink(args[1..]),
            "pack" => CmdPack(args[1..]),
            _ => CmdLink(args) // 默认为 link 模式（向后兼容）
        };
    }

    #region pack 命令

    static int CmdPack(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: LinkSourceData pack <csproj或sln路径> <输出文件夹> [目标框架]");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  LinkSourceData pack D:\\Arch\\src\\Arch\\Arch.csproj D:\\Unity\\Assets\\Plugins netstandard2.1");
            Console.WriteLine("  LinkSourceData pack D:\\Arch\\Arch.sln D:\\Unity\\Assets\\Plugins net6.0");
            return 1;
        }

        var projectPath = Path.GetFullPath(args[0]);
        var outputDir = Path.GetFullPath(args[1]);
        var targetFramework = args.Length > 2 ? args[2] : "netstandard2.1";

        // 验证项目文件存在
        if (!File.Exists(projectPath))
        {
            Console.WriteLine($"错误: 项目文件不存在: {projectPath}");
            return 1;
        }

        var isSolution = projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"项目: {projectPath}");
        Console.WriteLine($"输出: {outputDir}");
        Console.WriteLine($"框架: {targetFramework}");
        Console.WriteLine($"类型: {(isSolution ? "解决方案" : "单个项目")}");
        Console.WriteLine();

        // 1. 构建
        Console.WriteLine("=== 开始构建 ===");
        var buildOk = BuildProject(projectPath, targetFramework);
        if (!buildOk)
        {
            Console.WriteLine("构建失败，终止操作。");
            return 1;
        }
        Console.WriteLine("构建成功！");
        Console.WriteLine();

        // 2. 收集并复制 DLL
        Console.WriteLine("=== 复制 DLL 到输出目录 ===");
        var (copiedCount, skippedCount) = isSolution
            ? CopySolutionOutputs(projectPath, targetFramework, outputDir)
            : CopyProjectOutput(projectPath, targetFramework, outputDir);

        Console.WriteLine();
        Console.WriteLine("=== 执行结果 ===");
        Console.WriteLine($"复制 DLL: {copiedCount} 个");
        Console.WriteLine($"跳过已存在: {skippedCount} 个");

        // 3. 列出输出目录内容
        Console.WriteLine();
        Console.WriteLine($"输出目录: {outputDir}");
        if (Directory.Exists(outputDir))
        {
            foreach (var file in Directory.GetFiles(outputDir, "*.dll", SearchOption.AllDirectories).OrderBy(f => f))
            {
                var rel = Path.GetRelativePath(outputDir, file);
                var size = new FileInfo(file).Length;
                Console.WriteLine($"  {rel} ({size / 1024}KB)");
            }
        }

        return 0;
    }

    static bool BuildProject(string projectPath, string targetFramework)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectPath}\" -c Release -f {targetFramework}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // 只显示关键输出
        var lines = output.Split('\n')
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("failed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var line in lines)
            Console.WriteLine($"  {line.Trim()}");

        if (process.ExitCode != 0)
        {
            if (lines.Count == 0)
                Console.WriteLine($"  构建输出:\n{output}");
            if (!string.IsNullOrEmpty(error))
                Console.WriteLine($"  错误:\n{error}");
        }

        return process.ExitCode == 0;
    }

    static (int copied, int skipped) CopyProjectOutput(string csprojPath, string targetFramework, string outputDir)
    {
        var projectDir = Path.GetDirectoryName(csprojPath)!;
        var projectName = Path.GetFileNameWithoutExtension(csprojPath);

        // 查找构建输出目录
        var buildOutputDir = Path.Combine(projectDir, "bin", "Release", targetFramework);
        if (!Directory.Exists(buildOutputDir))
        {
            Console.WriteLine($"  未找到构建输出: {buildOutputDir}");
            return (0, 0);
        }

        return CopyDlls(buildOutputDir, outputDir);
    }

    static (int copied, int skipped) CopySolutionOutputs(string slnPath, string targetFramework, string outputDir)
    {
        var slnDir = Path.GetDirectoryName(slnPath)!;
        int totalCopied = 0, totalSkipped = 0;

        // 查找 sln 目录下所有 bin/Release/{tfm} 目录
        var binDirs = Directory.EnumerateDirectories(slnDir, "bin", SearchOption.AllDirectories)
            .Select(bin => Path.Combine(bin, "Release", targetFramework))
            .Where(Directory.Exists)
            .ToList();

        // 去重（避免同一个 bin 被多次处理）
        var seen = new HashSet<string>();

        foreach (var binDir in binDirs)
        {
            // 跳过测试/示例项目的输出
            var rel = Path.GetRelativePath(slnDir, binDir);
            if (ShouldFilterPath(rel))
                continue;

            var (c, s) = CopyDlls(binDir, outputDir, seen);
            totalCopied += c;
            totalSkipped += s;
        }

        return (totalCopied, totalSkipped);
    }

    static (int copied, int skipped) CopyDlls(string sourceDir, string outputDir, HashSet<string>? seen = null)
    {
        int copied = 0, skipped = 0;
        seen ??= new HashSet<string>();

        // 只复制 .dll 和 .xml (文档) 文件，排除 .pdb 和测试相关
        var files = Directory.GetFiles(sourceDir)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".Test.dll", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".Tests.dll", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".Benchmark.dll", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".Benchmarks.dll", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".Sample.dll", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".Samples.dll", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).StartsWith("testhost.", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).StartsWith("Microsoft.NET.", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).StartsWith("BenchmarkDotNet", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);

            // 去重
            if (seen.Contains(fileName))
            {
                skipped++;
                continue;
            }
            seen.Add(fileName);

            var destFile = Path.Combine(outputDir, fileName);

            // 检查目标是否已存在且内容相同
            if (File.Exists(destFile))
            {
                var srcInfo = new FileInfo(file);
                var dstInfo = new FileInfo(destFile);
                if (srcInfo.Length == dstInfo.Length && srcInfo.LastWriteTime == dstInfo.LastWriteTime)
                {
                    skipped++;
                    continue;
                }
            }

            File.Copy(file, destFile, overwrite: true);
            Console.WriteLine($"  ✓ {fileName}");
            copied++;
        }

        return (copied, skipped);
    }

    static bool ShouldFilterPath(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var filterNames = new[] { "Test", "Tests", "Sample", "Samples", "Benchmark", "Benchmarks" };
        return parts.Any(p => filterNames.Any(f => p.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    #endregion

    #region link 命令

    static int CmdLink(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: LinkSourceData link <源文件夹> <目标文件夹> [链接类型]");
            return 1;
        }

        var sourceDir = Path.GetFullPath(args[0]);
        var targetDir = Path.GetFullPath(args[1]);
        var linkType = args.Length > 2 ? args[2].ToLower() : "hard";

        if (!Directory.Exists(sourceDir))
        {
            Console.WriteLine($"错误: 源目录不存在: {sourceDir}");
            return 1;
        }

        var sourceFolderName = new DirectoryInfo(sourceDir).Name;
        targetDir = Path.Combine(targetDir, sourceFolderName);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            Console.WriteLine($"创建目标目录: {targetDir}");
        }

        Console.WriteLine($"源目录: {sourceDir}");
        Console.WriteLine($"目标目录: {targetDir}");
        Console.WriteLine($"链接类型: {(linkType == "sym" ? "符号链接" : "硬链接")}");
        Console.WriteLine();

        try
        {
            var (createdCount, skippedCount, errorCount) = CreateFileLinks(sourceDir, targetDir, linkType);

            Console.WriteLine();
            Console.WriteLine("=== 执行结果 ===");
            Console.WriteLine($"创建链接: {createdCount} 个文件");
            Console.WriteLine($"跳过已存在: {skippedCount} 个文件");
            Console.WriteLine($"失败: {errorCount} 个文件");

            return errorCount > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    static (int createdCount, int skippedCount, int errorCount) CreateFileLinks(
        string sourceDir, string targetDir, string linkType)
    {
        int createdCount = 0, skippedCount = 0, errorCount = 0, filteredCount = 0;

        var csFiles = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories);

        foreach (var sourceFile in csFiles)
        {
            try
            {
                var relativePath = Path.GetRelativePath(sourceDir, sourceFile);

                if (ShouldFilterDirectory(relativePath))
                {
                    filteredCount++;
                    continue;
                }

                var targetFile = Path.Combine(targetDir, relativePath);
                var targetFileDir = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetFileDir) && !Directory.Exists(targetFileDir))
                    Directory.CreateDirectory(targetFileDir);

                if (File.Exists(targetFile))
                {
                    skippedCount++;
                    continue;
                }

                if (linkType == "sym")
                    File.CreateSymbolicLink(targetFile, sourceFile);
                else
                    CreateHardLink(targetFile, sourceFile);

                createdCount++;
            }
            catch (Exception ex)
            {
                var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
                Console.WriteLine($"✗ 失败: {relativePath}");
                Console.WriteLine($"  错误: {ex.Message}");
                errorCount++;
            }
        }

        if (filteredCount > 0)
            Console.WriteLine($"\n已过滤目录 (bin/obj/test/sample/benchmark/...): {filteredCount} 个文件");

        return (createdCount, skippedCount, errorCount);
    }

    static bool ShouldFilterDirectory(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var exactFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "sample", "samples", "test", "tests",
            "benchmark", "benchmarks", "example", "examples", "demo", "demos"
        };

        var containsFilter = new[] { "Test", "Sample", "Benchmark" };

        return parts.Any(part =>
            exactFilter.Contains(part) ||
            containsFilter.Any(f => part.Contains(f, StringComparison.OrdinalIgnoreCase) && part.Length > f.Length));
    }

    static void CreateHardLink(string linkPath, string targetPath)
    {
        string fullLinkPath = Path.GetFullPath(linkPath);
        string fullTargetPath = Path.GetFullPath(targetPath);

        if (fullLinkPath.Length >= 260 || fullTargetPath.Length >= 260)
        {
            fullLinkPath = "\\\\?\\" + fullLinkPath;
            fullTargetPath = "\\\\?\\" + fullTargetPath;
        }

        if (!CreateHardLinkWin32(fullLinkPath, fullTargetPath, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException($"无法创建硬链接 (错误: {error})");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLink")]
    static extern bool CreateHardLinkWin32(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    #endregion

    static void PrintUsage()
    {
        Console.WriteLine("LinkSourceData - 源码链接与 NuGet 打包工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  LinkSourceData link <源文件夹> <目标文件夹> [hard|sym]");
        Console.WriteLine("  LinkSourceData pack <csproj或sln路径> <输出文件夹> [目标框架]");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  link  创建源码硬链接/符号链接（默认命令）");
        Console.WriteLine("  pack  构建项目并复制 DLL 到目标文件夹");
        Console.WriteLine();
        Console.WriteLine("link 命令:");
        Console.WriteLine("  递归搜索 .cs 文件，在目标目录创建硬链接或符号链接");
        Console.WriteLine("  自动过滤 bin/obj/test/sample/benchmark 等目录");
        Console.WriteLine("  在目标文件夹下创建以源文件夹名称命名的子目录");
        Console.WriteLine();
        Console.WriteLine("pack 命令:");
        Console.WriteLine("  使用 dotnet build 构建项目，复制 DLL 到目标文件夹");
        Console.WriteLine("  默认目标框架: netstandard2.1");
        Console.WriteLine("  自动过滤测试/示例/基准测试相关的 DLL");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  # 源码链接");
        Console.WriteLine("  LinkSourceData link D:\\Arch\\src\\Arch D:\\Unity\\Assets\\Plugins hard");
        Console.WriteLine();
        Console.WriteLine("  # 构建并复制 DLL");
        Console.WriteLine("  LinkSourceData pack D:\\Arch\\src\\Arch\\Arch.csproj D:\\Unity\\Assets\\Plugins");
        Console.WriteLine("  LinkSourceData pack D:\\Arch\\Arch.sln D:\\Unity\\Assets\\Plugins net6.0");
    }
}
