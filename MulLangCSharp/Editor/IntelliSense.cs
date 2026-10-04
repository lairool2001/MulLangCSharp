using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MulLangCSharp.Build;
using MulLangCSharp.Language;

namespace MulLangCSharp.Editor;

/// <summary>
/// 以 Roslyn 語意分析提供 IntelliSense：成員完成清單（輸入「的」或「.」時）、
/// 識別字完成清單（開始輸入新詞時、Ctrl+Space）、參數資訊（輸入「(」「,」時）與滑鼠停留的快速資訊。
/// 分析對象是轉換後的 C#，再透過位置對照表對應回中文原始碼。
/// </summary>
public sealed class IntelliSenseService
{
    private readonly TextEditor _editor;
    private readonly Func<string?> _baseDirectory;
    private CompletionWindow? _completion;
    private OverloadInsightWindow? _insight;
    private Analysis? _cache;

    private sealed record Analysis(string Source, TranslationResult Translation, SyntaxTree Tree, SemanticModel Model);

    private static readonly SymbolDisplayFormat SignatureFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
                           SymbolDisplayMemberOptions.IncludeContainingType)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                              SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue)
        .WithLocalOptions(SymbolDisplayLocalOptions.IncludeType);

    public IntelliSenseService(TextEditor editor, Func<string?> baseDirectory)
    {
        _editor = editor;
        _baseDirectory = baseDirectory;
        editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
        editor.TextArea.TextEntering += OnTextEntering;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateInsight();
    }

    public bool Enabled => !_editor.IsReadOnly;

    /// <summary>打字時把全形符號自動換成半形（預設關閉：全形與半形互通，打什麼就保留什麼）。</summary>
    public bool ConvertFullWidthOnTyping { get; set; }

    // ───────────────────────── 分析 ─────────────────────────

    private Analysis Analyze()
    {
        var source = _editor.Document.Text;
        if (_cache is not null && _cache.Source == source) return _cache;
        var translation = Translator.ToCSharp(source);
        var tree = CSharpSyntaxTree.ParseText(translation.Code, CodeCompiler.ParseOptions);
        var user = CodeCompiler.ResolveUserReferences(translation, _baseDirectory());
        var model = CodeCompiler.CreateAnalysisCompilation(tree, user).GetSemanticModel(tree);
        return _cache = new Analysis(source, translation, tree, model);
    }

    private static bool IsInStringOrComment(SyntaxNode root, int position)
    {
        var token = root.FindToken(position, findInsideTrivia: true);
        if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.CharacterLiteralToken) ||
            token.IsKind(SyntaxKind.InterpolatedStringTextToken) || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) ||
            token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) || token.IsKind(SyntaxKind.Utf8StringLiteralToken))
            return token.SpanStart < position && position < token.Span.End ||
                   (position == token.Span.End && !token.Text.EndsWith('"') && !token.Text.EndsWith('\''));
        var trivia = root.FindTrivia(position > 0 ? position - 1 : position);
        return trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
               trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
    }

    private static string Zh(string name) => KeywordDictionary.CSharpToChinese.TryGetValue(name, out var z) ? z : name;

    // ───────────────────────── 輸入事件 ─────────────────────────

    /// <summary>在這些字元之後（尚未輸入任何字）也自動顯示完成清單。</summary>
    private static bool IsAutoTriggerChar(char c) =>
        c is ' ' or '\t' or KeywordDictionary.SpaceChar or '(' or ',' or '=' or '{' or '[' or '!' or '+' or '-' or '*' or '/' or '%' or
             '<' or '>' or '&' or '|' or '?' or ':' or ';';

    /// <summary>
    /// 清單是「尚未輸入任何字」自動跳出、且使用者沒有用方向鍵選取項目時，
    /// Enter / Tab 照常換行、縮排，不會誤插入項目。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool enter = e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None;
        bool tab = e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None;

        if (_completion is not null && (enter || tab) &&
            _editor.CaretOffset == _completion.StartOffset && _completion.CompletionList.SelectedItem is null)
        {
            _completion.Close();
            e.Handled = true;
            if (enter) EditingCommands.EnterParagraphBreak.Execute(null, _editor.TextArea);
            else EditingCommands.TabForward.Execute(null, _editor.TextArea);
            ScheduleAutoCompletion();
            return;
        }

        // 換行後（新的一行尚無任何輸入）也顯示完成清單。
        if (enter && _completion is null)
            ScheduleAutoCompletion();
    }

    private void ScheduleAutoCompletion() =>
        _editor.Dispatcher.BeginInvoke(() =>
        {
            if (_completion is null && _editor.TextArea.IsKeyboardFocusWithin)
                ShowCompletion(explicitInvoke: false);
        }, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>打字時要直接換成半形的全形符號（字串與註解內不換）。</summary>
    private static readonly Dictionary<char, char> FullWidthBrackets = new()
    {
        ['（'] = '(', ['）'] = ')', ['｛'] = '{', ['｝'] = '}',
        ['，'] = ',', ['＜'] = '<', ['＞'] = '>', ['＄'] = '$', ['［'] = '[', ['］'] = ']',
    };

    /// <summary>全形雙引號：字串中打「＂」通常是要結束字串，因此只有在註解中才保留全形。</summary>
    private const char FullWidthQuote = '＂';

    /// <summary>游標位置是否在字串或註解中。</summary>
    private bool CaretInStringOrComment()
    {
        try
        {
            var a = Analyze();
            return IsInStringOrComment(a.Tree.GetRoot(), a.Translation.ToTranslatedCaret(_editor.CaretOffset));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>游標位置是否在註解中。</summary>
    private bool CaretInComment()
    {
        try
        {
            var a = Analyze();
            int pos = a.Translation.ToTranslatedCaret(_editor.CaretOffset);
            var trivia = a.Tree.GetRoot().FindTrivia(pos > 0 ? pos - 1 : pos);
            return trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                   trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnTextEntering(object sender, TextCompositionEventArgs e)
    {
        // 全形符號（輸入法中文模式）→ 半形，讓後續的參數資訊、自動完成照常運作。
        if (Enabled && ConvertFullWidthOnTyping && e.Text.Any(ch => FullWidthBrackets.ContainsKey(ch) || ch == FullWidthQuote))
        {
            bool inComment = CaretInComment();
            bool inStringOrComment = inComment || CaretInStringOrComment();
            var converted = new string(e.Text.Select(ch =>
                ch == FullWidthQuote ? (inComment ? ch : '"')
                : FullWidthBrackets.TryGetValue(ch, out var h) && !inStringOrComment ? h
                : ch).ToArray());
            if (converted != e.Text)
            {
                e.Handled = true;
                _editor.TextArea.PerformTextInput(converted);
                return;
            }
        }

        if (_completion is null || e.Text.Length != 1) return;
        char c = e.Text[0];
        bool commitChar = c is '(' or '.' or ';' or '[' or KeywordDictionary.MemberAccessChar;
        if (!commitChar) return;

        // 只有在已輸入的字首確實符合選取項目時才自動完成，避免誤換掉使用者的字。
        int start = _completion.StartOffset, caret = _editor.CaretOffset;
        var typed = caret > start ? _editor.Document.GetText(start, caret - start) : "";
        if (typed.Length > 0 && _completion.CompletionList.SelectedItem is { } item && item.Text.StartsWith(typed, StringComparison.Ordinal))
            _completion.CompletionList.RequestInsertion(e);
        else
            _completion.Close();
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (!Enabled || e.Text.Length == 0) return;
        char last = e.Text[^1];

        if (last is '(' or ',') ShowParameterInfo();
        if (last == ')')
        {
            UpdateInsight();
            return;
        }
        if (last == KeywordDictionary.MemberAccessChar || last == '.')
        {
            ShowCompletion(explicitInvoke: false);
            return;
        }

        if (_completion is not null)
        {
            // 篩選後已無符合項目（例如輸入了空白或「之」）就關閉，接著依下面的規則重新判斷。
            if (_completion.CompletionList.ListBox is { Items.Count: 0 }) _completion.Close();
            else return;
        }

        // 空白、「之」、運算符號之後（尚未輸入任何字）也自動顯示。
        if (IsAutoTriggerChar(last))
        {
            ShowCompletion(explicitInvoke: false);
            return;
        }

        // 開始輸入一個新的詞（游標前的詞剛好就是這次輸入的內容）時自動顯示。
        if (Translator.IsIdentStart(e.Text[0]) && e.Text.All(Translator.IsIdentPart))
        {
            int wordStart = WordStart(_editor.CaretOffset);
            if (_editor.CaretOffset - wordStart == e.Text.Length)
                ShowCompletion(explicitInvoke: false);
        }
    }

    private int WordStart(int caret)
    {
        var doc = _editor.Document;
        int start = caret;
        while (start > 0)
        {
            char c = doc.GetCharAt(start - 1);
            if (!Translator.IsIdentPart(c) || c is KeywordDictionary.MemberAccessChar or KeywordDictionary.SpaceChar) break;
            start--;
        }
        return start;
    }

    // ───────────────────────── 完成清單 ─────────────────────────

    public void ShowCompletion(bool explicitInvoke)
    {
        if (!Enabled) return;
        _completion?.Close();

        var doc = _editor.Document;
        int caret = _editor.CaretOffset;
        int wordStart = WordStart(caret);
        bool isMember = wordStart > 0 && doc.GetCharAt(wordStart - 1) is KeywordDictionary.MemberAccessChar or '.';

        // 數字中的小數點不觸發。
        if (isMember && doc.GetCharAt(wordStart - 1) == '.' && wordStart >= 2 && char.IsDigit(doc.GetCharAt(wordStart - 2)))
            return;

        List<CompletionEntry> entries;
        try
        {
            var a = Analyze();
            int pos = a.Translation.ToTranslatedCaret(wordStart);
            var root = a.Tree.GetRoot();
            if (IsInStringOrComment(root, pos)) return;
            entries = isMember ? MemberEntries(a, pos) : ScopeEntries(a, pos);
        }
        catch (Exception)
        {
            return;
        }
        if (entries.Count == 0) return;

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = wordStart,
            EndOffset = caret,
            Width = 380,
            CloseWhenCaretAtBeginning = !explicitInvoke,
        };
        var data = window.CompletionList.CompletionData;
        foreach (var e in entries.OrderBy(e => e.IsTranslated ? 0 : 1).ThenBy(e => e.Text, StringComparer.Ordinal))
            data.Add(e);

        if (caret > wordStart)
        {
            window.CompletionList.SelectItem(doc.GetText(wordStart, caret - wordStart));
            if (window.CompletionList.ListBox is { Items.Count: 0 } && !explicitInvoke) return;
        }
        window.Closed += (_, _) => { if (_completion == window) _completion = null; };
        _completion = window;
        window.Show();
    }

    /// <summary>「運算式的」之後：列出型別／命名空間成員。</summary>
    private static List<CompletionEntry> MemberEntries(Analysis a, int pos)
    {
        var root = a.Tree.GetRoot();
        var dot = root.FindToken(Math.Max(0, pos - 1));
        if (!dot.IsKind(SyntaxKind.DotToken)) return new();

        ExpressionSyntax? expr = dot.Parent switch
        {
            MemberAccessExpressionSyntax ma => ma.Expression,
            QualifiedNameSyntax qn => qn.Left,
            _ => null,
        };
        if (expr is null) return new();

        var model = a.Model;
        var symbol = model.GetSymbolInfo(expr).Symbol;
        IEnumerable<ISymbol> symbols;

        if (symbol is INamespaceSymbol ns)
        {
            symbols = model.LookupNamespacesAndTypes(pos, ns);
        }
        else if (symbol is INamedTypeSymbol type && expr is not (ThisExpressionSyntax or BaseExpressionSyntax) && !IsColorColor(model, expr, type))
        {
            symbols = model.LookupSymbols(pos, type).Where(s => s.IsStatic || s is INamedTypeSymbol);
        }
        else
        {
            var exprType = model.GetTypeInfo(expr).Type;
            if (exprType is null || exprType.TypeKind == TypeKind.Error) return new();
            symbols = model.LookupSymbols(pos, exprType, includeReducedExtensionMethods: true)
                .Where(s => !s.IsStatic || s is IMethodSymbol { MethodKind: MethodKind.ReducedExtension });
        }

        return ToEntries(symbols.Where(IsCompletable));
    }

    /// <summary>屬性與型別同名（例如 Color Color）時，把它當作執行個體處理。</summary>
    private static bool IsColorColor(SemanticModel model, ExpressionSyntax expr, INamedTypeSymbol type) =>
        expr is IdentifierNameSyntax id &&
        model.LookupSymbols(expr.SpanStart, name: id.Identifier.ValueText)
            .Any(s => s is IPropertySymbol or IFieldSymbol or ILocalSymbol or IParameterSymbol);

    /// <summary>一般位置：區域變數、參數、成員、型別、命名空間，再加上字典中的關鍵字。</summary>
    private static List<CompletionEntry> ScopeEntries(Analysis a, int pos)
    {
        var entries = ToEntries(a.Model.LookupSymbols(pos).Where(IsCompletable));
        var names = entries.Select(e => e.Text).ToHashSet();
        foreach (var w in KeywordDictionary.Entries)
        {
            if (w.Category == WordCategory.函式庫 || !names.Add(w.Chinese)) continue;
            entries.Add(new CompletionEntry(w.Chinese, w.CSharp, CompletionKind.Keyword,
                $"{w.Category}　{w.Chinese} → {w.CSharp}{(w.Note.Length > 0 ? "\n" + w.Note : "")}"));
        }
        return entries;
    }

    private static bool IsCompletable(ISymbol s)
    {
        if (!s.CanBeReferencedByName) return false;
        if (s is IMethodSymbol m && m.MethodKind is not (MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.LocalFunction))
            return false;
        if (s.Name.StartsWith("__MulLang", StringComparison.Ordinal)) return false;
        return true;
    }

    private static List<CompletionEntry> ToEntries(IEnumerable<ISymbol> symbols)
    {
        var list = new List<CompletionEntry>();
        foreach (var g in symbols.GroupBy(s => Zh(s.Name)))
        {
            var first = g.First();
            int overloads = g.Count(s => s is IMethodSymbol);
            var csharp = first.ToDisplayString(SignatureFormat);
            var desc = $"{KindLabel(first)} {Translator.ToChinese(csharp)}" +
                       (overloads > 1 ? $"（+{overloads - 1} 個多載）" : "") +
                       (g.Key != first.Name ? $"\n{csharp}" : "");
            list.Add(new CompletionEntry(g.Key, first.Name, KindOf(first), desc));
        }
        return list;
    }

    public enum CompletionKind { Keyword, Namespace, Type, Method, Property, Field, Event, Local }

    private static CompletionKind KindOf(ISymbol s) => s switch
    {
        INamespaceSymbol => CompletionKind.Namespace,
        ITypeSymbol => CompletionKind.Type,
        IMethodSymbol => CompletionKind.Method,
        IPropertySymbol => CompletionKind.Property,
        IFieldSymbol => CompletionKind.Field,
        IEventSymbol => CompletionKind.Event,
        _ => CompletionKind.Local,
    };

    private static string KindLabel(ISymbol s) => s switch
    {
        INamespaceSymbol => "（命名空間）",
        INamedTypeSymbol { TypeKind: TypeKind.Class } => "（類別）",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "（結構）",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "（介面）",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "（列舉）",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "（委派）",
        ITypeSymbol => "（型別）",
        IMethodSymbol { MethodKind: MethodKind.ReducedExtension } => "（擴充方法）",
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => "（區域函式）",
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "（建構函式）",
        IMethodSymbol => "（方法）",
        IPropertySymbol => "（屬性）",
        IFieldSymbol { IsConst: true } => "（常數）",
        IFieldSymbol f when f.ContainingType?.TypeKind == TypeKind.Enum => "（列舉值）",
        IFieldSymbol => "（欄位）",
        IEventSymbol => "（事件）",
        ILocalSymbol => "（區域變數）",
        IParameterSymbol => "（參數）",
        IRangeVariableSymbol => "（範圍變數）",
        _ => "",
    };

    // ───────────────────────── 快速資訊 ─────────────────────────

    /// <summary>滑鼠停留在原始碼某字元上時的說明文字；沒有可說明的符號時傳回 null。</summary>
    public string? QuickInfo(int sourceOffset)
    {
        try
        {
            if (sourceOffset < 0 || sourceOffset >= _editor.Document.TextLength) return null;
            if (!Translator.IsIdentPart(_editor.Document.GetCharAt(sourceOffset))) return null;
            var a = Analyze();
            int pos = a.Translation.ToTranslatedChar(sourceOffset);
            var root = a.Tree.GetRoot();
            var token = root.FindToken(pos);
            if (!token.Span.Contains(pos)) return null;

            if (SyntaxFacts.IsKeywordKind(token.Kind()) && token.Parent is not PredefinedTypeSyntax)
            {
                var word = KeywordDictionary.Entries.FirstOrDefault(w => w.CSharp == token.Text);
                return word is null ? null : $"{word.Category}　{word.Chinese} → {word.CSharp}{(word.Note.Length > 0 ? "\n" + word.Note : "")}";
            }

            var node = token.Parent;
            if (node is null) return null;
            var model = a.Model;
            var symbol = model.GetSymbolInfo(node).Symbol
                         ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault()
                         ?? model.GetDeclaredSymbol(node)
                         ?? (node.Parent is { } p ? model.GetDeclaredSymbol(p) : null);
            if (symbol is null) return null;

            var csharp = symbol.ToDisplayString(SignatureFormat);
            var zh = Translator.ToChinese(csharp);
            var text = $"{KindLabel(symbol)} {zh}";
            if (zh != csharp) text += "\n" + csharp;
            if (symbol is ILocalSymbol { HasConstantValue: true } l) text += $"\n常數值：{l.ConstantValue}";
            return text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ───────────────────────── 參數資訊 ─────────────────────────

    private void ShowParameterInfo()
    {
        var info = FindCall();
        if (info is null) { _insight?.Close(); return; }

        if (_insight is { Provider: OverloadProvider existing } && existing.SameCall(info.Value.callStart))
        {
            existing.SetArgument(info.Value.argIndex);
            return;
        }

        _insight?.Close();
        var provider = new OverloadProvider(info.Value.methods, info.Value.argIndex, info.Value.callStart);
        var window = new OverloadInsightWindow(_editor.TextArea) { Provider = provider };
        window.Closed += (_, _) => { if (_insight == window) _insight = null; };
        _insight = window;
        window.Show();
    }

    private void UpdateInsight()
    {
        if (_insight?.Provider is not OverloadProvider provider) return;
        var info = FindCall();
        if (info is null || !provider.SameCall(info.Value.callStart)) { _insight.Close(); return; }
        provider.SetArgument(info.Value.argIndex);
    }

    private (IReadOnlyList<IMethodSymbol> methods, int argIndex, int callStart)? FindCall()
    {
        try
        {
            var a = Analyze();
            int pos = a.Translation.ToTranslatedCaret(_editor.CaretOffset);
            var root = a.Tree.GetRoot();
            if (IsInStringOrComment(root, pos)) return null;

            var token = root.FindToken(Math.Max(0, pos - 1));
            var argList = token.Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>()
                .FirstOrDefault(l => l.OpenParenToken.Span.End <= pos &&
                                     (l.CloseParenToken.IsMissing || pos <= l.CloseParenToken.SpanStart));
            if (argList is null) return null;

            int argIndex = argList.Arguments.GetSeparators().Count(s => s.SpanStart < pos);
            var model = a.Model;
            IEnumerable<IMethodSymbol> methods = argList.Parent switch
            {
                InvocationExpressionSyntax inv => model.GetMemberGroup(inv.Expression).OfType<IMethodSymbol>()
                    .Concat(model.GetSymbolInfo(inv).CandidateSymbols.OfType<IMethodSymbol>()),
                BaseObjectCreationExpressionSyntax creation =>
                    (model.GetTypeInfo(creation).Type as INamedTypeSymbol)?.InstanceConstructors
                        .Where(c => model.IsAccessible(pos, c)) ?? Enumerable.Empty<IMethodSymbol>(),
                _ => Enumerable.Empty<IMethodSymbol>(),
            };
            var list = methods.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                .OrderBy(m => m.Parameters.Length).ToList();
            if (list.Count == 0) return null;
            int sourceCallStart = a.Translation.ToSourceOffset(argList.SpanStart);
            return (list, argIndex, sourceCallStart);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class OverloadProvider : IOverloadProvider
    {
        private readonly IReadOnlyList<IMethodSymbol> _methods;
        private readonly int _callStart;
        private int _selected;
        private int _arg;

        public OverloadProvider(IReadOnlyList<IMethodSymbol> methods, int arg, int callStart)
        {
            _methods = methods;
            _callStart = callStart;
            _arg = arg;
            _selected = Math.Max(0, FirstAccepting(arg));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool SameCall(int callStart) => callStart == _callStart;

        /// <summary>第一個能接受第 arg 個引數的多載（第 0 個引數時直接選第一個多載）。</summary>
        private int FirstAccepting(int arg) =>
            _methods.ToList().FindIndex(m => arg == 0 || m.Parameters.Length > arg || m.Parameters.Any(p => p.IsParams));

        public void SetArgument(int arg)
        {
            if (arg == _arg) return;
            _arg = arg;
            var cur = _methods[_selected];
            if (cur.Parameters.Length <= arg && !cur.Parameters.Any(p => p.IsParams))
            {
                int better = FirstAccepting(arg);
                if (better >= 0) _selected = better;
            }
            Raise();
        }

        public int SelectedIndex
        {
            get => _selected;
            set { _selected = Math.Clamp(value, 0, _methods.Count - 1); Raise(); }
        }

        public int Count => _methods.Count;
        public string CurrentIndexText => $"{_selected + 1} / {_methods.Count}";

        public object CurrentHeader
        {
            get
            {
                var m = _methods[_selected];
                var tb = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft JhengHei") };
                if (m.MethodKind != MethodKind.Constructor)
                    tb.Inlines.Add(new Run(Translator.ToChinese(m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)) + " ")
                        { Foreground = Brushes.Blue });
                var name = m.MethodKind == MethodKind.Constructor ? m.ContainingType.Name : m.Name;
                tb.Inlines.Add(new Run(Zh(name)) { FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new Run("("));
                for (int i = 0; i < m.Parameters.Length; i++)
                {
                    if (i > 0) tb.Inlines.Add(new Run(", "));
                    var p = m.Parameters[i];
                    var text = (p.IsParams ? "可變參數 " : "") +
                               (p.RefKind switch { RefKind.Ref => "參考 ", RefKind.Out => "傳出 ", RefKind.In => "在 ", _ => "" }) +
                               Translator.ToChinese(p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)) + " " + p.Name +
                               (p.HasExplicitDefaultValue ? " = " + (p.ExplicitDefaultValue?.ToString() ?? "空") : "");
                    bool current = i == _arg || (p.IsParams && _arg >= i);
                    tb.Inlines.Add(new Run(text)
                    {
                        FontWeight = current ? FontWeights.Bold : FontWeights.Normal,
                        Background = current ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xC2)) : null,
                    });
                }
                tb.Inlines.Add(new Run(")"));
                return tb;
            }
        }

        public object CurrentContent => _methods[_selected].ToDisplayString(SignatureFormat);

        private void Raise()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentIndexText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentContent)));
        }
    }

    // ───────────────────────── 完成清單項目 ─────────────────────────

    public sealed class CompletionEntry : ICompletionData
    {
        private static readonly Dictionary<CompletionKind, (string glyph, Brush brush)> Glyphs = new()
        {
            [CompletionKind.Keyword] = ("■", Brushes.SteelBlue),
            [CompletionKind.Namespace] = ("{}", Brushes.Gray),
            [CompletionKind.Type] = ("◆", new SolidColorBrush(Color.FromRgb(0x2B, 0x91, 0xAF))),
            [CompletionKind.Method] = ("ƒ", new SolidColorBrush(Color.FromRgb(0x8E, 0x44, 0xAD))),
            [CompletionKind.Property] = ("▪", new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))),
            [CompletionKind.Field] = ("●", new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xB5))),
            [CompletionKind.Event] = ("⚡", Brushes.DarkGoldenrod),
            [CompletionKind.Local] = ("○", new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79))),
        };

        private readonly string _csharp;
        private readonly CompletionKind _kind;

        public CompletionEntry(string text, string csharp, CompletionKind kind, string description)
        {
            Text = text;
            _csharp = csharp;
            _kind = kind;
            Description = description;
            Priority = kind is CompletionKind.Local ? 2 : kind is CompletionKind.Keyword ? 0 : 1;
        }

        public ImageSource? Image => null;
        public bool IsTranslated => _csharp != Text;
        public string Text { get; }
        public object Description { get; }
        public double Priority { get; }

        public object Content
        {
            get
            {
                var (glyph, brush) = Glyphs[_kind];
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(new TextBlock { Text = glyph, Foreground = brush, Width = 20, TextAlignment = TextAlignment.Center });
                panel.Children.Add(new TextBlock { Text = Text });
                if (_csharp != Text)
                    panel.Children.Add(new TextBlock { Text = "  " + _csharp, Foreground = Brushes.Gray });
                return panel;
            }
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }
}
