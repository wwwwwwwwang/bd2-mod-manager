using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace BD2ModManager.Gui;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
    }

    private void Hook()
    {
        if (_vm != null) _vm.LogLines.CollectionChanged -= OnLogLinesChanged;

        _vm = DataContext as MainViewModel;

        if (_vm != null) _vm.LogLines.CollectionChanged += OnLogLinesChanged;
    }

    /// <summary>
    /// 新日志进来就把列表滚到底。
    /// 不用 ScrollIntoView：它会一路冒泡，把外面那层整页 ScrollViewer 也带着滚，
    /// 结果整页往上跳、卡片标题被切掉。这里只动日志列表自己的滚动条。
    /// </summary>
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        var sv = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        sv?.ScrollToEnd();
    }
}
