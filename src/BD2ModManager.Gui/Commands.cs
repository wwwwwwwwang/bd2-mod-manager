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

/// <summary>组件列表里的一行：整包开关 + 整包移除。</summary>
public sealed class ComponentItem : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly int _fileCount;
    private readonly long _size;
    private readonly bool _loaderReady;
    private bool _loading;
    private bool _enabled;

    internal ComponentItem(MainViewModel owner, ComponentInfo info, bool loaderReady)
    {
        _owner = owner;
        _loaderReady = loaderReady;
        Name = info.Name;
        Source = info.Source;
        Missing = info.Missing;
        _fileCount = info.FileCount;
        _size = info.Size;
        _enabled = info.Enabled;
        RemoveCommand = new RelayCommand(() => _ = _owner.RemoveComponentAsync(this));
    }

    public string Name { get; }
    public string Source { get; }
    public bool Missing { get; }
    public ICommand RemoveCommand { get; }

    public string Meta
    {
        get
        {
            var head = $"{_fileCount} 个文件 · {_size / 1024.0:F0} KB";
            if (Missing) return head + " · 文件已不在磁盘上";
            if (!_enabled) return head + " · 已禁用，不加载";
            return _loaderReady ? head + " · 运行中加载" : head + " · 已启用，但 loader 未安装";
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value || _loading) return;
            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Meta));
            _ = _owner.SetComponentEnabledAsync(this, value);
        }
    }

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

/// <summary>插件列表里的一行。开关切换直接落到文件改名上。</summary>
public sealed class PluginItem : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly bool _loaderReady;
    private bool _loading;
    private bool _enabled;

    internal PluginItem(MainViewModel owner, PluginEntry entry, bool loaderReady)
    {
        _owner = owner;
        _loaderReady = loaderReady;
        DllPath = entry.DllPath;
        Name = Path.GetFileName(entry.DllPath);
        if (Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            Name = Name[..^".disabled".Length];
        Size = entry.Size;
        _enabled = entry.Enabled;
    }

    public string DllPath { get; private set; }
    public string Name { get; }
    public long Size { get; }

    /// <summary>
    /// 开关就是改文件名，成功后必须把路径一起改过来。
    /// 否则下一次点击会拿旧路径判断，直接报「该插件当前已是禁用/启用状态」。
    /// 这里只改路径、不重建列表——重建会让控件绑定重新挂一遍，反而点不动了。
    /// </summary>
    internal void SyncPathAfterToggle(bool enabled)
    {
        const string Suffix = ".disabled";
        if (enabled)
        {
            if (DllPath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                DllPath = DllPath[..^Suffix.Length];
        }
        else if (!DllPath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            DllPath += Suffix;
        }
    }

    /// <summary>
    /// 只描述「这个文件会不会被加载」，光有 loader 还不够：
    /// loader 核心不在时，启用也只是把文件名改回来，游戏照样不认。
    /// </summary>
    public string Meta
    {
        get
        {
            var head = $"{Size / 1024.0:F0} KB";
            if (!_enabled) return head + " · 已禁用，不加载";
            return _loaderReady ? head + " · 运行中加载" : head + " · 已启用，但 loader 未安装";
        }
    }

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
