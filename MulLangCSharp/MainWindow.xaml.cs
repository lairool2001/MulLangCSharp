using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using MulLangCSharp.Build;
using MulLangCSharp.Debugging;
using MulLangCSharp.Editor;
using MulLangCSharp.Language;

namespace MulLangCSharp;

public sealed class VariableItem
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string TypeDisplay { get; init; } = "";
    public List<VariableItem> Children { get; init; } = new();

    public static VariableItem From(VarNode n) => new()
    {
        Name = KeywordDictionary.CSharpToChinese.TryGetValue(n.N, out var zh) ? zh : n.N,
        Value = n.V,
        TypeDisplay = string.IsNullOrEmpty(n.T) ? "" : Translator.ToChinese(n.T),
        Children = n.C?.Select(From).ToList() ?? new(),
    };
}

public partial class MainWindow : Window
{
    private enum IdeState { 編輯中, 執行中, 已暫停 }

    private const string FileFilter = "中文 C# 檔案 (*.ccs;*.tcs;*.cs)|*.ccs;*.tcs;*.cs|文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*";

    private readonly BreakpointManager _breakpoints;
    private readonly BreakpointMargin _breakpointMargin;
    private readonly LineBackgroundRenderer _lineRenderer;
    private readonly DiagnosticRenderer _diagnosticRenderer;
    private readonly DispatcherTimer _translateTimer;
    private readonly ToolTip _hoverTip = new();
    private readonly IntelliSenseService _intelliSense;

    private TranslationResult _translation = Translator.ToCSharp("");
    private CancellationTokenSource? _analyzeCts;
    private DebugSession? _session;
    private PausedInfo? _paused;
    private IdeState _state = IdeState.編輯中;
    private string? _filePath;
    private bool _dirty;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        Editor.SyntaxHighlighting = ChineseHighlighting.Create();
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        SearchPanel.Install(Editor);

        CSharpView.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");

