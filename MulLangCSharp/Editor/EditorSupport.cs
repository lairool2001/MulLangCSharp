using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using MulLangCSharp.Language;

namespace MulLangCSharp.Editor;

/// <summary>依字典動態產生中文 C# 的語法上色定義。</summary>
public static class ChineseHighlighting
{
    public static IHighlightingDefinition Create()
    {
        string Words(WordCategory c) => string.Join("|", KeywordDictionary.Entries
            .Where(e => e.Category == c)
            .Select(e => Regex.Escape(e.Chinese))
            .OrderByDescending(w => w.Length));

        // 詞的邊界：非識別字字元，或「的」「之」，或黏著寫的運算子（被指派、加上、加一、非…），
        // 因此「主控台的寫行」「使用之系統」「暫停中被指派假」中的每個詞都能各自上色。
        string glued = string.Join("|", KeywordDictionary.EmbeddableOperators.Concat(KeywordDictionary.PreSplitOperators)
            .Concat(KeywordDictionary.AffixOperators).Concat(KeywordDictionary.PrefixOperators)
            .OrderByDescending(w => w.Length).Select(Regex.Escape));
        string separators = $"[{KeywordDictionary.MemberAccessChar}{KeywordDictionary.SpaceChar}]|{glued}";
        string before = $@"(?<=^|[^\w]|{separators})";
        string after = $@"(?=$|[^\w]|{separators})";

        string Rule(string color, string words) => words.Length == 0 ? "" :
            $"<Rule color=\"{color}\">{SecurityElement.Escape($"{before}(?:{words}){after}")}</Rule>";

        string numberRule = SecurityElement.Escape(
            $@"{before}(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*(\.\d+)?([eE][+-]?\d+)?[fFdDmMuUlL]*)");

        var xshd = $$"""
            <SyntaxDefinition name="中文C#" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#008000" />
              <Color name="String" foreground="#A31515" />
              <Color name="Keyword" foreground="#0000FF" fontWeight="bold" />
              <Color name="Type" foreground="#2B91AF" fontWeight="bold" />
              <Color name="Literal" foreground="#0000FF" />
              <Color name="Operator" foreground="#AF00DB" fontWeight="bold" />
              <Color name="Library" foreground="#795E26" />
              <Color name="Number" foreground="#098658" />
              <Color name="Member" foreground="#C800C8" fontWeight="bold" />
              <Color name="Separator" foreground="#B0B0B0" />
              <Color name="Preprocessor" foreground="#808080" />
              <RuleSet>
                <Span color="Comment" multiline="true" begin="/\*|／＊|開始註解" end="\*/|＊／|結束註解" />
                <Span color="Comment" begin="//|／／|註解" />
                <Span color="Preprocessor" begin="^\s*\#" />
                <Span color="String" multiline="true">
                  <Begin>[\$＄]?@[\$＄]?"</Begin><End>"</End>
                  <RuleSet><Span begin='""' end="" /></RuleSet>
                </Span>
                <Span color="String">
                  <Begin>[\$＄]?"</Begin><End>"</End>
                  <RuleSet><Span begin="\\" end="." /></RuleSet>
                </Span>
                <Span color="String" multiline="true">
                  <Begin>[\$＄]?@[\$＄]?＂</Begin><End>＂</End>
                  <RuleSet><Span begin="＂＂" end="" /></RuleSet>
                </Span>
                <Span color="String">
                  <Begin>[\$＄]?＂</Begin><End>＂</End>
                  <RuleSet><Span begin="\\" end="." /></RuleSet>
                </Span>
                <Span color="String">
                  <Begin>'</Begin><End>'</End>
                  <RuleSet><Span begin="\\" end="." /></RuleSet>
                </Span>
                {{EmbeddedOperatorRules()}}
                {{Rule("Keyword", Words(WordCategory.關鍵字))}}
                {{Rule("Type", Words(WordCategory.型別))}}
                {{Rule("Literal", Words(WordCategory.常值))}}
                {{Rule("Operator", Words(WordCategory.運算子))}}
                <Rule color="Member">的</Rule>
                <Rule color="Separator">之</Rule>
                <Rule color="Number">{{numberRule}}</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        // 可嵌入的運算子（x被指派1）在任何位置都上色；加一／減一只在詞首或詞尾（加一c、c加一）上色。
        static string EmbeddedOperatorRules()
        {
            string embed = string.Join("|", KeywordDictionary.EmbeddableOperators.Concat(KeywordDictionary.PreSplitOperators).OrderByDescending(w => w.Length).Select(Regex.Escape));
            string affix = string.Join("|", KeywordDictionary.AffixOperators.Select(Regex.Escape));
            string affixRegex = $@"(?<![\w-[的之]])(?:{affix})|(?:{affix})(?![\w-[的之]])";
            string prefix = string.Join("|", KeywordDictionary.PrefixOperators.Select(Regex.Escape));
            string prefixRegex = $@"(?<![\w-[的之]])(?:{prefix})(?=[\w-[的之]])";
            return $"<Rule color=\"Operator\">{SecurityElement.Escape(embed)}</Rule>\n" +
                   $"<Rule color=\"Operator\">{SecurityElement.Escape(prefixRegex)}</Rule>\n" +
                   $"<Rule color=\"Operator\">{SecurityElement.Escape(affixRegex)}</Rule>";
        }

        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}

/// <summary>
/// 函式庫名稱（內建字典 + ctdll 轉換表，可能上萬筆）的上色：逐行掃描識別字，
/// 以「的」切開後查表；字串與 // 註解內不上色。
/// </summary>
public sealed class LibraryColorizer : DocumentColorizingTransformer
{
    private static readonly Brush LibraryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x79, 0x5E, 0x26)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>
    /// 為一段不含「的」「之」的詞上色：整段是函式庫名稱就上色；否則依黏著寫的運算子
    /// （被指派、加上…、前後的加一／減一、前置的非）切開後再分別判斷，與轉換器的切法一致。
    /// </summary>
    private void ColorPiece(string text, int start, int end, int lineOffset)
    {
        if (end <= start) return;
        var piece = text.Substring(start, end - start);
        if (KeywordDictionary.IsLibraryWord(piece))
        {
            ChangeLinePart(lineOffset + start, lineOffset + end, el => el.TextRunProperties.SetForegroundBrush(LibraryBrush));
            return;
        }
        if (KeywordDictionary.ChineseToCSharp.ContainsKey(piece)) return; // 其他關鍵字由語法規則上色

        foreach (var op in KeywordDictionary.EmbeddableOperators)
        {
            int p = piece.IndexOf(op, StringComparison.Ordinal);
            if (p < 0) continue;
            ColorPiece(text, start, start + p, lineOffset);
            ColorPiece(text, start + p + op.Length, end, lineOffset);
            return;
        }
        foreach (var op in KeywordDictionary.AffixOperators)
        {
            if (piece.Length > op.Length && piece.StartsWith(op, StringComparison.Ordinal)) { ColorPiece(text, start + op.Length, end, lineOffset); return; }
            if (piece.Length > op.Length && piece.EndsWith(op, StringComparison.Ordinal)) { ColorPiece(text, start, end - op.Length, lineOffset); return; }
        }
        foreach (var op in KeywordDictionary.PrefixOperators)
            if (piece.Length > op.Length && piece.StartsWith(op, StringComparison.Ordinal)) { ColorPiece(text, start + op.Length, end, lineOffset); return; }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '/' or '／' && i + 1 < text.Length && text[i + 1] is '/' or '／') return;
            if (c is '"' or '\'' or '＂')
            {
                int j = i + 1;
                while (j < text.Length && text[j] != c) j += text[j] == '\\' ? 2 : 1;
                i = j + 1;
                continue;
            }
            if (c == '@' && i + 1 < text.Length && Translator.IsIdentStart(text[i + 1]))
            {
                i++;
                while (i < text.Length && Translator.IsIdentPart(text[i])) i++;
                continue;
            }
            if (!Translator.IsIdentStart(c)) { i++; continue; }

            int end = i;
            while (end < text.Length && Translator.IsIdentPart(text[end])) end++;
            // 「註解」「開始註解」：之後是註解，不再上色（與轉換器相同的切法）。
            int commentAt = text.IndexOf("註解", i, end - i, StringComparison.Ordinal);
            if (commentAt >= 0) end = commentAt >= i + 2 && text.Substring(commentAt - 2, 2) == "開始" ? commentAt - 2 : commentAt;
            int pieceStart = i;
            for (int k = i; k <= end; k++)
            {
                if (k == end || text[k] is KeywordDictionary.MemberAccessChar or KeywordDictionary.SpaceChar)
                {
                    if (k > pieceStart) ColorPiece(text, pieceStart, k, line.Offset);
                    pieceStart = k + 1;
                }
            }
            if (commentAt >= 0) return;
            i = end;
        }
    }
}

/// <summary>以 TextAnchor 記錄中斷點，編輯插入／刪除行時會自動跟著移動。</summary>
public sealed class BreakpointManager
{
    private readonly TextDocument _document;
    private readonly List<TextAnchor> _anchors = new();

    public event Action? Changed;

    public BreakpointManager(TextDocument document) => _document = document;

    public IReadOnlyList<int> Lines
    {
        get
        {
            _anchors.RemoveAll(a => a.IsDeleted);
            return _anchors.Select(a => a.Line).Distinct().OrderBy(l => l).ToList();
        }
    }

    public bool Contains(int line) => Lines.Contains(line);

    public void Toggle(int line)
    {
        if (line < 1 || line > _document.LineCount) return;
        var existing = _anchors.Where(a => !a.IsDeleted && a.Line == line).ToList();
        if (existing.Count > 0)
            _anchors.RemoveAll(existing.Contains);
        else
        {
            var anchor = _document.CreateAnchor(_document.GetLineByNumber(line).Offset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;
            anchor.SurviveDeletion = false;
            _anchors.Add(anchor);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        _anchors.Clear();
        Changed?.Invoke();
    }
}

/// <summary>左側中斷點邊界：點一下切換紅點，黃色箭頭表示目前執行位置。</summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private readonly BreakpointManager _breakpoints;
    private int _currentLine;

    public BreakpointMargin(BreakpointManager breakpoints)
    {
        _breakpoints = breakpoints;
        _breakpoints.Changed += InvalidateVisual;
        Cursor = Cursors.Hand;
        ToolTip = "按一下以切換中斷點 (F9)";
    }

    public int CurrentLine
    {
        get => _currentLine;
        set { _currentLine = value; InvalidateVisual(); }
    }

    protected override Size MeasureOverride(Size availableSize) => new(18, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF2)), null, new Rect(0, 0, RenderSize.Width, RenderSize.Height));
        var view = TextView;
        if (view is null || !view.VisualLinesValid) return;
        var bps = _breakpoints.Lines.ToHashSet();
        foreach (var vl in view.VisualLines)
        {
            int line = vl.FirstDocumentLine.LineNumber;
            double y = vl.VisualTop - view.VerticalOffset;
            double h = vl.Height;
            var center = new Point(9, y + h / 2);
            if (bps.Contains(line))
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xC5, 0x1E, 0x1E)), new Pen(Brushes.White, 1), center, 6, 6);
            if (line == _currentLine)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(3, center.Y - 5), true, true);
                    ctx.LineTo(new Point(10, center.Y - 5), true, false);
                    ctx.LineTo(new Point(15, center.Y), true, false);
                    ctx.LineTo(new Point(10, center.Y + 5), true, false);
                    ctx.LineTo(new Point(3, center.Y + 5), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(Brushes.Gold, new Pen(Brushes.DarkGoldenrod, 1), g);
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var view = TextView;
        if (view is null) return;
        var pos = e.GetPosition(view);
        var vl = view.GetVisualLineFromVisualTop(pos.Y + view.VerticalOffset);
        if (vl is not null) _breakpoints.Toggle(vl.FirstDocumentLine.LineNumber);
        e.Handled = true;
    }
}

/// <summary>以背景色標示中斷點行與目前執行行。</summary>
public sealed class LineBackgroundRenderer : IBackgroundRenderer
{
    private readonly BreakpointManager _breakpoints;

    public LineBackgroundRenderer(BreakpointManager breakpoints) => _breakpoints = breakpoints;

    public int CurrentLine { get; set; }
    public bool CurrentIsException { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (!textView.VisualLinesValid) return;
        var bps = _breakpoints.Lines.ToHashSet();
        var bpBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xE5, 0x73, 0x73));
        var curBrush = CurrentIsException
            ? new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xA0, 0x7A))
            : new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xEE, 0x58));
        foreach (var vl in textView.VisualLines)
        {
            int line = vl.FirstDocumentLine.LineNumber;
            Brush? brush = line == CurrentLine ? curBrush : bps.Contains(line) ? bpBrush : null;
            if (brush is null) continue;
            double y = vl.VisualTop - textView.VerticalOffset;
            dc.DrawRectangle(brush, null, new Rect(0, y, textView.ActualWidth + textView.HorizontalOffset, vl.Height));
        }
    }
}

/// <summary>錯誤／警告的波浪底線。</summary>
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    public sealed record Marker(int Offset, int Length, bool IsError, string Message);

    private readonly TextSegmentCollection<TextSegment> _segments;
    private readonly Dictionary<TextSegment, Marker> _markers = new();

    public DiagnosticRenderer(TextDocument document) => _segments = new TextSegmentCollection<TextSegment>(document);

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetMarkers(IEnumerable<Marker> markers, int docLength)
    {
        _segments.Clear();
        _markers.Clear();
        foreach (var m in markers)
        {
            int start = Math.Clamp(m.Offset, 0, docLength);
            int len = Math.Clamp(m.Length, 0, docLength - start);
            var seg = new TextSegment { StartOffset = start, Length = Math.Max(len, 0) };
            _segments.Add(seg);
            _markers[seg] = m;
        }
    }

    public string? MessageAt(int offset) =>
        _segments.FindSegmentsContaining(offset).Select(s => _markers.TryGetValue(s, out var m) ? m.Message : null).FirstOrDefault(m => m != null);

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (!textView.VisualLinesValid || _segments.Count == 0) return;
        var visible = textView.VisualLines;
        int viewStart = visible.First().FirstDocumentLine.Offset;
        int viewEnd = visible.Last().LastDocumentLine.EndOffset;
        foreach (var seg in _segments.FindOverlappingSegments(viewStart, viewEnd - viewStart))
        {
            if (!_markers.TryGetValue(seg, out var m)) continue;
            var pen = new Pen(m.IsError ? Brushes.Red : new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57)), 1);
            pen.Freeze();
            var segForGeo = seg.Length == 0 ? new TextSegment { StartOffset = seg.StartOffset, Length = 1 } : seg;
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segForGeo))
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    double y = r.Bottom - 1;
                    ctx.BeginFigure(new Point(r.Left, y), false, false);
                    bool up = true;
                    for (double x = r.Left + 2; x <= Math.Max(r.Right, r.Left + 6); x += 2)
                    {
                        ctx.LineTo(new Point(x, up ? y - 2 : y), true, false);
                        up = !up;
                    }
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }
    }
}
