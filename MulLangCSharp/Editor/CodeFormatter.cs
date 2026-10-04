using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using MulLangCSharp.Build;
using MulLangCSharp.Language;
using CSharpFormattingOptions = Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions;

namespace MulLangCSharp.Editor;

/// <summary>對中文原始碼要做的一處空白變更（位置以中文原始碼為準）。</summary>
public sealed record SourceEdit(int Start, int Length, string NewText);

/// <summary>
/// 美化文字格式（Ctrl+E、D）：先轉成 C#，由 Roslyn 計算縮排、空白、換行，再經位置對照表套回中文原始碼。
/// glue = true（預設）時，可黏著寫的運算子（被指派、加上、減掉、而且、或是、非）會去掉前後空白；
/// glue = false 時則一律用標準空白（c 被指派 0）。不論哪種模式都只改空白，不改寫任何文字。
/// </summary>
public static class CodeFormatter
{
    private static readonly Lazy<AdhocWorkspace> Workspace = new(() => new AdhocWorkspace());

    /// <summary>C# 運算子 → 可黏著寫的中文詞；prefixOnly 表示只黏後面（非）。</summary>
    private static readonly Dictionary<SyntaxKind, (string word, bool prefixOnly)> GlueOperators = new()
    {
        [SyntaxKind.EqualsToken] = ("被指派", false),
        [SyntaxKind.PlusEqualsToken] = ("加上", false),
        [SyntaxKind.MinusEqualsToken] = ("減掉", false),
        [SyntaxKind.AmpersandAmpersandToken] = ("而且", false),
        [SyntaxKind.BarBarToken] = ("或是", false),
        [SyntaxKind.ExclamationToken] = ("非", true),
        [SyntaxKind.SemicolonToken] = ("接著", false),
        [SyntaxKind.EqualsEqualsToken] = ("等於", false),
        [SyntaxKind.ExclamationEqualsToken] = ("不等於", false),
        [SyntaxKind.GreaterThanToken] = ("大於", false),
        [SyntaxKind.LessThanToken] = ("小於", false),
        [SyntaxKind.GreaterThanEqualsToken] = ("大於等於", false),
        [SyntaxKind.LessThanEqualsToken] = ("小於等於", false),
    };

    public static IReadOnlyList<SourceEdit> ComputeEdits(string source, string newLine = "\r\n", int indentSize = 4, bool glue = true) =>
        WhitespaceDiff(source, Format(source, newLine, indentSize, glue));

    /// <summary>傳回格式化後的文字。</summary>
    public static string Format(string source, string newLine = "\r\n", int indentSize = 4, bool glue = true)
    {
        var edits = RoslynEdits(source, newLine, indentSize);
        var spaced = Apply(source, edits);

        // 安全檢查：格式化後轉換出的 C# 語彙單元必須不變（例如「非 Foo()」若被去掉空白、而 Foo 不是已知名稱，
        // 就會變成名稱「非Foo」）；有問題時逐一套用，只保留不改變程式意義的變更。
        var expected = Tokens(Translator.ToCSharp(source).Code);
        if (!Same(expected, spaced))
        {
            spaced = source;
            foreach (var edit in edits.OrderByDescending(e => e.Start))
            {
                var trial = Apply(spaced, new[] { edit });
                if (Same(expected, trial)) spaced = trial;
            }
        }
        return glue ? Glue(spaced) : spaced;
    }

    // ───────────────────────── Roslyn 標準格式 ─────────────────────────