        _breakpoints = new BreakpointManager(Editor.Document);
        _breakpointMargin = new BreakpointMargin(_breakpoints);
        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);
        _lineRenderer = new LineBackgroundRenderer(_breakpoints);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_lineRenderer);
        _diagnosticRenderer = new DiagnosticRenderer(Editor.Document);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        Editor.TextArea.TextView.LineTransformers.Add(new LibraryColorizer());
        _breakpoints.Changed += OnBreakpointsChanged;
        _intelliSense = new IntelliSenseService(Editor, () => BaseDirectory);

        _translateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _translateTimer.Tick += (_, _) => { _translateTimer.Stop(); UpdateTranslation(); };

        Editor.TextChanged += (_, _) =>
        {
            if (!_dirty) { _dirty = true; UpdateTitle(); }
            _translateTimer.Stop();
            _translateTimer.Start();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            CaretText.Text = $"行 {Editor.TextArea.Caret.Line}，欄 {Editor.TextArea.Caret.Column}";
        Editor.MouseHover += Editor_MouseHover;
        Editor.MouseHoverStopped += (_, _) => _hoverTip.IsOpen = false;

        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += Window_Closing;

        Task.Run(CodeCompiler.Warmup);
        // 系統 ctdll 已預先做好並隨程式附帶：同步載入，第一次轉換就能使用函式庫中文名稱。
        ApplyCtdll();
        _ = LoadCtdllAsync(update: false);
        CleanupOldBuilds();

        Editor.Text = Samples.HelloWorld;
        _dirty = false;
        UpdateTitle();
        UpdateTranslation();
        UpdateUiState();
        BtnGlue.IsChecked = AppSettings.GetBool("FormatGlue", true);
        BtnHalfWidth.IsChecked = AppSettings.GetBool("TypingHalfWidth", false);
        _intelliSense.ConvertFullWidthOnTyping = BtnHalfWidth.IsChecked == true;
    }

    // ───────────────────────── 轉換與即時錯誤檢查 ─────────────────────────

    private void UpdateTranslation()
    {
        var source = Editor.Text;
        _translation = Translator.ToCSharp(source);
        var offset = CSharpView.VerticalOffset;
        CSharpView.Document.Text = _translation.Code;
        CSharpView.ScrollToVerticalOffset(offset);

        _analyzeCts?.Cancel();
        var cts = _analyzeCts = new CancellationTokenSource();
        var translation = _translation;
        var baseDir = BaseDirectory;
        RefreshUserCtdll(translation);
        Task.Run(() =>
            {
                // 文字又被修改時會取消舊的分析；在此吃掉取消例外，避免偵錯工具中斷在「使用者未處理的例外」。
                try { return CodeCompiler.Analyze(source, translation, baseDir, cts.Token); }
                catch (OperationCanceledException) { return null; }
            })
            .ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion || t.Result is null) return;
                if (Editor.Text != source) return;
                ShowDiagnostics(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowDiagnostics(List<CompileDiagnostic> diagnostics)
    {
        ErrorList.ItemsSource = diagnostics;
        int errors = diagnostics.Count(d => d.IsError);
        int warnings = diagnostics.Count - errors;
        TabErrors.Header = errors + warnings == 0 ? "錯誤清單" : $"錯誤清單（{errors} 個錯誤，{warnings} 個警告）";
        _diagnosticRenderer.SetMarkers(
            diagnostics.Select(d => new DiagnosticRenderer.Marker(d.SourceStart, d.SourceLength, d.IsError, $"{d.Id}：{d.Message}")),
            Editor.Document.TextLength);
        Editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
    }

    private void Editor_MouseHover(object sender, MouseEventArgs e)
    {
        var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (pos is null) return;
        int offset = Editor.Document.GetOffset(pos.Value.Location);
        var msg = _diagnosticRenderer.MessageAt(offset) ?? _intelliSense.QuickInfo(offset);
        if (msg is null) return;
        _hoverTip.PlacementTarget = Editor;
        _hoverTip.Content = msg;
        _hoverTip.IsOpen = true;
        e.Handled = true;
    }

    // ───────────────────────── 編譯 / 執行 / 偵錯 ─────────────────────────

    private async Task<CompileResult?> BuildAsync(bool forDebug)
    {
        if (_busy) return null;
        _busy = true;
        UpdateUiState();
        try
        {
            _translateTimer.Stop();
            UpdateTranslation();
            var source = Editor.Text;
            var translation = _translation;
            var baseDir = BaseDirectory;
            var outDir = Path.Combine(BuildRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));

            OutputBox.Clear();
            Log($"========== 編譯開始：{DateTime.Now:HH:mm:ss}（{(forDebug ? "偵錯" : "一般")}模式）==========");
            BottomTabs.SelectedItem = TabOutput;
            StatusText.Text = "編譯中…";

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => CodeCompiler.Compile(source, translation, outDir, "MulLangProgram", forDebug, baseDir));
            stopwatch.Stop();
            var elapsed = $"{stopwatch.Elapsed.TotalSeconds:F2} 秒";

            ShowDiagnostics(result.Diagnostics);
            foreach (var d in result.Diagnostics)
                Log($"  第 {d.Line} 行，第 {d.Column} 欄：{d.Severity} {d.Id}：{d.Message}");
            foreach (var n in result.Notes)
                Log("  " + n);

            if (result.Success)
            {
                Log($"編譯成功（耗時 {elapsed}）→ {result.AssemblyPath}");
                StatusText.Text = $"編譯成功（{elapsed}）";
            }
            else
            {
                int errors = result.Diagnostics.Count(d => d.IsError);
                Log($"編譯失敗：{errors} 個錯誤（耗時 {elapsed}）。");
                StatusText.Text = "編譯失敗";
                BottomTabs.SelectedItem = TabErrors;
            }
            return result;
        }
        catch (Exception ex)
        {
            Log("編譯時發生例外：" + ex);
            StatusText.Text = "編譯失敗";
            return null;
        }
        finally
        {
            _busy = false;
            UpdateUiState();
        }
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_state != IdeState.編輯中) return;
        await BuildAsync(false);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_state != IdeState.編輯中) return;
        var result = await BuildAsync(false);
        if (result is not { Success: true }) return;
        try
        {
            DebugSession.RunDetached(result.AssemblyPath!);
            Log("已在新的主控台視窗中啟動程式。");
        }
        catch (Exception ex)
        {
            Log("無法啟動程式：" + ex.Message);
        }
    }

    private async void DebugOrContinue_Click(object sender, RoutedEventArgs e)
    {
        if (_state == IdeState.已暫停)
        {
            Resume(s => s.Continue());
            return;
        }
        if (_state != IdeState.編輯中) return;
        await StartDebuggingAsync(stepIntoFirst: false);
    }

    private async Task StartDebuggingAsync(bool stepIntoFirst)
    {
        var result = await BuildAsync(true);
        if (result is not { Success: true }) return;
        try
        {
            var bps = _breakpoints.Lines.ToList();
            var session = DebugSession.StartDebugging(result.AssemblyPath!, bps, stepIntoFirst);
            session.Paused += info => Dispatcher.BeginInvoke(() => OnPaused(session, info));
            session.Ended += () => Dispatcher.BeginInvoke(() => OnSessionEnded(session));
            _session = session;
            SetState(IdeState.執行中);
            Log(result.Instrumented ? "偵錯開始。程式在新的主控台視窗中執行。" : "程式已啟動（未插樁，中斷點不會生效）。");
        }
        catch (Exception ex)
        {
            Log("無法啟動偵錯：" + ex.Message);
        }
    }

    private void OnPaused(DebugSession session, PausedInfo info)
    {
        if (session != _session) return;
        _paused = info;
        SetState(IdeState.已暫停);

        bool exception = info.Reason == "exception";
        _lineRenderer.CurrentIsException = exception;
        ShowExecutionLine(info.Line);

        CallStackList.ItemsSource = info.Frames;
        CallStackList.SelectedIndex = info.Frames.Count > 0 ? 0 : -1;
        ShowVariables(info.Frames.FirstOrDefault());
        BottomTabs.SelectedItem = TabDebug;

        string reason = info.Reason switch
        {
            "breakpoint" => "命中中斷點",
            "step" => "逐步執行",
            "pause" => "已暫停",
            "exception" => "發生未處理的例外",
            _ => info.Reason,
        };
        StatusText.Text = $"已暫停：{reason}（第 {info.Line} 行）";
        if (exception)
        {
            Log($"未處理的例外（第 {info.Line} 行）：{info.Message}");
            MessageBox.Show(this, $"第 {info.Line} 行發生未處理的例外：\n\n{info.Message}\n\n按「繼續」將結束程式。",
                "例外", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Editor.Focus();
    }

    private void OnSessionEnded(DebugSession session)
    {
        if (session != _session) return;
        session.Dispose();
        _session = null;
        _paused = null;
        SetState(IdeState.編輯中);
        ShowExecutionLine(0);
        CallStackList.ItemsSource = null;
        VariablesTree.ItemsSource = null;
        Log("偵錯已結束。");
        StatusText.Text = "就緒";
    }

    private void ShowExecutionLine(int line)
    {
        _lineRenderer.CurrentLine = line;
        _breakpointMargin.CurrentLine = line;
        Editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
        if (line > 0 && line <= Editor.Document.LineCount)
        {
            Editor.ScrollToLine(line);
            Editor.TextArea.Caret.Line = line;
            Editor.TextArea.Caret.Column = 1;
        }
    }

    private void ShowVariables(FrameInfo? frame)
    {
        VariablesTree.ItemsSource = frame?.Vars.Select(VariableItem.From).ToList();
    }

    private void CallStack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CallStackList.SelectedItem is FrameInfo f)
        {
            ShowVariables(f);
            if (f.Line > 0) ShowExecutionLine(f.Line);
        }
    }

    private void Resume(Action<DebugSession> command)
    {
        if (_session is null || _state != IdeState.已暫停) return;
        _paused = null;
        _lineRenderer.CurrentIsException = false;
        ShowExecutionLine(0);
        SetState(IdeState.執行中);
        StatusText.Text = "執行中…";
        command(_session);
    }

    private async void StepInto_Click(object sender, RoutedEventArgs e)
    {
        if (_state == IdeState.編輯中) await StartDebuggingAsync(stepIntoFirst: true);
        else Resume(s => s.StepInto());
    }

    private async void StepOver_Click(object sender, RoutedEventArgs e)
    {
        if (_state == IdeState.編輯中) await StartDebuggingAsync(stepIntoFirst: true);
        else Resume(s => s.StepOver());
    }

    private void StepOut_Click(object sender, RoutedEventArgs e) => Resume(s => s.StepOut());

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_state == IdeState.執行中) _session?.RequestPause();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _session?.Stop();

    private void ToggleBreakpoint_Click(object sender, RoutedEventArgs e) => _breakpoints.Toggle(Editor.TextArea.Caret.Line);

    private void ClearBreakpoints_Click(object sender, RoutedEventArgs e) => _breakpoints.Clear();

    private void OnBreakpointsChanged()
    {
        Editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
        _session?.SetBreakpoints(_breakpoints.Lines);
    }

    private void SetState(IdeState state)
    {
        _state = state;
        Editor.IsReadOnly = state != IdeState.編輯中;
        EditorTitle.Text = state switch
        {
            IdeState.執行中 => "中文 C# 原始碼 — 執行中（偵錯時唯讀）",
            IdeState.已暫停 => "中文 C# 原始碼 — 已暫停（偵錯時唯讀）",
            _ => "中文 C# 原始碼",
        };
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        bool editing = _state == IdeState.編輯中 && !_busy;
        bool paused = _state == IdeState.已暫停;
        bool running = _state == IdeState.執行中;

        BtnBuild.IsEnabled = MenuRun.IsEnabled = BtnRun.IsEnabled = editing;
        BtnDebug.IsEnabled = MenuDebug.IsEnabled = editing || paused;
        BtnDebug.Content = paused ? "▶ 繼續" : "▶ 偵錯";
        BtnPause.IsEnabled = MenuPause.IsEnabled = running;
        BtnStop.IsEnabled = MenuStop.IsEnabled = running || paused;
        BtnStepInto.IsEnabled = MenuStepInto.IsEnabled = editing || paused;
        BtnStepOver.IsEnabled = MenuStepOver.IsEnabled = editing || paused;
        BtnStepOut.IsEnabled = MenuStepOut.IsEnabled = paused;
    }

    // ───────────────────────── 鍵盤快速鍵 ─────────────────────────

    private bool _chordCtrlE;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        RoutedEventArgs args = new();
        bool handled = true;

        // 組合鍵 Ctrl+E、D：格式化文件（第二個鍵按 D 或 Ctrl+D 皆可）。
        if (_chordCtrlE && key is not (Key.LeftCtrl or Key.RightCtrl))
        {
            _chordCtrlE = false;
            e.Handled = true;
            if (key == Key.D) FormatDocument();
            else StatusText.Text = "（Ctrl+E）不是有效的組合鍵";
            return;
        }
        if (key == Key.E && mods == ModifierKeys.Control)
        {
            _chordCtrlE = true;
            StatusText.Text = "已按下 Ctrl+E，等待第二個按鍵（D：格式化文件）…";
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.F5 when mods == ModifierKeys.Control: Run_Click(this, args); break;
            case Key.F5 when mods == ModifierKeys.Shift: Stop_Click(this, args); break;
            case Key.F5 when mods == ModifierKeys.None: DebugOrContinue_Click(this, args); break;
            case Key.F6: Build_Click(this, args); break;
            case Key.F9 when mods == (ModifierKeys.Control | ModifierKeys.Shift): ClearBreakpoints_Click(this, args); break;
            case Key.F9: ToggleBreakpoint_Click(this, args); break;
            case Key.F10: StepOver_Click(this, args); break;
            case Key.F11 when mods == ModifierKeys.Shift: StepOut_Click(this, args); break;
            case Key.F11: StepInto_Click(this, args); break;
            case Key.Cancel when mods.HasFlag(ModifierKeys.Control): Pause_Click(this, args); break;
            case Key.N when mods == ModifierKeys.Control: New_Click(this, args); break;
            case Key.O when mods == ModifierKeys.Control: Open_Click(this, args); break;
            case Key.S when mods == ModifierKeys.Control: Save_Click(this, args); break;
            case Key.Space or Key.J when mods == ModifierKeys.Control && Editor.TextArea.IsKeyboardFocusWithin && !Editor.IsReadOnly:
                _intelliSense.ShowCompletion(explicitInvoke: true);
                break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    // ───────────────────────── 檔案 ─────────────────────────

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "目前的檔案尚未儲存，要先儲存嗎？", "中文 C# 編輯器",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r switch
        {
            MessageBoxResult.Yes => SaveFile(false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void LoadText(string text, string? path)
    {
        _breakpoints.Clear();
        Editor.Text = text;
        Editor.ScrollToHome();
        _filePath = path;
        _dirty = false;
        UpdateTitle();
        UpdateTranslation();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_state != IdeState.編輯中 || !ConfirmDiscard()) return;
        LoadText("""
            使用之系統

            類別之程式
            {
                靜態之虛無之主程式()
                {
                    主控台的寫行("你好！")
                }
            }

            """, null);
    }

    private void Sample_Click(object sender, RoutedEventArgs e)
    {
        if (_state != IdeState.編輯中 || !ConfirmDiscard()) return;
        LoadText(Samples.HelloWorld, null);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_state != IdeState.編輯中 || !ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = FileFilter };
        if (dlg.ShowDialog(this) != true) return;
        LoadText(File.ReadAllText(dlg.FileName, Encoding.UTF8), dlg.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveFile(false);

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveFile(true);

    private bool SaveFile(bool saveAs)
    {
        var path = _filePath;
        if (saveAs || path is null)
        {
            var dlg = new SaveFileDialog { Filter = FileFilter, FileName = path is null ? "程式.ccs" : Path.GetFileName(path) };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        File.WriteAllText(path, Editor.Text, new UTF8Encoding(true));
        _filePath = path;
        _dirty = false;
        UpdateTitle();
        StatusText.Text = "已儲存：" + path;
        return true;
    }

    private void ImportCSharp_Click(object sender, RoutedEventArgs e)
    {
        if (_state != IdeState.編輯中 || !ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "C# 檔案 (*.cs)|*.cs|所有檔案 (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var zh = Translator.ToChinese(File.ReadAllText(dlg.FileName));
        LoadText(zh, null);
        _dirty = true;
        UpdateTitle();
    }

    private void ExportCSharp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "C# 檔案 (*.cs)|*.cs",
            FileName = (_filePath is null ? "Program" : Path.GetFileNameWithoutExtension(_filePath)) + ".cs",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, Translator.ToCSharp(Editor.Text).Code, new UTF8Encoding(true));
        StatusText.Text = "已匯出：" + dlg.FileName;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        _session?.Stop();
    }

    private void UpdateTitle()
    {
        var name = _filePath is null ? "未命名" : Path.GetFileName(_filePath);
        Title = $"{name}{(_dirty ? " *" : "")} — 中文 C# 編輯器";
    }

    // ───────────────────────── 其他 ─────────────────────────

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        Editor.Focus();
        ApplicationCommands.Find.Execute(null, Editor.TextArea);
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        Editor.Focus();
        _intelliSense.ShowCompletion(explicitInvoke: true);
    }

    private void ToggleCSharpPanel_Click(object sender, RoutedEventArgs e)
    {
        bool show = MenuShowCSharp.IsChecked;
        CSharpPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SplitterColumn.Width = new GridLength(show ? 5 : 0);
        CSharpColumn.Width = show ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
    }

    private void ErrorList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ErrorList.SelectedItem is not CompileDiagnostic d) return;
        int offset = Math.Clamp(d.SourceStart, 0, Editor.Document.TextLength);
        Editor.Focus();
        Editor.CaretOffset = offset;
        Editor.ScrollToLine(Editor.TextArea.Caret.Line);
        Editor.Select(offset, Math.Clamp(d.SourceLength, 0, Editor.Document.TextLength - offset));
    }

    private void Dictionary_Click(object sender, RoutedEventArgs e) => new DictionaryWindow { Owner = this }.Show();

    private void FormatDocument_Click(object sender, RoutedEventArgs e) => FormatDocument();

    private void HalfWidthToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool on = BtnHalfWidth.IsChecked == true;
        _intelliSense.ConvertFullWidthOnTyping = on;
        AppSettings.SetBool("TypingHalfWidth", on);
        StatusText.Text = on ? "半形輸入：全形符號會自動換成半形" : "全形與半形互通：打什麼就保留什麼";
    }

    private void GlueToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool glue = BtnGlue.IsChecked == true;
        AppSettings.SetBool("FormatGlue", glue);
        StatusText.Text = glue ? "格式化：黏著寫（c被指派0）" : "格式化：標準空白（c 被指派 0）";
    }

    /// <summary>美化文字格式：只調整縮排、空白、換行；整次變更可用一次 Ctrl+Z 復原。</summary>
    private void FormatDocument()
    {
        if (Editor.IsReadOnly) { StatusText.Text = "偵錯中無法格式化"; return; }
        try
        {
            var doc = Editor.Document;
            var newLine = doc.Text.Contains("\r\n") ? "\r\n" : "\n";
            bool glue = BtnGlue.IsChecked == true;
            var edits = CodeFormatter.ComputeEdits(doc.Text, newLine, glue: glue);
            if (edits.Count == 0) { StatusText.Text = "格式已經是整齊的"; return; }

            doc.BeginUpdate();
            try
            {
                foreach (var edit in edits.OrderByDescending(x => x.Start))
                    doc.Replace(edit.Start, edit.Length, edit.NewText);
            }
            finally
            {
                doc.EndUpdate();
            }
            StatusText.Text = $"已格式化文件（{edits.Count} 處變更，{(glue ? "黏著寫" : "標準空白")}）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "格式化失敗：" + ex.Message;
        }
    }

    // ───────────────────────── ctdll 轉換表 ─────────────────────────

    private IReadOnlyList<string> _userCtdllFiles = Array.Empty<string>();
    private string _userReferenceKey = "";

    /// <summary>原始碼檔案所在資料夾（解析 #參考 相對路徑用）。</summary>
    private string? BaseDirectory => _filePath is null ? null : Path.GetDirectoryName(_filePath);

    /// <summary>套用系統 ctdll（預先做好、隨程式附帶）+ 目前程式碼參考的使用者 dll 的 ctdll。</summary>
    private int ApplyCtdll() =>
        KeywordDictionary.ApplyGenerated(CtdllTables.LoadSystem().Concat(CtdllTables.LoadFiles(_userCtdllFiles)));

    /// <summary>
    /// 系統 ctdll 隨程式附帶（已預先做好）；只有缺少的組件才在背景補產生。
    /// update = true 時重新掃描全部系統組件並補上新名稱，既有的中文名稱保留。
    /// </summary>
    private async Task LoadCtdllAsync(bool update)
    {
        try
        {
            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var (created, applied) = await Task.Run(() =>
            {
                int n = CtdllTables.Generate(CodeCompiler.ReferenceList, update, progress);
                return (n, ApplyCtdll());
            });
            if (created > 0) Log($"已產生／更新 {created} 個 ctdll 檔 → {CtdllTables.Directory}");
            StatusText.Text = $"已載入 ctdll 轉換 {applied} 筆";
            UpdateTranslation();
            Editor.TextArea.TextView.Redraw();
        }
        catch (Exception ex)
        {
            Log("ctdll 轉換表處理失敗：" + ex.Message);
            StatusText.Text = "ctdll 轉換表處理失敗";
        }
    }

    /// <summary>程式碼的 #參考 有變動時：為使用者 dll 產生（或補充）ctdll 並套用。</summary>
    private void RefreshUserCtdll(TranslationResult translation)
    {
        var refs = CodeCompiler.ResolveUserReferences(translation, BaseDirectory);
        var key = string.Join("|", refs.Paths.Select(p => p + "@" + File.GetLastWriteTimeUtc(p).Ticks));
        if (key == _userReferenceKey) return;
        _userReferenceKey = key;

        Task.Run(() => refs.Paths
                .Select(dll => CtdllTables.EnsureUserCtdll(dll, CodeCompiler.ReferenceList))
                .OfType<string>().ToList())
            .ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    Log("使用者 dll 的 ctdll 產生失敗：" + t.Exception?.GetBaseException().Message);
                    return;
                }
                _userCtdllFiles = t.Result;
                int applied = ApplyCtdll();
                foreach (var f in t.Result) Log("已載入使用者 ctdll：" + f);
                StatusText.Text = $"已載入 ctdll 轉換 {applied} 筆";
                UpdateTranslation();
                Editor.TextArea.TextView.Redraw();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async void ReloadCtdll_Click(object sender, RoutedEventArgs e)
    {
        CtdllTables.InvalidateCache();
        _userReferenceKey = "";
        await LoadCtdllAsync(update: false);
    }

    private async void RegenerateCtdll_Click(object sender, RoutedEventArgs e) => await LoadCtdllAsync(update: true);

    /// <summary>選擇使用者 dll，在程式碼開頭（其他 #參考 之後）插入 #參考 "路徑"。</summary>
    private void AddReference_Click(object sender, RoutedEventArgs e)
    {
        if (_state != IdeState.編輯中) return;
        var dlg = new OpenFileDialog { Filter = "組件 (*.dll)|*.dll", Title = "加入參考 DLL" };
        if (dlg.ShowDialog(this) != true) return;

        var path = dlg.FileName;
        if (BaseDirectory is { } baseDir && Path.GetPathRoot(baseDir) == Path.GetPathRoot(path))
            path = Path.GetRelativePath(baseDir, path);

        var doc = Editor.Document;
        int insertLine = 1;
        foreach (var r in _translation.References) insertLine = Math.Max(insertLine, r.Line + 1);
        var offset = insertLine <= doc.LineCount ? doc.GetLineByNumber(insertLine).Offset : doc.TextLength;
        doc.Insert(offset, $"#參考 \"{path}\"{Environment.NewLine}");
        UpdateTranslation();
    }

    private void OpenCtdllFolder_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{CtdllTables.Directory}\"") { UseShellExecute = true });

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "中文 C# 編輯器\n\n" +
            "自然對人工語言轉換：以固定字典把中文關鍵字逐詞轉換為 C#，「的」直接轉換為「.」。\n" +
            "使用 Roslyn 編譯，於獨立主控台視窗執行；偵錯透過程式插樁支援中斷點、逐步執行、區域變數與呼叫堆疊。\n\n" +
            "提示：識別字前加 @ 可停用轉換，例如 @目的 不會被拆成「目.」。",
            "關於", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Log(string line) => OutputBox.AppendText(line + Environment.NewLine);

    private static string BuildRoot => Path.Combine(Path.GetTempPath(), "MulLangCSharp");

    private static void CleanupOldBuilds()
    {
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(BuildRoot)) return;
                foreach (var dir in Directory.GetDirectories(BuildRoot))
                {
                    try
                    {
                        if (Directory.GetCreationTime(dir) < DateTime.Now.AddHours(-1))
                            Directory.Delete(dir, true);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
        });
    }
}
