namespace MulLangCSharp.Language;

public enum WordCategory
{
    關鍵字,
    型別,
    常值,
    運算子,
    函式庫,
}

public sealed record WordEntry(string Chinese, string CSharp, WordCategory Category, string Note = "");

/// <summary>
/// 自然對人工語言轉換所用的固定字典映射。翻譯時以「完整詞」比對（識別字字元連續段落，並以「的」切開），
/// 唯一的例外是「的」：在程式碼中任何位置都直接轉換為「.」。
/// </summary>
public static class KeywordDictionary
{
    /// <summary>「的」→「.」，不受完整詞比對限制。</summary>
    public const char MemberAccessChar = '的';

    /// <summary>「之」→ 空白（可用來代替空白分隔，例如「整數之c」= 「整數 c」）。</summary>
    public const char SpaceChar = '之';

    public static readonly IReadOnlyList<WordEntry> Entries = new List<WordEntry>
    {
        // ── 關鍵字 ──
        new("使用", "using", WordCategory.關鍵字),
        new("命名空間", "namespace", WordCategory.關鍵字),
        new("類別", "class", WordCategory.關鍵字),
        new("結構", "struct", WordCategory.關鍵字),
        new("介面", "interface", WordCategory.關鍵字),
        new("列舉", "enum", WordCategory.關鍵字),
        new("記錄", "record", WordCategory.關鍵字),
        new("委派", "delegate", WordCategory.關鍵字),
        new("事件", "event", WordCategory.關鍵字),
        new("公開", "public", WordCategory.關鍵字),
        new("私有", "private", WordCategory.關鍵字),
        new("保護", "protected", WordCategory.關鍵字),
        new("內部", "internal", WordCategory.關鍵字),
        new("靜態", "static", WordCategory.關鍵字),
        new("唯讀", "readonly", WordCategory.關鍵字),
        new("常數", "const", WordCategory.關鍵字),
        new("虛擬", "virtual", WordCategory.關鍵字),
        new("覆寫", "override", WordCategory.關鍵字),
        new("抽象", "abstract", WordCategory.關鍵字),
        new("密封", "sealed", WordCategory.關鍵字),
        new("局部", "partial", WordCategory.關鍵字),
        new("外部", "extern", WordCategory.關鍵字),
        new("不安全", "unsafe", WordCategory.關鍵字),
        new("易變", "volatile", WordCategory.關鍵字),
        new("必要", "required", WordCategory.關鍵字),
        new("初始", "init", WordCategory.關鍵字),
        new("全域", "global", WordCategory.關鍵字),
        new("新", "new", WordCategory.關鍵字),
        new("回傳", "return", WordCategory.關鍵字),
        new("如果", "if", WordCategory.關鍵字),
        new("否則", "else", WordCategory.關鍵字),
        new("當", "while", WordCategory.關鍵字),
        new("做", "do", WordCategory.關鍵字),
        new("對於", "for", WordCategory.關鍵字),
        new("對每個", "foreach", WordCategory.關鍵字),
        new("在", "in", WordCategory.關鍵字),
        new("切換", "switch", WordCategory.關鍵字),
        new("情況", "case", WordCategory.關鍵字),
        new("預設", "default", WordCategory.關鍵字),
        new("當符合", "when", WordCategory.關鍵字),
        new("中斷", "break", WordCategory.關鍵字),
        new("繼續", "continue", WordCategory.關鍵字),
        new("轉到", "goto", WordCategory.關鍵字),
        new("嘗試", "try", WordCategory.關鍵字),
        new("捕捉", "catch", WordCategory.關鍵字),
        new("最終", "finally", WordCategory.關鍵字),
        new("拋出", "throw", WordCategory.關鍵字),
        new("鎖定", "lock", WordCategory.關鍵字),
        new("固定", "fixed", WordCategory.關鍵字),
        new("檢查", "checked", WordCategory.關鍵字),
        new("不檢查", "unchecked", WordCategory.關鍵字),
        new("堆疊配置", "stackalloc", WordCategory.關鍵字),
        new("這個", "this", WordCategory.關鍵字),
        new("基底", "base", WordCategory.關鍵字),
        new("是", "is", WordCategory.關鍵字),
        new("作為", "as", WordCategory.關鍵字),
        new("取型別", "typeof", WordCategory.關鍵字),
        new("取大小", "sizeof", WordCategory.關鍵字),
        new("取名稱", "nameof", WordCategory.關鍵字),
        new("參考", "ref", WordCategory.關鍵字),
        new("傳出", "out", WordCategory.關鍵字),
        new("可變參數", "params", WordCategory.關鍵字),
        new("非同步", "async", WordCategory.關鍵字),
        new("等待", "await", WordCategory.關鍵字),
        new("產生", "yield", WordCategory.關鍵字),
        new("運算子", "operator", WordCategory.關鍵字),
        new("隱含", "implicit", WordCategory.關鍵字),
        new("明確", "explicit", WordCategory.關鍵字),
        new("取得", "get", WordCategory.關鍵字),
        new("設定", "set", WordCategory.關鍵字),
        new("設定值", "value", WordCategory.關鍵字, "屬性 set 存取子中的隱含參數"),
        new("從", "from", WordCategory.關鍵字, "LINQ"),
        new("其中", "where", WordCategory.關鍵字, "LINQ / 泛型條件約束"),
        new("選取", "select", WordCategory.關鍵字, "LINQ"),
        new("排序依據", "orderby", WordCategory.關鍵字, "LINQ"),
        new("遞增", "ascending", WordCategory.關鍵字, "LINQ"),
        new("遞減", "descending", WordCategory.關鍵字, "LINQ"),
        new("群組", "group", WordCategory.關鍵字, "LINQ"),
        new("依", "by", WordCategory.關鍵字, "LINQ"),
        new("聯結", "join", WordCategory.關鍵字, "LINQ"),
        new("於", "on", WordCategory.關鍵字, "LINQ"),
        new("等值", "equals", WordCategory.關鍵字, "LINQ"),
        new("讓", "let", WordCategory.關鍵字, "LINQ"),
        new("進入", "into", WordCategory.關鍵字, "LINQ"),

        // ── 型別 ──
        new("虛無", "void", WordCategory.型別),
        new("變數", "var", WordCategory.型別),
        new("動態", "dynamic", WordCategory.型別),
        new("物件", "object", WordCategory.型別),
        new("字串", "string", WordCategory.型別),
        new("字元", "char", WordCategory.型別),
        new("布林", "bool", WordCategory.型別),
        new("整數", "int", WordCategory.型別),
        new("長整數", "long", WordCategory.型別),
        new("短整數", "short", WordCategory.型別),
        new("位元組", "byte", WordCategory.型別),
        new("有號位元組", "sbyte", WordCategory.型別),
        new("無號整數", "uint", WordCategory.型別),
        new("無號長整數", "ulong", WordCategory.型別),
        new("無號短整數", "ushort", WordCategory.型別),
        new("浮點數", "float", WordCategory.型別),
        new("雙精度", "double", WordCategory.型別),
        new("小數", "decimal", WordCategory.型別),

        // ── 常值 ──
        new("真", "true", WordCategory.常值),
        new("假", "false", WordCategory.常值),
        new("空", "null", WordCategory.常值),

        // ── 運算子 ──
        new("而且", "&&", WordCategory.運算子, "前後可不加空格"),
        new("或是", "||", WordCategory.運算子, "前後可不加空格"),
        new("非", "!", WordCategory.運算子, "可直接接在名稱前：非暫停中"),
        new("被指派", "=", WordCategory.運算子, "指派運算子（前後可不加空格）"),
        new("接著", ";", WordCategory.運算子, "陳述式分隔（前後可不加空格；行尾的 ; 本來就可省略）"),
        new("呼叫", "()", WordCategory.運算子, "空的括號，可黏著寫：主控台的讀行呼叫 = Console.ReadLine()"),
        new("委派之", "=>", WordCategory.運算子, "Lambda／運算式主體，可黏著寫：人委派之人的分數 = 人 => 人.分數"),
        new("繼承之", ":", WordCategory.運算子, "繼承／實作，可黏著寫：類別之甲繼承之表單 = class 甲 : Form"),
        new("如果的話就", "?", WordCategory.運算子, "條件運算子前半，可黏著寫：條件如果的話就甲或者是乙 = 條件 ? 甲 : 乙"),
        new("或者是", ":", WordCategory.運算子, "條件運算子後半（配合「如果的話就」）"),
        new("註解", "//", WordCategory.關鍵字, "單行註解：之後到行尾都是註解"),
        new("開始註解", "/*", WordCategory.關鍵字, "區塊註解開始（可跨行），以「結束註解」結束"),
        new("結束註解", "*/", WordCategory.關鍵字, "區塊註解結束"),
        new("加上", "+=", WordCategory.運算子, "前後可不加空格"),
        new("減掉", "-=", WordCategory.運算子, "前後可不加空格"),
        new("加一", "++", WordCategory.運算子, "可直接接在變數前後：加一c、c加一"),
        new("減一", "--", WordCategory.運算子, "可直接接在變數前後：減一c、c減一"),
        new("等於", "==", WordCategory.運算子),
        new("不等於", "!=", WordCategory.運算子),
        new("大於", ">", WordCategory.運算子),
        new("小於", "<", WordCategory.運算子),
        new("大於等於", ">=", WordCategory.運算子),
        new("小於等於", "<=", WordCategory.運算子),

        // ── 慣用名稱（使用者程式中的名稱，不屬於任何 dll）──
        // 其他函式庫名稱（主控台、寫行、黃色、W鍵…）一律由 ctdll 轉換表提供，不在程式中手工對照。
        new("程式", "Program", WordCategory.函式庫, "慣用的主類別名稱"),
        new("主程式", "Main", WordCategory.函式庫, "程式進入點"),
    };

