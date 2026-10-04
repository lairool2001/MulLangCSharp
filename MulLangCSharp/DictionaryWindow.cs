using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MulLangCSharp.Language;

namespace MulLangCSharp;

/// <summary>關鍵字對照表（固定字典）。</summary>
public sealed class DictionaryWindow : Window
{
    public DictionaryWindow()
    {
        Title = "關鍵字對照表";
        Width = 620;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft JhengHei UI");
        FontSize = 14;

        var search = new TextBox { Margin = new Thickness(0, 0, 0, 6), ToolTip = "輸入中文或 C# 關鍵字篩選" };
        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            Text = "自然對人工語言轉換：以完整詞比對下表進行固定映射；例外是「的」，在程式碼中一律直接轉換為「.」"
                 + "（例如「主控台的寫行」→「Console.WriteLine」）；「之」代表空白（整數之c = 整數 c）；行尾的「;」可省略，轉換時自動補上。「被指派」「加上」「減掉」前後不需空格，「加一」「減一」可直接接在變數前後（加一c、c加一）。字串、字元與註解內容不轉換；"
                 + "全形標點（，；（）｛｝等）會轉為半形；識別字前加 @ 可停用轉換。函式庫名稱另由各組件的 ctdll 檔自動轉換（工具 → 開啟 ctdll 資料夾）。",
        };

        var view = CollectionViewSource.GetDefaultView(KeywordDictionary.Entries.Concat(KeywordDictionary.GeneratedEntries).ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WordEntry.Category)));
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            view.Filter = q.Length == 0 ? null : o => o is WordEntry w &&
                (w.Chinese.Contains(q) || w.CSharp.Contains(q, StringComparison.OrdinalIgnoreCase) || w.Note.Contains(q));
        };

        var grid = new DataGrid
        {
            ItemsSource = view,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "中文", Binding = new Binding(nameof(WordEntry.Chinese)), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "C#", Binding = new Binding(nameof(WordEntry.CSharp)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "說明", Binding = new Binding(nameof(WordEntry.Note)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var groupStyle = new GroupStyle();
        var headerTemplate = new DataTemplate();
        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        tb.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        tb.SetValue(TextBlock.MarginProperty, new Thickness(4, 8, 0, 2));
        headerTemplate.VisualTree = tb;
        groupStyle.HeaderTemplate = headerTemplate;
        grid.GroupStyle.Add(groupStyle);

        var dock = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(search);
        dock.Children.Add(grid);
        Content = dock;
    }
}
