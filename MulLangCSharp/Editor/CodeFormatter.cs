using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using MulLangCSharp.Build;
using MulLangCSharp.Language;

namespace MulLangCSharp.Editor;

/// <summary>對中文原始碼要做的一處空白變更（位置以中文原始碼為準）。</summary>
public sealed record SourceEdit(int Start, int Length, string NewText);

/// <summary>
/// 美化文字格式（Ctrl+E、D）：先轉成 C#，由 Roslyn 計算格式化需要的「空白」變更，
/// 再經位置對照表套回中文原始碼。只調整縮排、空白、換行，不改寫任何文字。
/// </summary>
public static class CodeFormatter
{
    private static readonly Lazy<AdhocWorkspace> Workspace = new(() => new AdhocWorkspace());

    public static IReadOnlyList<SourceEdit> ComputeEdits(string source, string newLine = "\r\n", int indentSize = 4)
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
            .WithChangedOption(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions.WrappingKeepStatementsOnSingleLine, false)
            .WithChangedOption(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions.WrappingPreserveSingleLine, false);

        // 自動屬性（所有存取子都沒有本體）的 { 取得; 設定; }：從屬性名稱之後到右大括號，不做任何變更。
        var keep = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AccessorListSyntax>()
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

            // 黏著寫的運算子（c被指派0、非暫停中、甲加一）：插入點兩側都是識別字字元，代表原本就刻意不留空白，保持原樣。
            if (original.Length == 0 && change.NewText is { Length: > 0 } inserted && inserted.All(ch => ch is ' ' or '\t')
                && start > 0 && start < source.Length
                && Translator.IsIdentPart(source[start - 1]) && Translator.IsIdentPart(source[start]))
                continue;
            if (!string.IsNullOrWhiteSpace(original) && original.Length > 0) continue; // 安全起見：只替換空白
            if (original != change.NewText)
                edits.Add(new SourceEdit(start, end - start, change.NewText ?? ""));
        }
        return edits;
    }

    /// <summary>直接傳回格式化後的文字（測試或無編輯器時使用）。</summary>
    public static string Format(string source, string newLine = "\r\n")
    {
        var text = source;
        foreach (var e in ComputeEdits(source, newLine).OrderByDescending(e => e.Start))
            text = text.Remove(e.Start, e.Length).Insert(e.Start, e.NewText);
        return text;
    }
}
