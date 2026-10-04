using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;

namespace MulLangCSharp.Language;

/// <summary>
/// ctdll 檔：每個參考組件（dll）自動產生的「自然對人工語言轉換」對照表。
/// 檔名為「組件名稱.ctdll」，UTF-8 文字，每行「英文名稱 Tab 中文名稱 Tab 種類」，可手動修改；
/// 已存在的檔案不會被覆寫（除非選擇重新產生）。
/// </summary>
public static class CtdllTables
{
    public const string Extension = ".ctdll";
    private const string IndexFileName = "_已處理組件.txt";

    /// <summary>載入順序：越前面優先權越高（同一中文名稱只保留第一個）。</summary>
    private static readonly string[] Priority =
    {
        "System.Private.CoreLib", "System.Console", "System.Runtime", "System.Collections", "System.Linq",
        "System.Text.RegularExpressions", "System.IO", "System.Threading", "System.Threading.Tasks",
    };

    private static readonly Regex WordRegex = new(@"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+", RegexOptions.Compiled);

    private static string? _directory;

    /// <summary>ctdll 資料夾：編輯器執行檔旁的 ctdll\；無法寫入時改用 %LOCALAPPDATA%\MulLangCSharp\ctdll。</summary>
    public static string Directory => _directory ??= ResolveDirectory();

    private static string ResolveDirectory()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ctdll");
        try
        {
            System.IO.Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return local;
        }
        catch (Exception)
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MulLangCSharp", "ctdll");
            System.IO.Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    // ───────────────────────── 產生 ─────────────────────────

    /// <summary>
    /// 為系統參考組件產生 ctdll 檔：update = false 時只處理還沒處理過的組件（預先做好的檔案會直接沿用）；
    /// update = true 時重新掃描全部組件並補上新名稱，已存在的中文名稱（含手動修改）一律保留。
    /// 傳回寫入的檔案數。
    /// </summary>
    public static int Generate(IReadOnlyList<MetadataReference> references, bool update, IProgress<string>? progress = null)
    {
        var dir = Directory;
        var indexPath = Path.Combine(dir, IndexFileName);
        var processed = !update && File.Exists(indexPath)
            ? File.ReadAllLines(indexPath, Encoding.UTF8).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var compilation = CSharpCompilation.Create("CtdllScan", references: references);
        int written = 0;
        foreach (var reference in references)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) continue;
            var id = $"{asm.Identity.Name} {asm.Identity.Version}";
            if (processed.Contains(id)) continue;