    /// <summary>可嵌在識別字之間、前後不需空格的運算子（例如「x被指派1」）。</summary>
    public static readonly IReadOnlyList<string> EmbeddableOperators = new[] { "被指派", "加上", "減掉", "而且", "或是", "接著", "大於等於", "小於等於", "不等於", "大於", "小於", "等於", "呼叫", "或者是" };

    /// <summary>含有「的」「之」的運算子詞：轉換時要在以「的」「之」切開之前先認出來（也都可以黏著寫）。</summary>
    public static readonly IReadOnlyList<string> PreSplitOperators = new[] { "如果的話就", "委派之", "繼承之" };

    /// <summary>可直接黏在變數前（前置）或後（後置）的運算子（例如「加一c」「c加一」）。</summary>
    public static readonly IReadOnlyList<string> AffixOperators = new[] { "加一", "減一" };

    /// <summary>只能直接黏在名稱前面的運算子（例如「非暫停中」→「!暫停中」）。</summary>
    public static readonly IReadOnlyList<string> PrefixOperators = new[] { "非" };

    // 目前生效的對照表 = 內建字典 + ctdll 自動轉換表；整組替換以確保多執行緒讀取安全。
    private sealed record Tables(
        Dictionary<string, string> Forward,
        Dictionary<string, string> Reverse,
        HashSet<string> LibraryWords,
        IReadOnlyList<WordEntry> Generated);

