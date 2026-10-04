using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using MulLangCSharp.Debugging;
using MulLangCSharp.Language;

namespace MulLangCSharp.Build;

public sealed record CompileDiagnostic(string Severity, string Id, string Message, int Line, int Column, int SourceStart, int SourceLength)
{
    public bool IsError => Severity == "錯誤";
}

public sealed class CompileResult
{
    public bool Success { get; init; }
    public string? AssemblyPath { get; init; }
    public bool Instrumented { get; init; }
    public List<CompileDiagnostic> Diagnostics { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>以 Roslyn 編譯轉換後的 C#，輸出 dll + runtimeconfig.json，可由 dotnet 主機執行。</summary>
public static class CodeCompiler
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    public static string CoreDirectory => Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    /// <summary>dotnet.exe 位置：shared/Microsoft.NETCore.App/x.y.z/ 往上三層。</summary>
    public static string DotnetHostPath
    {
        get
        {
            var root = Directory.GetParent(CoreDirectory)?.Parent?.Parent?.FullName;
            var exe = root is null ? null : Path.Combine(root, "dotnet.exe");
            return exe is not null && File.Exists(exe) ? exe : "dotnet";
        }
    }

    /// <summary>Microsoft.WindowsDesktop.App 共用架構資料夾（WinForms、WPF、System.Drawing.Common）。</summary>
    public static string DesktopDirectory => Path.GetDirectoryName(typeof(System.Windows.Application).Assembly.Location)!;

    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        // 參考 Microsoft.NETCore.App 與 Microsoft.WindowsDesktop.App 共用架構；
        // 實際用到桌面組件時，runtimeconfig 會改宣告 WindowsDesktop 架構（見 WriteRuntimeConfig）。
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var core = CoreDirectory;
        var desktop = DesktopDirectory;
        bool In(string p, string dir) => string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase);

