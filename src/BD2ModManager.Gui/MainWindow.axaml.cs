using System.ComponentModel;
using Avalonia.Controls;

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

    /// <summary>日志更新后把光标推到末尾，实现自动滚动到底部。</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LogText)) return;
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }
}