    private static List<SourceEdit> RoslynEdits(string source, string newLine, int indentSize)
    {
        var translation = Translator.ToCSharp(source);
        var root = CSharpSyntaxTree.ParseText(translation.Code, CodeCompiler.ParseOptions).GetRoot();

        var workspace = Workspace.Value;
        var options = workspace.Options
            .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, false)
            .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, indentSize)
            .WithChangedOption(FormattingOptions.TabSize, LanguageNames.CSharp, indentSize)
            .WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, newLine)
            // 美化：每個陳述式各佔一行，單行的區塊也展開（自動屬性 { 取得; 設定; } 另外保留為單行）。
            .WithChangedOption(CSharpFormattingOptions.WrappingKeepStatementsOnSingleLine, false)
            .WithChangedOption(CSharpFormattingOptions.WrappingPreserveSingleLine, false);

        // 自動屬性（所有存取子都沒有本體）的 { 取得; 設定; }：從屬性名稱之後到右大括號，不做任何變更。
        var keep = root.DescendantNodes().OfType<AccessorListSyntax>()
            .Where(l => l.Accessors.All(a => a.Body is null && a.ExpressionBody is null))
            .Select(l => TextSpan.FromBounds(l.OpenBraceToken.GetPreviousToken().Span.End, l.CloseBraceToken.Span.End))
            .ToList();

        var edits = new List<SourceEdit>();
        foreach (var change in Formatter.GetFormattedTextChanges(root, workspace, options))
        {
            if (keep.Any(k => k.Contains(change.Span))) continue;
            // 格式化只會改動語彙單元之間的空白；空白在轉換時逐字對應，因此位置可直接換算回原始碼。
            int start = translation.ToSourceOffset(change.Span.Start);
            int end = translation.ToSourceOffset(change.Span.End);
            if (end < start) continue;
            var original = source.Substring(start, end - start);
            if (original.Length > 0 && !string.IsNullOrWhiteSpace(original)) continue; // 安全起見：只替換空白
            if (original != change.NewText)
                edits.Add(new SourceEdit(start, end - start, change.NewText ?? ""));
        }
        return edits;
    }

    // ───────────────────────── 黏著寫 ─────────────────────────

    /// <summary>
    /// 去掉可黏著寫運算子前後的空白（不跨行）。每一處都先確認轉換後的 C# 語彙單元不變才套用，
    /// 例如「非 同步測試()」黏起來會變成名稱「非同步測試」，就保留空白。
    /// </summary>
    private static string Glue(string text)
    {
        var translation = Translator.ToCSharp(text);
        var expected = Tokens(translation.Code);
        var root = CSharpSyntaxTree.ParseText(translation.Code, CodeCompiler.ParseOptions).GetRoot();

        var candidates = new List<SourceEdit>();
        foreach (var token in root.DescendantTokens())
        {
            if (!GlueOperators.TryGetValue(token.Kind(), out var op)) continue;
            if (token.IsKind(SyntaxKind.ExclamationToken) && token.Parent is not PrefixUnaryExpressionSyntax) continue;
            int start = translation.ToSourceOffset(token.SpanStart);
            if (string.CompareOrdinal(text, start, op.word, 0, op.word.Length) != 0) continue; // 原文是符號（=、&&…）就不動

            if (!op.prefixOnly && SpacesBefore(text, start) is { } before) candidates.Add(before);
            if (SpacesAfter(text, start + op.word.Length) is { } after) candidates.Add(after);
        }
        if (candidates.Count == 0) return text;

        var all = Apply(text, candidates);
        if (Same(expected, all)) return all;

        // 有衝突時逐一嘗試，只保留不改變程式意義的部分（由後往前，位置才不會跑掉）。
        var current = text;
        foreach (var edit in candidates.OrderByDescending(e => e.Start))
        {
            var trial = Apply(current, new[] { edit });
            if (Same(expected, trial)) current = trial;
        }
        return current;
    }

    private static SourceEdit? SpacesBefore(string text, int pos)
    {
        int s = pos;
        while (s > 0 && text[s - 1] is ' ' or '\t' or '　') s--;
        // 行首的縮排不動（運算子在行首時）
        if (s == pos || s == 0 || text[s - 1] is '\n' or '\r') return null;
        return new SourceEdit(s, pos - s, "");
    }

    private static SourceEdit? SpacesAfter(string text, int pos)
    {
        int e = pos;
        while (e < text.Length && text[e] is ' ' or '\t' or '　') e++;
        if (e == pos || e >= text.Length || text[e] is '\n' or '\r') return null;
        return new SourceEdit(pos, e - pos, "");
    }

    private static bool Same(List<(SyntaxKind, string)> expected, string candidate) =>
        expected.SequenceEqual(Tokens(Translator.ToCSharp(candidate).Code));

    private static List<(SyntaxKind, string)> Tokens(string code) =>
        SyntaxFactory.ParseTokens(code).Select(t => (t.Kind(), t.Text)).ToList();

    // ───────────────────────── 工具 ─────────────────────────

    private static string Apply(string text, IEnumerable<SourceEdit> edits)
    {
        var sb = new System.Text.StringBuilder(text);
        foreach (var e in edits.OrderByDescending(e => e.Start))
            sb.Remove(e.Start, e.Length).Insert(e.Start, e.NewText);
        return sb.ToString();
    }

    /// <summary>
    /// 比對原文與目標文字（兩者只有空白不同），找出每一段需要替換的空白，
    /// 讓編輯器以最小變更套用（中斷點與游標位置不會亂掉）。
    /// </summary>
    private static List<SourceEdit> WhitespaceDiff(string source, string target)
    {
        var edits = new List<SourceEdit>();
        int i = 0, j = 0;
        while (i <= source.Length && j <= target.Length)
        {
            int i2 = i, j2 = j;
            while (i2 < source.Length && char.IsWhiteSpace(source[i2])) i2++;
            while (j2 < target.Length && char.IsWhiteSpace(target[j2])) j2++;
            if (string.CompareOrdinal(source, i, target, j, Math.Max(i2 - i, j2 - j)) != 0 || i2 - i != j2 - j)
                edits.Add(new SourceEdit(i, i2 - i, target.Substring(j, j2 - j)));
            if (i2 >= source.Length || j2 >= target.Length)
            {
                if (i2 < source.Length || j2 < target.Length) // 非空白字元不一致（理論上不會發生）：整份替換
                    return new List<SourceEdit> { new(0, source.Length, target) };
                break;
            }
            if (source[i2] != target[j2])
                return new List<SourceEdit> { new(0, source.Length, target) };
            i = i2 + 1;
            j = j2 + 1;
        }
        return edits;
    }
}