    private static volatile Tables _tables = Build(Array.Empty<WordEntry>());

    /// <summary>中文 → C#（內建字典優先，其次為 ctdll 轉換表）。</summary>
    public static IReadOnlyDictionary<string, string> ChineseToCSharp => _tables.Forward;

    /// <summary>C# → 中文（內建字典優先，其次為 ctdll 轉換表）。</summary>
    public static IReadOnlyDictionary<string, string> CSharpToChinese => _tables.Reverse;

    /// <summary>目前生效的 ctdll 自動轉換項目。</summary>
    public static IReadOnlyList<WordEntry> GeneratedEntries => _tables.Generated;

    /// <summary>以函式庫顏色上色的中文詞（內建函式庫詞 + ctdll）。</summary>
    public static bool IsLibraryWord(string chinese) => _tables.LibraryWords.Contains(chinese);

    /// <summary>套用 ctdll 轉換表；不合法或與內建字典衝突的項目會被略過。傳回實際生效的筆數。</summary>
    public static int ApplyGenerated(IEnumerable<WordEntry> generated)
    {
        var t = Build(generated);
        _tables = t;
        return t.Generated.Count;
    }

    /// <summary>
    /// 中文名稱必須能被轉換器當成「單一詞」處理：是合法識別字、含非 ASCII 字元、
    /// 不含「的」與可嵌入運算子、頭尾不是加一／減一。
    /// </summary>
    public static bool IsUsableChineseName(string zh)
    {
        if (zh.Length == 0 || !Translator.IsIdentStart(zh[0]) || !zh.All(Translator.IsIdentPart)) return false;
        if (!zh.Any(c => c > 127) || zh.Contains(MemberAccessChar) || zh.Contains(SpaceChar) || zh.Contains("註解")) return false;
        if (EmbeddableOperators.Any(op => zh.Contains(op, StringComparison.Ordinal))) return false;
        if (AffixOperators.Any(op => zh != op && (zh.StartsWith(op, StringComparison.Ordinal) || zh.EndsWith(op, StringComparison.Ordinal))))
            return false;
        return true;
    }