        var coreFiles = tpa.Where(p => In(p, core)).ToList();
        var names = coreFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desktopFiles = tpa.Where(p => In(p, desktop) && !names.Contains(Path.GetFileName(p)));
        return coreFiles.Concat(desktopFiles)
                  .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                  .ToList();
    }

    /// <summary>編譯結果是否實際用到 WindowsDesktop 組件（WinForms／WPF／GDI+）。</summary>
    private static bool UsesDesktop(CSharpCompilation compilation)
    {
        var desktop = DesktopDirectory;
        return compilation.GetUsedAssemblyReferences().OfType<PortableExecutableReference>()
            .Any(r => r.FilePath is { } f && string.Equals(Path.GetDirectoryName(f), desktop, StringComparison.OrdinalIgnoreCase));
    }

    public static CSharpParseOptions ParseOptions { get; } = new(LanguageVersion.Latest);

    /// <summary>預先載入參考組件（背景執行以加快第一次編譯）。</summary>
    public static void Warmup() => _ = References.Value;

    /// <summary>編譯時參考的所有組件（產生 ctdll 轉換表時使用）。</summary>
    public static IReadOnlyList<MetadataReference> ReferenceList => References.Value;

    // ───────────────────────── 使用者組件（#參考） ─────────────────────────

    private static readonly Dictionary<string, (DateTime stamp, MetadataReference reference)> UserReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>#參考 的解析結果：找到的 dll 完整路徑與找不到的指示詞。</summary>
    public sealed record UserReferences(IReadOnlyList<string> Paths, IReadOnlyList<ReferenceDirective> Missing)
    {
        public static readonly UserReferences None = new(Array.Empty<string>(), Array.Empty<ReferenceDirective>());
    }

    /// <summary>把 #參考 "路徑" 解析成完整路徑；相對路徑以原始碼檔案所在資料夾為準。</summary>
    public static UserReferences ResolveUserReferences(TranslationResult translation, string? baseDirectory)
    {
        if (translation.References.Count == 0) return UserReferences.None;
        var found = new List<string>();
        var missing = new List<ReferenceDirective>();
        foreach (var r in translation.References)
        {
            var path = Environment.ExpandEnvironmentVariables(r.Path);
            if (!Path.IsPathRooted(path))
                path = Path.Combine(baseDirectory ?? Environment.CurrentDirectory, path);
            path = Path.GetFullPath(path);
            if (File.Exists(path)) { if (!found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path); }
            else missing.Add(r);
        }
        return new UserReferences(found, missing);
    }

    /// <summary>取得使用者 dll 的 MetadataReference（依檔案修改時間快取，避免每次按鍵都重新載入）。</summary>
    public static MetadataReference GetUserReference(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (UserReferenceCache)
        {
            if (UserReferenceCache.TryGetValue(path, out var hit) && hit.stamp == stamp) return hit.reference;
            var reference = MetadataReference.CreateFromFile(path);
            UserReferenceCache[path] = (stamp, reference);
            return reference;
        }
    }

    private static IEnumerable<MetadataReference> AllReferences(UserReferences user) =>
        References.Value.Concat(user.Paths.Select(GetUserReference));

    private static IEnumerable<CompileDiagnostic> MissingReferenceDiagnostics(UserReferences user) =>
        user.Missing.Select(m => new CompileDiagnostic("錯誤", "參考", $"找不到參考的組件：{m.Path}", m.Line, 1, m.SourceStart, m.SourceLength));

    /// <summary>建立只供分析（錯誤檢查、IntelliSense）用的編譯。</summary>
    public static CSharpCompilation CreateAnalysisCompilation(SyntaxTree tree, UserReferences? user = null) =>
        CSharpCompilation.Create("Check", new[] { tree }, AllReferences(user ?? UserReferences.None),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication).WithAllowUnsafe(true)
                .WithNullableContextOptions(NullableContextOptions.Disable));

    /// <summary>只做語意分析（不輸出組件），供編輯時即時顯示錯誤。</summary>
    public static List<CompileDiagnostic> Analyze(string chineseSource, TranslationResult translation, string? baseDirectory, CancellationToken token)
    {
        var user = ResolveUserReferences(translation, baseDirectory);
        var tree = CSharpSyntaxTree.ParseText(translation.Code, ParseOptions, cancellationToken: token);
        var compilation = CreateAnalysisCompilation(tree, user);
        return MissingReferenceDiagnostics(user).Concat(compilation.GetDiagnostics(token)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .OrderByDescending(d => d.Severity).ThenBy(d => d.Location.SourceSpan.Start)
            .Select(d => ToDiagnostic(d, chineseSource, translation)))
            .ToList();
    }

    public static CompileResult Compile(string chineseSource, TranslationResult translation, string outputDir, string assemblyName, bool forDebug,
        string? baseDirectory = null)
    {
        var user = ResolveUserReferences(translation, baseDirectory);
        var tree = CSharpSyntaxTree.ParseText(translation.Code, ParseOptions, path: assemblyName + ".cs", encoding: Encoding.UTF8);
        var options = new CSharpCompilationOptions(OutputKind.ConsoleApplication)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithAllowUnsafe(true)
            .WithNullableContextOptions(NullableContextOptions.Disable)
            .WithConcurrentBuild(true);

        var compilation = CSharpCompilation.Create(assemblyName, new[] { tree }, AllReferences(user), options);
        var result = new CompileResult { Success = false };
        result.Diagnostics.AddRange(MissingReferenceDiagnostics(user));

        var diags = compilation.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();
        foreach (var d in diags.OrderByDescending(d => d.Severity).ThenBy(d => d.Location.SourceSpan.Start))
            result.Diagnostics.Add(ToDiagnostic(d, chineseSource, translation));

        if (result.Diagnostics.Any(d => d.IsError))
            return result;

        Directory.CreateDirectory(outputDir);
        var dllPath = Path.Combine(outputDir, assemblyName + ".dll");
        var instrumented = false;
        var toEmit = compilation;

        if (forDebug)
        {
            var dbg = Instrumenter.Instrument(compilation, tree, result.Notes);
            if (dbg is not null)
            {
                toEmit = dbg;
                instrumented = true;
            }
            else
            {
                result.Notes.Add("偵錯插樁失敗，改以一般模式執行（中斷點將不會生效）。");
            }
        }

        using (var pe = File.Create(dllPath))
        {
            var emit = toEmit.Emit(pe);
            if (!emit.Success)
            {
                foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    result.Diagnostics.Add(ToDiagnostic(d, chineseSource, translation));
                return result;
            }
        }

        bool desktop = UsesDesktop(compilation);
        WriteRuntimeConfig(Path.Combine(outputDir, assemblyName + ".runtimeconfig.json"), desktop);
        if (desktop) result.Notes.Add("使用桌面組件（WinForms／WPF／GDI+），以 Microsoft.WindowsDesktop.App 執行。");

        // 使用者 dll（與同名的 pdb、ctdll）複製到輸出資料夾，執行時才找得到。
        foreach (var dll in user.Paths)
        {
            var dir = Path.GetDirectoryName(dll)!;
            var stem = Path.GetFileNameWithoutExtension(dll);
            foreach (var file in Directory.GetFiles(dir, stem + ".*"))
                File.Copy(file, Path.Combine(outputDir, Path.GetFileName(file)), overwrite: true);
            result.Notes.Add("已參考使用者組件：" + dll);
        }
        return new CompileResult { Success = true, AssemblyPath = dllPath, Instrumented = instrumented }
            .WithDiagnostics(result);
    }

    private static CompileResult WithDiagnostics(this CompileResult target, CompileResult from)
    {
        target.Diagnostics.AddRange(from.Diagnostics);
        target.Notes.AddRange(from.Notes);
        return target;
    }

    private static void WriteRuntimeConfig(string path, bool desktop)
    {
        var v = Environment.Version;
        var framework = desktop ? "Microsoft.WindowsDesktop.App" : "Microsoft.NETCore.App";
        File.WriteAllText(path, $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{v.Major}}.{{v.Minor}}",
                "framework": { "name": "{{framework}}", "version": "{{v.Major}}.{{v.Minor}}.0" }
              }
            }
            """);
    }

    private static CompileDiagnostic ToDiagnostic(Diagnostic d, string source, TranslationResult translation)
    {
        var span = d.Location.IsInSource ? d.Location.SourceSpan : new TextSpan(0, 0);
        int srcStart = translation.ToSourceOffset(span.Start);
        int srcEnd = translation.ToSourceOffset(span.End);
        if (srcEnd <= srcStart) srcEnd = Math.Min(source.Length, srcStart + 1);

        var (line, col) = LineColumn(source, srcStart);
        string sev = d.Severity == DiagnosticSeverity.Error ? "錯誤" : "警告";
        return new CompileDiagnostic(sev, d.Id, d.GetMessage(System.Globalization.CultureInfo.GetCultureInfo("zh-TW")),
            line, col, srcStart, srcEnd - srcStart);
    }

    private static (int line, int col) LineColumn(string s, int offset)
    {
        int line = 1, col = 1;
        for (int i = 0; i < offset && i < s.Length; i++)
        {
            if (s[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }
}
