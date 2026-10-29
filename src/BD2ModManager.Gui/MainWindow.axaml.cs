using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BD2ModManager.Gui;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private readonly DispatcherTimer _spinner;
    private double _spinnerAngle;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();

        _spinner = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render,
            (_, _) => TickSpinner());
        _spinner.Start();
    }

    private void TickSpinner()
    {
        if (LoadingRing.RenderTransform is not RotateTransform rotate) return;

        if (DataContext is not MainViewModel vm || !vm.IsBusy)
        {
            _spinnerAngle = 0;
            rotate.Angle = 0;
            return;
        }

        _spinnerAngle = (_spinnerAngle + 5) % 360;
        rotate.Angle = _spinnerAngle;
    }

    protected override void OnClosed(EventArgs e)
    {
        _spinner.Stop();
        base.OnClosed(e);
    }

    private void Hook()
    {
        if (_vm != null)
        {
            _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as MainViewModel;

        if (_vm != null)
        {
            _vm.LogLines.CollectionChanged += OnLogLinesChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LogsSheetOpen) || _vm?.LogsSheetOpen != true) return;
        Dispatcher.UIThread.Post(() => FindLogScroll()?.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 新日志进来就把列表滚到底。
    /// 不用 ScrollIntoView：它会一路冒泡，把抽屉外层也带着滚。
    /// 这里只动日志列表自己的滚动条。
    /// </summary>
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        FindLogScroll()?.ScrollToEnd();
    }

    private ScrollViewer? FindLogScroll() =>
        LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 标题栏里的按钮自己处理点击，不要把它们当成拖拽区域。
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null) return;

        BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void CopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        await clipboard.SetTextAsync(vm.GamePath);
    }
}
