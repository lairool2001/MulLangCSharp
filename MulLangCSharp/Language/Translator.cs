using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

namespace MulLangCSharp.Language;

/// <summary>原始碼中的 #參考 "路徑"（或 #r "路徑"）指示詞。</summary>
public sealed record ReferenceDirective(string Path, int Line, int SourceStart, int SourceLength);

public sealed class TranslationResult
{
    public TranslationResult(string code, int[] map, IReadOnlyList<ReferenceDirective>? references = null)
    {
        Code = code;
        Map = map;
        References = references ?? Array.Empty<ReferenceDirective>();
    }

    /// <summary>轉換後的 C# 程式碼。</summary>
    public string Code { get; }

    /// <summary>原始碼中以 #參考 指定的使用者組件（dll）。</summary>
    public IReadOnlyList<ReferenceDirective> References { get; }

    /// <summary>Map[轉換後位移] = 原始碼位移，長度為 Code.Length + 1。</summary>
    public int[] Map { get; }

    public int ToSourceOffset(int translatedOffset)
    {
        if (Map.Length == 0) return 0;
        return Map[Math.Clamp(translatedOffset, 0, Map.Length - 1)];
    }

    /// <summary>原始碼中「兩字元之間」的位置（如游標）→ 轉換後位置：第一個 Map ≥ 來源位置者。</summary>
    public int ToTranslatedCaret(int sourceOffset)
    {
        int lo = 0, hi = Map.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Map[mid] >= sourceOffset) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>原始碼中「某個字元」→ 轉換後對應字元：最後一個 Map ≤ 來源位置者（落在被替換詞的內部）。</summary>
    public int ToTranslatedChar(int sourceOffset)
    {
        int lo = 0, hi = Map.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Map[mid] <= sourceOffset) lo = mid; else hi = mid - 1;
        }
        return Math.Min(lo, Math.Max(0, Code.Length - 1));
    }
}

/// <summary>
/// 自然對人工語言轉換：以固定字典把中文 C# 轉為標準 C#（或反向）。
/// 字串、字元常值、註解內容一律原樣保留；插值字串的 {…} 內部會遞迴轉換。
/// 轉換永遠不增減換行，因此兩邊行號一一對應。
/// </summary>
public static class Translator
{
    public static TranslationResult ToCSharp(string source) => AutoSemicolons(new ForwardScanner(source).Run());

    private static readonly Microsoft.CodeAnalysis.CSharp.CSharpParseOptions SemicolonParseOptions =
        new(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);

    /// <summary>
    /// 行尾的「;」可以省略：用 Roslyn 解析轉換後的 C#，凡是缺少「;」且語句在此結束
    /// （下一個語彙單元在下一行、是「}」或檔案結尾）的位置，自動補上「;」。
    /// 補上的字元不含換行，因此行號不變；位置對照表一併更新。
    /// </summary>
    private static TranslationResult AutoSemicolons(TranslationResult r)
    {
        // 第一步：行尾單獨的「回傳」「拋出」先補「;」，否則 Roslyn 會把下一行當成回傳值（return 名稱 = …）。
        r = InsertSemicolons(r, (tree, text) => tree.GetRoot().DescendantTokens()
            .Where(t => t.RawKind is (int)SyntaxKind.ReturnKeyword or (int)SyntaxKind.ThrowKeyword)
            .Where(t => t.GetNextToken() is var next && next.RawKind != (int)SyntaxKind.SemicolonToken &&
                        (next.RawKind == (int)SyntaxKind.EndOfFileToken || LineOf(text, next.SpanStart) > LineOf(text, t.Span.End)))
            .Select(t => t.Span.End));

        // 第二步：其餘解析器認為缺少「;」、且語句在行尾（或「}」、檔案結尾）結束的位置。
        return InsertSemicolons(r, (tree, text) => tree.GetRoot().DescendantTokens()
            .Where(t => t.IsMissing && t.RawKind == (int)SyntaxKind.SemicolonToken)
            .Select(t => (prev: t.GetPreviousToken(), next: t.GetNextToken()))
            .Where(p => p.prev.RawKind != 0)
            .Where(p => p.next.RawKind is 0 or (int)SyntaxKind.EndOfFileToken or (int)SyntaxKind.CloseBraceToken
                        || LineOf(text, p.next.SpanStart) > LineOf(text, p.prev.Span.End))
            .Select(p => p.prev.Span.End));
    }