    private static Tables Build(IEnumerable<WordEntry> generated)
    {
        var forward = new Dictionary<string, string>();
        var reverse = new Dictionary<string, string>();
        var library = new HashSet<string>();
        foreach (var e in Entries)
        {
            forward.TryAdd(e.Chinese, e.CSharp);
            // 單一字母（字母鍵 A…Z）不反向轉換，否則一般程式中的 X、Y 等識別字都會變成「X鍵」。
            if (!(e.CSharp.Length == 1 && char.IsLetter(e.CSharp[0]))) reverse.TryAdd(e.CSharp, e.Chinese);
            if (e.Category == WordCategory.函式庫) library.Add(e.Chinese);
        }

        var applied = new List<WordEntry>();
        foreach (var e in generated)
        {
            if (!IsUsableChineseName(e.Chinese) || forward.ContainsKey(e.Chinese)) continue;
            forward.Add(e.Chinese, e.CSharp);
            if (!(e.CSharp.Length == 1 && char.IsLetter(e.CSharp[0]))) reverse.TryAdd(e.CSharp, e.Chinese);
            library.Add(e.Chinese);
            applied.Add(e);
        }
        return new Tables(forward, reverse, library, applied);
    }

    /// <summary>全形標點 → 半形（僅在字串、字元、註解以外生效）。</summary>
    public static readonly IReadOnlyDictionary<char, char> FullWidthPunctuation = new Dictionary<char, char>
    {
        ['（'] = '(', ['）'] = ')', ['｛'] = '{', ['｝'] = '}', ['［'] = '[', ['］'] = ']',
        ['；'] = ';', ['，'] = ',', ['：'] = ':', ['？'] = '?', ['！'] = '!', ['＝'] = '=',
        ['＋'] = '+', ['－'] = '-', ['＊'] = '*', ['／'] = '/', ['％'] = '%', ['＜'] = '<',
        ['＞'] = '>', ['＆'] = '&', ['｜'] = '|', ['＾'] = '^', ['～'] = '~', ['＃'] = '#',
        ['　'] = ' ',
    };

}
