using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BD2ModManager.Gui;

public partial class MainWindow : Window
{
    /// <summary>日志每一行的行高，要和 XAML 里 TextBlock 的 LineHeight 保持一致。</summary>
    private const double LogLineHeight = 18;

    private MainViewModel? _vm;
    private bool _logHeightSnapped;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();

        // 等窗口真正布好局再量，否则拿到的是布局前的尺寸，会把高度改小
        Opened += (_, _) => Dispatcher.UIThread.Post(SnapLogListHeight, DispatcherPriority.Loaded);
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

        FindLogScroll()?.ScrollToEnd();
    }

    private ScrollViewer? FindLogScroll() =>
        LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>
    /// 兜底：把日志列表高度修成行高的整数倍。
    ///
    /// 正常情况下 XAML 里 Height 已经是 5×18，视口高度就是 90，这里算出来是 0 不会动。
    /// 留着是为了防止以后有人给 ListBox 加边框或内边距——那些会算进滚动区域，
    /// 让视口变成 101.33 这种非整数倍，滚到底时上下各露半行。
    /// 只修一次：高度与视口之差是常数。
    /// </summary>
    private void SnapLogListHeight()
    {
        if (_logHeightSnapped) return;

        var sv = FindLogScroll();
        if (sv == null) return;

        var viewport = sv.Viewport.Height;
        if (viewport <= LogLineHeight)
        {
            // 还没量出来，下一轮再说
            Dispatcher.UIThread.Post(SnapLogListHeight, DispatcherPriority.Background);
            return;
        }

        var target = Math.Floor(viewport / LogLineHeight) * LogLineHeight;
        var delta = target - viewport;
        if (Math.Abs(delta) > 0.01) LogList.Height += delta;
        _logHeightSnapped = true;
    }
}