    private static int LineOf(Microsoft.CodeAnalysis.Text.SourceText text, int position) =>
        text.Lines.GetLineFromPosition(position).LineNumber;

    /// <summary>在 find 找到的位置插入「;」，並同步更新位置對照表。</summary>
    private static TranslationResult InsertSemicolons(TranslationResult r,
        Func<Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis.Text.SourceText, IEnumerable<int>> find)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(r.Code, SemicolonParseOptions);
        var positions = new SortedSet<int>(find(tree, tree.GetText()));
        if (positions.Count == 0) return r;

        var code = new StringBuilder(r.Code.Length + positions.Count);
        var map = new List<int>(r.Map.Length + positions.Count);
        for (int i = 0; i <= r.Code.Length; i++)
        {
            if (positions.Contains(i))
            {
                code.Append(';');
                map.Add(r.Map[i]);
            }
            if (i < r.Code.Length) code.Append(r.Code[i]);
            map.Add(r.Map[i]);
        }
        return new TranslationResult(code.ToString(), map.ToArray(), r.References);
    }

    public static string ToChinese(string csharp) => new ReverseScanner(csharp).Run();

    public static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);

    public static bool IsIdentPart(char c)
    {
        if (c == '_' || char.IsLetterOrDigit(c)) return true;
        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format;
    }

    /// <summary>共用的詞法掃描：辨識註解與各種字串，交由子類別處理識別字與其他字元。</summary>
    private abstract class ScannerBase
    {
        protected readonly string S;
        protected readonly StringBuilder Out = new();

        protected ScannerBase(string s) => S = s;

        protected char At(int i) => i < S.Length ? S[i] : '\0';

        protected virtual void Emit(string text, int srcPos) => Out.Append(text);

        protected virtual void Emit(char c, int srcPos) => Out.Append(c);

        protected void Copy(int start, int end)
        {
            for (int i = start; i < end && i < S.Length; i++) Emit(S[i], i);
        }

        protected abstract int OnIdentifier(int i);

        protected abstract int OnOther(int i, ref int depth, bool inHole, out bool stop);

        /// <summary>掃描程式碼直到結尾；若 inHole 則在插值孔的 '}' 停下（並輸出它）。</summary>
        protected int ScanCode(int i, bool inHole)
        {
            int depth = 0;
            while (i < S.Length)
            {
                char c = S[i];
                if (IsSlash(c) && IsSlash(At(i + 1)))
                {
                    i = LineComment(i, 2);
                }
                else if (IsSlash(c) && IsStar(At(i + 1)))
                {
                    i = BlockComment(i, 2);
                }
                else if (c == '"')
                {
                    i = At(i + 1) == '"' && At(i + 2) == '"' ? ScanRawString(i, i) : ScanRegularString(i, false, false);
                }
                else if (c == '＂')
                {
                    i = ScanRegularString(i, false, false);
                }
                else if (c == '@' && IsQuote(At(i + 1)))
                {
                    i = ScanRegularString(i, true, false);
                }
                else if (c == '@' && IsDollar(At(i + 1)) && IsQuote(At(i + 2)))
                {
                    i = ScanRegularString(i, true, true);
                }
                else if (c == '@' && IsIdentStart(At(i + 1)))
                {
                    // 逐字識別字：@名稱 保持原樣，不做任何轉換（包括「的」）。
                    int e = i + 1;
                    while (e < S.Length && IsIdentPart(S[e])) e++;
                    Copy(i, e);
                    i = e;
                }
                else if (IsDollar(c))
                {
                    int j = i;
                    while (IsDollar(At(j))) j++;
                    if (At(j) == '@' && IsQuote(At(j + 1))) i = ScanRegularString(i, true, true);
                    else if (At(j) == '"' && At(j + 1) == '"' && At(j + 2) == '"') i = ScanRawString(i, j);
                    else if (IsQuote(At(j))) i = ScanRegularString(i, false, true);
                    else { Emit(c == '＄' ? '$' : c, i); i++; }
                }
                else if (c == '\'')
                {
                    int e = i + 1;
                    while (e < S.Length && S[e] != '\'' && S[e] != '\n')
                        e += S[e] == '\\' ? 2 : 1;
                    e = Math.Min(e + 1, S.Length);
                    Copy(i, e);
                    i = e;
                }
                else if (char.IsDigit(c))
                {
                    int e = i;
                    // 數字常值只吃 ASCII 字元，因此「1加上甲」中的「加上甲」仍會被翻譯。
                    while (e < S.Length && (char.IsAsciiLetterOrDigit(S[e]) || S[e] == '_' || (S[e] == '.' && char.IsDigit(At(e + 1)))))
                        e++;
                    Copy(i, e);
                    i = e;
                }
                else if (IsIdentStart(c))
                {
                    i = OnIdentifier(i);
                }
                else
                {
                    i = OnOther(i, ref depth, inHole, out bool stop);
                    if (stop) return i;
                }
            }
            return i;
        }

        protected static bool IsSlash(char c) => c is '/' or '／';

        protected static bool IsStar(char c) => c is '*' or '＊';

        /// <summary>單行註解：開頭（//、／／ 或「註解」，長度 openLength）輸出為 //，其餘到行尾原樣保留。</summary>
        protected int LineComment(int i, int openLength)
        {
            int e = S.IndexOf('\n', i);
            if (e < 0) e = S.Length;
            if (e > i && S[e - 1] == '\r') e--;
            Emit("//", i);
            Copy(i + openLength, e);
            return e;
        }

        /// <summary>
        /// 區塊註解：開頭（/*、／＊ 或「開始註解」）輸出為 /*，內容原樣保留，
        /// 結尾可以是 */、＊／ 或「結束註解」，輸出為 */。
        /// </summary>
        protected int BlockComment(int i, int openLength)
        {
            Emit("/*", i);
            int from = i + openLength;
            int end = S.Length, endLength = 0;
            foreach (var closer in new[] { "*/", "＊／", "結束註解" })
            {
                int p = S.IndexOf(closer, from, StringComparison.Ordinal);
                if (p >= 0 && p < end) { end = p; endLength = closer.Length; }
            }
            Copy(from, end);
            if (endLength > 0) Emit("*/", end);
            return end + endLength;
        }

        protected static bool IsQuote(char c) => c is '"' or '＂';

        protected static bool IsDollar(char c) => c is '$' or '＄';

        /// <summary>
        /// 一般／逐字／插值字串。i 指向字串前綴的第一個字元。
        /// 全形與半形互通：＄、＂ 一律輸出為 $、"；字串以哪種引號開頭就以同一種引號結尾，
        /// 以 ＂ 開頭的字串中出現的半形 " 會自動跳脫。
        /// </summary>
        private int ScanRegularString(int i, bool verbatim, bool interpolated)
        {
            int q = i;
            while (!IsQuote(S[q])) q++;
            char close = S[q];
            for (int k = i; k <= q; k++)
                Emit(S[k] switch { '＄' => '$', '＂' => '"', var ch => ch }, k);
            i = q + 1;
            while (i < S.Length)
            {
                char c = S[i];
                if (!verbatim && c == '\\')
                {
                    if (At(i + 1) == '＂') { Emit("\\\"", i); i += 2; continue; }
                    Copy(i, i + 2); i += 2; continue;
                }
                if (c == close)
                {
                    if (verbatim && At(i + 1) == close) { Emit("\"\"", i); i += 2; continue; }
                    Emit('"', i);
                    return i + 1;
                }
                if (c == '"') // 以 ＂ 開頭的字串中的半形引號：當作內容，需跳脫
                {
                    Emit(verbatim ? "\"\"" : "\\\"", i);
                    i++;
                    continue;
                }
                if (!verbatim && c == '\n') return i; // 未結束的字串：交還給程式碼掃描
                if (interpolated && c == '{')
                {
                    if (At(i + 1) == '{') { Copy(i, i + 2); i += 2; continue; }
                    Emit(c, i);
                    i = ScanCode(i + 1, true);
                    continue;
                }
                Emit(c, i);
                i++;
            }
            return i;
        }

        /// <summary>原始字串 """…"""（含 $ 前綴）整段原樣保留。</summary>
        private int ScanRawString(int start, int quoteStart)
        {
            int n = 0;
            while (At(quoteStart + n) == '"') n++;
            string closer = new('"', n);
            int e = S.IndexOf(closer, quoteStart + n, StringComparison.Ordinal);
            e = e < 0 ? S.Length : e + n;
            while (At(e) == '"') e++;
            Copy(start, e);
            return e;
        }

        /// <summary>插值孔的格式字串部分（':' 之後到 '}'），原樣輸出。</summary>
        protected int CopyFormatSpecifier(int i)
        {
            while (i < S.Length && S[i] != '}' && S[i] != '"' && S[i] != '\n') { Emit(S[i], i); i++; }
            if (At(i) == '}') { Emit('}', i); i++; }
            return i;
        }
    }

    private sealed class ForwardScanner : ScannerBase
    {
        private readonly List<int> _map = new();
        private readonly List<ReferenceDirective> _references = new();

        private static readonly System.Text.RegularExpressions.Regex ReferenceRegex =
            new(@"\G[#＃]\s*(?:參考|r)\s+""([^""\r\n]+)""", System.Text.RegularExpressions.RegexOptions.Compiled);

        public ForwardScanner(string s) : base(s) { }

        private HashSet<string>? _names;

        /// <summary>名稱是否已知：在字典中，或在這份原始碼中單獨出現過（不含前置的「非」）。</summary>
        private bool IsKnownName(string name)
        {
            if (KeywordDictionary.ChineseToCSharp.ContainsKey(name)) return true;
            _names ??= CollectSourceNames();
            return _names.Contains(name);
        }

        /// <summary>收集原始碼中所有名稱片段：以「的」與可嵌入運算子切開，並去掉加一／減一。</summary>
        private HashSet<string> CollectSourceNames()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            int i = 0;
            while (i < S.Length)
            {
                if (!IsIdentStart(S[i])) { i++; continue; }
                int e = i;
                while (e < S.Length && IsIdentPart(S[e])) e++;
                foreach (var part in S[i..e].Split(KeywordDictionary.MemberAccessChar, KeywordDictionary.SpaceChar))
                    AddNameParts(part, set);
                i = e;
            }
            return set;
        }

        private static void AddNameParts(string part, HashSet<string> set)
        {
            if (part.Length == 0) return;
            foreach (var op in KeywordDictionary.EmbeddableOperators)
            {
                int p = part.IndexOf(op, StringComparison.Ordinal);
                if (p < 0) continue;
                AddNameParts(part[..p], set);
                AddNameParts(part[(p + op.Length)..], set);
                return;
            }
            foreach (var op in KeywordDictionary.AffixOperators)
            {
                if (part.Length > op.Length && part.StartsWith(op, StringComparison.Ordinal)) { AddNameParts(part[op.Length..], set); return; }
                if (part.Length > op.Length && part.EndsWith(op, StringComparison.Ordinal)) { AddNameParts(part[..^op.Length], set); return; }
            }
            set.Add(part);
        }

        /// <summary>
        /// 行首的 #參考 "路徑"（或 #r "路徑"）：記錄要參考的使用者 dll，並把整行轉成 // 註解（保持行號不變）。
        /// </summary>
        private bool TryReferenceDirective(int i, out int next)
        {
            next = i;
            int lineStart = i;
            while (lineStart > 0 && S[lineStart - 1] is ' ' or '\t') lineStart--;
            if (lineStart > 0 && S[lineStart - 1] != '\n') return false;

            var m = ReferenceRegex.Match(S, i);
            if (!m.Success) return false;
            int end = S.IndexOf('\n', i);
            if (end < 0) end = S.Length;
            if (end > i && S[end - 1] == '\r') end--;

            int line = 1;
            for (int k = 0; k < i; k++) if (S[k] == '\n') line++;
            _references.Add(new ReferenceDirective(m.Groups[1].Value.Trim(), line, i, end - i));

            Emit("//", i);
            Copy(i, end);
            next = end;
            return true;
        }

        public TranslationResult Run()
        {
            ScanCode(0, false);
            _map.Add(S.Length);
            return new TranslationResult(Out.ToString(), _map.ToArray(), _references);
        }

        protected override void Emit(string text, int srcPos)
        {
            Out.Append(text);
            for (int k = 0; k < text.Length; k++) _map.Add(srcPos);
        }

        protected override void Emit(char c, int srcPos)
        {
            Out.Append(c);
            _map.Add(srcPos);
        }

        protected override int OnIdentifier(int i)
        {
            int e = i;
            while (e < S.Length && IsIdentPart(S[e])) e++;

            // 「註解」（→ //）、「開始註解」（→ /*）、「結束註解」（→ */）：前面的部分照常轉換，之後交給註解處理。
            int cw = -1, cwLength = 0;
            foreach (var word in new[] { "開始註解", "結束註解", "註解" })
            {
                int p = S.IndexOf(word, i, e - i, StringComparison.Ordinal);
                if (p >= 0 && (cw < 0 || p < cw)) { cw = p; cwLength = word.Length; }
            }
            if (cw >= 0)
            {
                if (cw > i) TranslateRun(i, cw);
                return S.AsSpan(cw).StartsWith("開始註解") ? BlockComment(cw, 4)
                     : S.AsSpan(cw).StartsWith("結束註解") ? EmitAndSkip("*/", cw, 4)
                     : LineComment(cw, 2);
            }

            TranslateRun(i, e);
            return e;
        }

        private int EmitAndSkip(string text, int pos, int length)
        {
            Emit(text, pos);
            return pos + length;
        }

        /// <summary>翻譯一段識別字字元：以「的」（→ .）與「之」（→ 空白）切開，每段做完整詞比對。</summary>
        private void TranslateRun(int i, int e)
        {
            // 含有「的」「之」的運算子詞（如果的話就 → ?、委派之 → =>、繼承之 → :）要在切開前先認出來。
            int sp = -1;
            string? spWord = null;
            foreach (var word in KeywordDictionary.PreSplitOperators)
            {
                int p = S.IndexOf(word, i, e - i, StringComparison.Ordinal);
                if (p >= 0 && (sp < 0 || p < sp)) { sp = p; spWord = word; }
            }
            if (spWord is not null)
            {
                if (sp > i) TranslateRun(i, sp);
                Emit(KeywordDictionary.ChineseToCSharp[spWord], sp);
                if (sp + spWord.Length < e) TranslateRun(sp + spWord.Length, e);
                return;
            }

            int pieceStart = i;
            for (int k = i; k <= e; k++)
            {
                if (k == e || S[k] is KeywordDictionary.MemberAccessChar or KeywordDictionary.SpaceChar)
                {
                    if (k > pieceStart) TranslatePiece(pieceStart, k);
                    if (k < e) Emit(S[k] == KeywordDictionary.SpaceChar ? ' ' : '.', k);
                    pieceStart = k + 1;
                }
            }
        }

        /// <summary>
        /// 翻譯一段不含「的」的詞：先完整詞比對；否則依序拆出嵌入的運算子（被指派、加上、減掉）、
        /// 前置的加一／減一、後置的加一／減一；都不符合就原樣保留。
        /// </summary>
        private void TranslatePiece(int start, int end)
        {
            if (end <= start) return;
            string piece = S.Substring(start, end - start);
            if (KeywordDictionary.ChineseToCSharp.TryGetValue(piece, out var mapped))
            {
                Emit(mapped, start);
                return;
            }

            int bestPos = -1;
            string? bestOp = null;
            foreach (var op in KeywordDictionary.EmbeddableOperators)
            {
                int p = piece.IndexOf(op, StringComparison.Ordinal);
                if (p >= 0 && (bestPos < 0 || p < bestPos || (p == bestPos && op.Length > bestOp!.Length)))
                {
                    bestPos = p;
                    bestOp = op;
                }
            }
            if (bestOp is not null)
            {
                TranslatePiece(start, start + bestPos);
                Emit(KeywordDictionary.ChineseToCSharp[bestOp], start + bestPos);
                TranslatePiece(start + bestPos + bestOp.Length, end);
                return;
            }

            foreach (var op in KeywordDictionary.AffixOperators)
            {
                if (piece.Length > op.Length && piece.StartsWith(op, StringComparison.Ordinal))
                {
                    Emit(KeywordDictionary.ChineseToCSharp[op], start);
                    TranslatePiece(start + op.Length, end);
                    return;
                }
                if (piece.Length > op.Length && piece.EndsWith(op, StringComparison.Ordinal))
                {
                    TranslatePiece(start, end - op.Length);
                    Emit(KeywordDictionary.ChineseToCSharp[op], end - op.Length);
                    return;
                }
            }

            foreach (var op in KeywordDictionary.PrefixOperators)
            {
                // 只有「非」後面那段本身是已知名稱（檔案中單獨出現過，或在字典中）才拆開：
                // 非暫停中 → !暫停中；但方法「非同步測試」不會被拆成 !同步測試。
                if (piece.Length > op.Length && piece.StartsWith(op, StringComparison.Ordinal) && IsKnownName(piece[op.Length..]))
                {
                    Emit(KeywordDictionary.ChineseToCSharp[op], start);
                    TranslatePiece(start + op.Length, end);
                    return;
                }
            }

            Copy(start, end);
        }

        protected override int OnOther(int i, ref int depth, bool inHole, out bool stop)
        {
            stop = false;
            if (!inHole && S[i] is '#' or '＃' && TryReferenceDirective(i, out int afterDirective))
                return afterDirective;

            char c = S[i];
            if (KeywordDictionary.FullWidthPunctuation.TryGetValue(c, out var half)) c = half;

            if (inHole)
            {
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == '}')
                {
                    if (depth == 0) { Emit('}', i); stop = true; return i + 1; }
                    depth--;
                }
                else if (c == ':' && depth == 0 && At(i + 1) != ':')
                {
                    Emit(':', i);
                    stop = true;
                    return CopyFormatSpecifier(i + 1);
                }
            }
            Emit(c, i);
            return i + 1;
        }
    }

    private sealed class ReverseScanner : ScannerBase
    {
        /// <param name="Padded">true：前後補空白（完整詞比對的運算子）；false：直接黏著輸出（可嵌入的運算子）。</param>
        private readonly record struct OperatorAt(int Length, string Zh, bool Padded);

        private readonly Dictionary<int, OperatorAt> _operators;

        public ReverseScanner(string s) : base(s) => _operators = FindOperators(s);

        /// <summary>
        /// 用 Roslyn 找出真正的運算子位置；純詞法無法分辨 a &lt; b 與 清單&lt;整數&gt;、!x 與 x!。
        /// </summary>
        private static Dictionary<int, OperatorAt> FindOperators(string code)
        {
            var result = new Dictionary<int, OperatorAt>();
            var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code).GetRoot();
            foreach (var token in root.DescendantTokens())
            {
                var parent = token.Parent;
                bool padded;
                switch (token.Text)
                {
                    // 比較、&& ||：二元運算式或關係模式（是 大於 3）；可黏著寫，保留來源原本的空白。
                    case "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||"
                        when parent is Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax
                                    or Microsoft.CodeAnalysis.CSharp.Syntax.RelationalPatternSyntax:
                        padded = false;
                        break;
                    // 邏輯非 !x（不含 null 容許的 x!）；輸出「非 x」（加空白，任何名稱都能正確轉回）。
                    case "!" when parent is Microsoft.CodeAnalysis.CSharp.Syntax.PrefixUnaryExpressionSyntax:
                        padded = true;
                        break;
                    // += -=：可嵌入，原樣保留來源的空白。
                    case "+=" or "-=" when parent is Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax:
                        padded = false;
                        break;
                    // ++ --：直接黏在運算元前後（加一c、c加一）。
                    case "++" or "--" when parent is Microsoft.CodeAnalysis.CSharp.Syntax.PrefixUnaryExpressionSyntax
                                                  or Microsoft.CodeAnalysis.CSharp.Syntax.PostfixUnaryExpressionSyntax:
                        padded = false;
                        break;
                    default:
                        continue;
                }
                var w = KeywordDictionary.Entries.First(e => e.Category == WordCategory.運算子 && e.CSharp == token.Text);
                result[token.SpanStart] = new OperatorAt(token.Span.Length, w.Chinese, padded);
            }
            return result;
        }

        /// <summary>輸出中文運算子詞，前後補空白以免黏到識別字。</summary>
        private int AppendOperatorWord(string word, int next)
        {
            if (Out.Length > 0 && !char.IsWhiteSpace(Out[^1])) Out.Append(' ');
            Out.Append(word);
            if (!char.IsWhiteSpace(At(next))) Out.Append(' ');
            return next;
        }

        public string Run()
        {
            ScanCode(0, false);
            return Out.ToString();
        }

        protected override int OnIdentifier(int i)
        {
            int e = i;
            while (e < S.Length && IsIdentPart(S[e])) e++;
            string word = S.Substring(i, e - i);
            if (KeywordDictionary.CSharpToChinese.TryGetValue(word, out var zh) && !IsOperatorWord(zh))
                Out.Append(zh);
            else
                Out.Append(word);
            return e;
        }

        private static bool IsOperatorWord(string zh) =>
            KeywordDictionary.Entries.Any(x => x.Chinese == zh && x.Category == WordCategory.運算子);

        protected override int OnOther(int i, ref int depth, bool inHole, out bool stop)
        {
            stop = false;
            char c = S[i];
            if (inHole)
            {
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == '}')
                {
                    if (depth == 0) { Out.Append('}'); stop = true; return i + 1; }
                    depth--;
                }
                else if (c == ':' && depth == 0 && At(i + 1) != ':')
                {
                    Out.Append(':');
                    stop = true;
                    return CopyFormatSpecifier(i + 1);
                }
            }

            // 由 Roslyn 判定的運算子（泛型的 < > 與 x! 不會被轉換）。
            if (_operators.TryGetValue(i, out var op))
            {
                if (op.Padded) return AppendOperatorWord(op.Zh, i + op.Length);
                Out.Append(op.Zh);
                return i + op.Length;
            }

            // 單獨的指派「=」→「被指派」（不含 == <= >= != => += ??= 等複合運算子）；可嵌入，不補空白。
            if (c == '=' && At(i + 1) is not ('=' or '>') && (i == 0 || "=!<>+-*/%&|^?".IndexOf(S[i - 1]) < 0))
            {
                Out.Append("被指派");
                return i + 1;
            }

            // 成員存取的「.」→「的」：前面是識別字、')'、']'、'>'，後面是識別字。
            if (c == '.' && Out.Length > 0 && IsIdentStart(At(i + 1)))
            {
                char prev = Out[^1];
                if (IsIdentPart(prev) || prev is ')' or ']' or '>' or '!')
                {
                    Out.Append(KeywordDictionary.MemberAccessChar);
                    return i + 1;
                }
            }
            Out.Append(c);
            return i + 1;
        }
    }
}