            var path = Path.Combine(dir, asm.Identity.Name + Extension);
            if (update || !File.Exists(path))
            {
                var entries = CollectNames(asm, ReadExisting(path));
                if (entries.Count > 0)
                {
                    progress?.Report($"產生 {asm.Identity.Name}{Extension}（{entries.Count} 筆）");
                    WriteFile(path, asm, entries);
                    written++;
                }
            }
            processed.Add(id);
        }
        File.WriteAllLines(indexPath, processed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), new UTF8Encoding(true));
        _systemCache = null;
        return written;
    }

    // ───────────────────────── 使用者 dll ─────────────────────────

    /// <summary>使用者 dll 對應的 ctdll 位置：優先放在 dll 旁（MyLib.dll → MyLib.ctdll），無法寫入時放在 ctdll 資料夾。</summary>
    public static string? FindUserCtdll(string dllPath)
    {
        var beside = Path.ChangeExtension(dllPath, Extension);
        if (File.Exists(beside)) return beside;
        var central = Path.Combine(Directory, Path.GetFileNameWithoutExtension(dllPath) + Extension);
        return File.Exists(central) ? central : null;
    }

    /// <summary>
    /// 為使用者 dll 產生（或在 dll 更新後補充）ctdll 檔；已存在的中文名稱保留。傳回 ctdll 路徑（無可轉換名稱時為 null）。
    /// </summary>
    public static string? EnsureUserCtdll(string dllPath, IReadOnlyList<MetadataReference> systemReferences)
    {
        var existing = FindUserCtdll(dllPath);
        if (existing is not null && File.GetLastWriteTimeUtc(existing) >= File.GetLastWriteTimeUtc(dllPath))
            return existing;

        var reference = MetadataReference.CreateFromFile(dllPath);
        var compilation = CSharpCompilation.Create("CtdllUserScan", references: systemReferences.Append(reference));
        if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) return existing;

        var entries = CollectNames(asm, existing is null ? new() : ReadExisting(existing));
        if (entries.Count == 0) return existing;

        var target = existing ?? Path.ChangeExtension(dllPath, Extension);
        try
        {
            WriteFile(target, asm, entries);
        }
        catch (Exception) when (existing is null)
        {
            target = Path.Combine(Directory, asm.Identity.Name + Extension);
            WriteFile(target, asm, entries);
        }
        return target;
    }

    private sealed record NameEntry(string English, string Chinese, string Kind, int Order);

    /// <summary>讀取既有 ctdll 的「英文 → 中文」，重新產生時保留（包含使用者手動修改的名稱）。</summary>
    private static Dictionary<string, string> ReadExisting(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return d;
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            if (raw.Length == 0 || raw[0] == '#') continue;
            var cols = raw.Split('\t');
            if (cols.Length >= 2 && cols[0].Trim().Length > 0 && cols[1].Trim().Length > 0)
                d.TryAdd(cols[0].Trim(), cols[1].Trim());
        }
        return d;
    }

    private static readonly Regex DigitKey = new(@"^D(\d)$", RegexOptions.Compiled);
    private static readonly Regex FunctionKey = new(@"^F(\d{1,2})$", RegexOptions.Compiled);
    private static readonly Regex NumPadKey = new(@"^NumPad(\d)$", RegexOptions.Compiled);

    /// <summary>按鍵列舉（名稱以 Key／Keys 結尾，如 ConsoleKey、WinForms 的 Keys）的成員：W → W鍵、D1 → 數字1鍵、F5 → F5鍵、NumPad0 → 數字鍵盤0鍵。</summary>
    private static string? KeyEnumName(string member)
    {
        if (member.Length == 1 && char.IsAsciiLetterUpper(member[0])) return member + "鍵";
        if (DigitKey.Match(member) is { Success: true } d) return $"數字{d.Groups[1].Value}鍵";
        if (FunctionKey.Match(member) is { Success: true } f) return $"F{f.Groups[1].Value}鍵";
        if (NumPadKey.Match(member) is { Success: true } n) return $"數字鍵盤{n.Groups[1].Value}鍵";
        return null;
    }

    /// <summary>收集組件中所有公開的命名空間、型別、成員名稱，並嘗試轉成中文。existing 中已有的名稱優先沿用。</summary>
    private static List<NameEntry> CollectNames(IAssemblySymbol asm, Dictionary<string, string> existing)
    {
        var map = new Dictionary<string, NameEntry>(StringComparer.Ordinal);
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string name, string kind, int order, bool isInterface = false, string? special = null)
        {
            frequency[name] = frequency.GetValueOrDefault(name) + 1;
            if (map.TryGetValue(name, out var seen) && seen.Order <= order) return;
            if (name.Length == 0 || !SyntaxFacts.IsValidIdentifier(name)) return;
            string? zh = existing.TryGetValue(name, out var kept) ? kept
                : KeywordDictionary.CSharpToChinese.TryGetValue(name, out var builtIn) ? builtIn
                : special ?? TranslateName(name, isInterface);
            if (zh is not null && KeywordDictionary.IsUsableChineseName(zh))
                map[name] = new NameEntry(name, zh, kind, order);
        }

        void WalkType(INamedTypeSymbol type)
        {
            if (type.DeclaredAccessibility != RoslynAccessibility.Public) return;
            Add(type.Name, KindOf(type), 1, type.TypeKind == TypeKind.Interface);
            foreach (var m in type.GetMembers())
            {
                if (m.DeclaredAccessibility is not (RoslynAccessibility.Public or RoslynAccessibility.Protected or RoslynAccessibility.ProtectedOrInternal))
                    continue;
                if (m.IsImplicitlyDeclared || !m.CanBeReferencedByName) continue;
                switch (m)
                {
                    case INamedTypeSymbol nested: WalkType(nested); break;
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary }: Add(m.Name, "方法", 2); break;
                    case IPropertySymbol: Add(m.Name, "屬性", 2); break;
                    case IFieldSymbol f when type.TypeKind == TypeKind.Enum:
                        Add(m.Name, "列舉值", 3, special: type.Name.EndsWith("Key", StringComparison.Ordinal) || type.Name.EndsWith("Keys", StringComparison.Ordinal) ? KeyEnumName(m.Name) : null);
                        break;
                    case IFieldSymbol f: Add(m.Name, f.IsConst ? "常數" : "欄位", 2); break;
                    case IEventSymbol: Add(m.Name, "事件", 2); break;
                }
            }
        }

        void WalkNamespace(INamespaceSymbol ns)
        {
            if (!ns.IsGlobalNamespace) Add(ns.Name, "命名空間", 0);
            foreach (var t in ns.GetTypeMembers()) WalkType(t);
            foreach (var child in ns.GetNamespaceMembers()) WalkNamespace(child);
        }

        WalkNamespace(asm.GlobalNamespace);

        // 檔案中手動加入、但不是組件符號名稱的項目（例如特性簡稱 STAThread）也保留。
        foreach (var (english, chinese) in existing)
            if (!map.ContainsKey(english) && KeywordDictionary.IsUsableChineseName(chinese))
            {
                frequency[english] = 0;
                map[english] = new NameEntry(english, chinese, "自訂", 4);
            }

        // 同一組件內中文名稱衝突時：型別 > 成員 > 列舉值，其次出現次數多者優先（載入時先到先得）。
        return map.Values.OrderBy(e => e.Order).ThenByDescending(e => frequency[e.English]).ThenBy(e => e.English, StringComparer.Ordinal).ToList();
    }

    private static string KindOf(INamedTypeSymbol t) => t.TypeKind switch
    {
        TypeKind.Interface => "介面",
        TypeKind.Struct => "結構",
        TypeKind.Enum => "列舉",
        TypeKind.Delegate => "委派",
        _ => "類別",
    };

    /// <summary>
    /// 依大小寫把名稱拆成單字逐字翻譯：SetCursorPosition → 設定游標位置。
    /// 介面名稱去掉開頭的 I 並加上「介面」：IDisposable → 可處置介面。任何單字查不到就傳回 null。
    /// </summary>
    public static string? TranslateName(string name, bool isInterface = false)
    {
        string suffix = "";
        if (isInterface && name.Length > 2 && name[0] == 'I' && char.IsUpper(name[1]))
        {
            name = name[1..];
            suffix = "介面";
        }

        var sb = new StringBuilder();
        foreach (var part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = WordRegex.Matches(part);
            if (matches.Sum(m => m.Length) != part.Length) return null; // 有無法拆解的字元
            foreach (Match m in matches)
            {
                if (char.IsDigit(m.Value[0])) sb.Append(m.Value);
                else if (WordGlossary.Words.TryGetValue(m.Value, out var zh)) sb.Append(zh);
                else return null;
            }
        }
        return sb.Length == 0 ? null : sb.Append(suffix).ToString();
    }

    private static void WriteFile(string path, IAssemblySymbol asm, List<NameEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# ctdll：{asm.Identity.Name} 的自然對人工語言轉換表（自動產生，可手動修改）");
        sb.AppendLine($"# 組件：{asm.Identity}");
        sb.AppendLine("# 格式：英文名稱<Tab>中文名稱<Tab>種類；以 # 開頭為註解。修改中文名稱或刪除整行即可調整轉換。");
        sb.AppendLine("# 內建關鍵字字典優先；中文名稱不可含「的」「被指派」「加上」「減掉」，否則該行會被略過。");
        foreach (var e in entries)
            sb.Append(e.English).Append('\t').Append(e.Chinese).Append('\t').Append(e.Kind).AppendLine();
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    // ───────────────────────── 載入 ─────────────────────────

    private static List<WordEntry>? _systemCache;

    /// <summary>系統組件的 ctdll（ctdll 資料夾中的所有檔案，依優先順序）；結果會快取。</summary>
    public static List<WordEntry> LoadSystem()
    {
        if (_systemCache is { } cached) return cached;
        var files = System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "*" + Extension)
            : Array.Empty<string>();

        int Rank(string file)
        {
            int i = Array.IndexOf(Priority, Path.GetFileNameWithoutExtension(file));
            return i < 0 ? Priority.Length : i;
        }

        return _systemCache = LoadFiles(files.OrderBy(Rank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>清除系統 ctdll 快取（手動修改檔案後重新載入用）。</summary>
    public static void InvalidateCache() => _systemCache = null;

    /// <summary>讀取指定的 ctdll 檔，傳回轉換項目。</summary>
    public static List<WordEntry> LoadFiles(IEnumerable<string> files)
    {
        var result = new List<WordEntry>();
        foreach (var file in files)
        {
            var asmName = Path.GetFileNameWithoutExtension(file);
            foreach (var raw in File.ReadLines(file, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                var cols = raw.Split('\t');
                if (cols.Length < 2) continue;
                var english = cols[0].Trim();
                var chinese = cols[1].Trim();
                if (english.Length == 0 || chinese.Length == 0) continue;
                var kind = cols.Length > 2 ? cols[2].Trim() : "";
                result.Add(new WordEntry(chinese, english, WordCategory.函式庫, $"ctdll：{asmName}（{kind}）"));
            }
        }
        return result;
    }

    /// <summary>設定 ctdll 資料夾（預先產生系統 ctdll 檔到專案資料夾時使用）。</summary>
    public static void UseDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        _directory = path;
        _systemCache = null;
    }
}