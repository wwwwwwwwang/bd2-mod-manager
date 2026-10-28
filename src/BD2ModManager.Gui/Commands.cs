using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BD2ModManager.Core;

namespace BD2ModManager.Gui;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>插件列表里的一行。开关切换直接落到文件改名上。</summary>
public sealed class PluginItem : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private bool _loading;
    private bool _enabled;

    internal PluginItem(MainViewModel owner, PluginEntry entry)
    {
        _owner = owner;
        DllPath = entry.DllPath;
        Name = Path.GetFileName(entry.DllPath);
        if (Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            Name = Name[..^".disabled".Length];
        Size = entry.Size;
        _enabled = entry.Enabled;
    }

    public string DllPath { get; }
    public string Name { get; }
    public long Size { get; }

    public string Meta => $"{Size / 1024.0:F0} KB · {(Enabled ? "运行中加载" : "已禁用，不加载")}";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value || _loading) return;
            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Meta));
            _ = _owner.SetPluginEnabledAsync(this, value);
        }
    }

    /// <summary>程序内部改状态时用，不触发写盘。</summary>
    internal void SetSilently(bool value)
    {
        _loading = true;
        _enabled = value;
        _loading = false;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Meta));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
