using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
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
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>
    /// 日志更新后滚到底部。
    /// 注意：不能靠设 CaretIndex 来实现——那会让日志框抢走焦点，
    /// 之后所有开关的点击都会被吞掉（0.2 起就有的老问题，0.4.2 才查出来）。
    /// 这里只动滚动条，不碰光标与选区。
    /// </summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LogText)) return;

        // 等这一轮绑定更新完再滚，否则拿到的是旧内容高度
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.GetVisualDescendants()
                  .OfType<ScrollViewer>()
                  .FirstOrDefault()?
                  .ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}
