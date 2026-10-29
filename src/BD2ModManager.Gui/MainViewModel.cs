using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BD2ModManager.Core;

namespace BD2ModManager.Gui;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Window _window;
    private const int MaxLogLines = 500;

    private readonly StateStore _store = new();
    private readonly LoaderInstaller _installer;
    private readonly ComponentManager _components;

    private GameInstall? _game;
    private PackageHandle? _manualPackage;
    private TaskCompletionSource<bool>? _confirm;
    private DispatcherTimer? _watch;

    private bool _syncing;
    private bool _toggleOn;
    private bool _isBusy;
    private bool _hasGame;
    private bool _gameRunning;
    private bool _confirmVisible;
    private bool _showPickPackage;
    private bool _showCleanResidue;
    private bool _pathSheetOpen;
    private bool _logsSheetOpen;
    private bool _unreadLogs;
    private bool _updateAvailable;
    private bool _checkingUpdate;
    private string _updateStatus = "尚未检查更新";
    private string _releaseUrl = "";

    private string _busyText = "";
    private string _statusText = "尚未检测";
    private string _statusHint = "";
    private string _toggleHint = "";
    private string _confirmTitle = "";
    private string _confirmMessage = "";
    private string _lastWarnStamp = "";

    public MainViewModel(Window window)
    {
        _window = window;
        _installer = new LoaderInstaller(_store);
        _components = new ComponentManager(_store);

        RescanCommand = new RelayCommand(() => _ = RescanAsync());
        BrowseCommand = new RelayCommand(() => _ = BrowseAsync());
        PickPackageCommand = new RelayCommand(() => _ = PickPackageAsync());
        InstallComponentCommand = new RelayCommand(() => _ = InstallComponentAsync());
        InstallBuiltInCommand = new RelayCommand(() => _ = InstallBuiltInAsync());
        OpenGameFolderCommand = new RelayCommand(OpenGameFolder);
        CleanResidueCommand = new RelayCommand(() => _ = CleanResidueAsync());
        ClearLogCommand = new RelayCommand(ClearLogs);
        ToggleCommand = new RelayCommand(ToggleFromButton);
        OpenPathSheetCommand = new RelayCommand(OpenPathSheet);
        OpenLogsSheetCommand = new RelayCommand(OpenLogsSheet);
        CloseSheetCommand = new RelayCommand(CloseSheet);
        CheckUpdateCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(true));
        OpenUpdateCommand = new RelayCommand(OpenReleasePage);
        ConfirmYesCommand = new RelayCommand(() => _confirm?.TrySetResult(true));
        ConfirmCancelCommand = new RelayCommand(() => _confirm?.TrySetResult(false));

        StatusBrush = Brush.Parse("#9AA1AA");
        Version = ReadVersion();
    }

    // ------------------------------------------------------------ 绑定属性

    public string Version { get; }
    public string Title => "BD2ModManager";
    public string Subtitle => "Brown Dust 2 · 一键皮肤解锁";

    public ObservableCollection<PluginItem> Plugins { get; } = new();
    public ObservableCollection<ComponentItem> Components { get; } = new();

    public ICommand RescanCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand PickPackageCommand { get; }
    public ICommand InstallComponentCommand { get; }

    /// <summary>把打包时嵌进来的解锁插件装成组件，用户不用另外找文件。</summary>
    public ICommand InstallBuiltInCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand CleanResidueCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand OpenPathSheetCommand { get; }
    public ICommand OpenLogsSheetCommand { get; }
    public ICommand CloseSheetCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand OpenUpdateCommand { get; }
    public ICommand ConfirmYesCommand { get; }
    public ICommand ConfirmCancelCommand { get; }

    public string GamePath => _game?.Root ?? "尚未找到 Brown Dust 2";
    public string GameMeta => _game == null
        ? "支持官网启动器安装版"
        : $"Unity {_game.UnityVersion ?? "未识别"} · {_game.ExeName}";
    public bool HasGame => _hasGame;
    public bool HasNoPlugins => Plugins.Count == 0;
    public string PluginCountText => Plugins.Count == 0 ? "无" : $"{Plugins.Count} 个";

    public bool HasNoComponents => Components.Count == 0;
    public string ComponentCountText => Components.Count == 0 ? "无" : $"{Components.Count} 个";
    public bool CanManageComponents => HasGame && !IsBusy && !GameRunning && ToggleOn;

    public bool GameRunning
    {
        get => _gameRunning;
        private set
        {
            if (!Set(ref _gameRunning, value)) return;
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(CanManageComponents));
            OnPropertyChanged(nameof(CanCleanResidue));
        }
    }

    public bool ShowPickPackage
    {
        get => _showPickPackage;
        private set { if (Set(ref _showPickPackage, value)) OnPropertyChanged(nameof(CanToggle)); }
    }

    /// <summary>loader 核心已不在、只剩缓存日志时，露出「清理残留」按钮。</summary>
    public bool ShowCleanResidue
    {
        get => _showCleanResidue;
        private set
        {
            if (!Set(ref _showCleanResidue, value)) return;
            OnPropertyChanged(nameof(CanCleanResidue));
        }
    }

    public bool CanCleanResidue => ShowCleanResidue && HasGame && !IsBusy && !GameRunning;

    public bool PathSheetOpen
    {
        get => _pathSheetOpen;
        private set
        {
            if (!Set(ref _pathSheetOpen, value)) return;
            OnPropertyChanged(nameof(IsAnySheetOpen));
        }
    }

    public bool LogsSheetOpen
    {
        get => _logsSheetOpen;
        private set
        {
            if (!Set(ref _logsSheetOpen, value)) return;
            OnPropertyChanged(nameof(IsAnySheetOpen));
        }
    }

    public bool IsAnySheetOpen => PathSheetOpen || LogsSheetOpen;

    public bool UnreadLogs
    {
        get => _unreadLogs;
        private set => Set(ref _unreadLogs, value);
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => Set(ref _updateAvailable, value);
    }

    public bool IsCheckingUpdate
    {
        get => _checkingUpdate;
        private set => Set(ref _checkingUpdate, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    public string LogCountText => LogLines.Count.ToString();

    public string HeroTitle => IsBusy
        ? "正在同步组件"
        : ToggleOn ? "已启用 Mod 支持" : "未启用 · 原生纯净状态";

    public string HeroHint => IsBusy
        ? (string.IsNullOrWhiteSpace(BusyText) ? "正在准备…" : BusyText)
        : ToggleOn
            ? ""
            : "点击中央开关，一键安装解锁插件与 Loader";

    public bool ShowHeroHint => IsBusy || !ToggleOn;

    public string PowerLabel => IsBusy ? "处理中" : ToggleOn ? "已启用" : "启用";

    public IBrush PowerBackground => IsBusy
        ? Brush.Parse("#60A5FA")
        : ToggleOn ? Brush.Parse("#2563EB") : Brush.Parse("#E2E8F0");

    public IBrush PowerTextBrush => IsBusy || ToggleOn
        ? Brushes.White
        : Brush.Parse("#94A3B8");

    public IBrush HeroDotBrush => ToggleOn
        ? Brush.Parse("#2563EB")
        : Brush.Parse("#CBD5E1");

    /// <summary>日志按行存，交给 ListBox 显示——它的滚动最靠谱。</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    public string BusyText
    {
        get => _busyText;
        private set
        {
            if (Set(ref _busyText, value)) NotifyHeroState();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(CanManageComponents));
            OnPropertyChanged(nameof(CanCleanResidue));
            NotifyHeroState();
        }
    }

    private void NotifyHeroState()
    {
        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroHint));
        OnPropertyChanged(nameof(ShowHeroHint));
        OnPropertyChanged(nameof(PowerLabel));
        OnPropertyChanged(nameof(PowerBackground));
        OnPropertyChanged(nameof(PowerTextBrush));
        OnPropertyChanged(nameof(HeroDotBrush));
    }

    private void ToggleFromButton()
    {
        if (!CanToggle) return;
        ToggleOn = !ToggleOn;
    }

    private void OpenPathSheet()
    {
        LogsSheetOpen = false;
        PathSheetOpen = true;
    }

    private void OpenLogsSheet()
    {
        PathSheetOpen = false;
        LogsSheetOpen = true;
        UnreadLogs = false;
    }

    private void CloseSheet()
    {
        PathSheetOpen = false;
        LogsSheetOpen = false;
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsCheckingUpdate) return;

        IsCheckingUpdate = true;
        if (manual) UpdateStatus = "正在检查更新…";

        try
        {
            var result = await UpdateChecker.CheckAsync(Version, force: manual);
            if (!result.Success)
            {
                if (manual)
                {
                    UpdateStatus = "检查失败，请稍后重试";
                    Log($"检查更新失败：{result.Error}");
                }
                return;
            }

            UpdateAvailable = result.Available;
            _releaseUrl = result.ReleaseUrl;
            UpdateStatus = result.Available
                ? $"发现新版本 {result.LatestVersion}"
                : "已是最新版本";

            if (result.Available)
                Log($"发现新版本 {result.LatestVersion}：{result.ReleaseUrl}");
            else if (manual)
                Log("当前已是最新版本。");
        }
        catch (Exception ex)
        {
            if (manual)
            {
                UpdateStatus = "检查失败，请稍后重试";
                Log($"检查更新失败：{ex.Message}");
            }
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private void OpenReleasePage()
    {
        var url = string.IsNullOrWhiteSpace(_releaseUrl)
            ? "https://github.com/wwwwwwwwang/bd2-mod-manager/releases/latest"
            : _releaseUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开下载页失败：{ex.Message}");
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StatusHint { get => _statusHint; private set => Set(ref _statusHint, value); }
    public string ToggleHint { get => _toggleHint; private set => Set(ref _toggleHint, value); }
    public IBrush StatusBrush { get; private set; }

    public string ConfirmTitle { get => _confirmTitle; private set => Set(ref _confirmTitle, value); }
    public string ConfirmMessage { get => _confirmMessage; private set => Set(ref _confirmMessage, value); }

    public bool ConfirmVisible
    {
        get => _confirmVisible;
        private set => Set(ref _confirmVisible, value);
    }

    public bool ToggleOn
    {
        get => _toggleOn;
        set
        {
            if (!Set(ref _toggleOn, value)) return;
            OnPropertyChanged(nameof(CanManageComponents));
            NotifyHeroState();
            if (_syncing) return;
            _ = ApplyToggleAsync(value);
        }
    }

    public bool CanToggle => HasGame && !IsBusy && !GameRunning;

    // ------------------------------------------------------------ 启动

    public async Task InitializeAsync()
    {
        _watch = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => GameRunning = ProcessGuard.IsGameRunning());
        _watch.Start();

        Log($"BD2ModManager {Version} 启动");
        Log($"程序目录：{AppContext.BaseDirectory}");
        _ = CheckForUpdatesAsync(false);
        await RescanAsync(firstRun: true);
    }

    private async Task RescanAsync(bool firstRun = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "正在查找游戏安装位置…";
        try
        {
            GameInstall? found = null;

            // 高级用法：手动指定（便携版 / 非标准路径）
            var forced = Environment.GetEnvironmentVariable("BD2MM_GAME_ROOT");
            if (!string.IsNullOrWhiteSpace(forced))
            {
                found = GameLocator.TryBuild(forced!, "环境变量");
                if (found != null) Log($"按环境变量 BD2MM_GAME_ROOT 指定：{found.Root}");
                else Log($"环境变量 BD2MM_GAME_ROOT 无效：{forced}");
            }

            if (found == null)
            {
                var list = await Task.Run(() => new GameLocator().Find());
                if (list.Count > 0)
                {
                    found = list[0];
                    Log($"找到 {list.Count} 个安装，使用最近修改的：{found.Root}");
                    foreach (var g in list.Skip(1)) Log($"  另有：{g.Root}");
                }
            }

            _game = found;
            if (found == null && firstRun)
                Log("未找到游戏。可点「选择目录」手动指定包含 BrownDust II.exe 的文件夹。");
        }
        catch (Exception ex)
        {
            Log($"查找游戏失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            RefreshStatus();
            if (firstRun && _game == null) OpenPathSheet();
        }
    }

    // ------------------------------------------------------------ 状态刷新

    private void RefreshStatus()
    {
        var hadGame = HasGame;
        _hasGame = _game != null;
        if (hadGame != _hasGame) OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(GamePath));
        OnPropertyChanged(nameof(GameMeta));

        if (_game == null)
        {
            _syncing = true;
            ToggleOn = false;
            _syncing = false;

            StatusText = "未找到游戏";
            StatusHint = "点「选择目录」指定安装位置";
            ToggleHint = "找到游戏后才能启用";
            StatusBrush = Brush.Parse("#D97706");
            ShowCleanResidue = false;
            Plugins.Clear();
            Components.Clear();
            NotifyPlugins();
            NotifyHeroState();
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(CanManageComponents));
            return;
        }

        var st = LoaderInspector.Inspect(_game, _store);

        _syncing = true;
        // 残留态虽然目录还在，但核心已删、游戏根本不会加载任何插件，
        // 这时候开关必须是关的，否则用户会以为 MOD 还开着。
        ToggleOn = st.CorePresent;
        _syncing = false;

        if (!st.CorePresent)
        {
            // 没有 loader 核心，游戏不会加载任何插件。三种情况分开说，免得用户以为没清干净。
            if (st.ResidueOnly)
            {
                StatusText = "仅剩残留";
                StatusHint = "loader 已移除，只剩缓存与日志";
                ToggleHint = "打开开关会重新安装 loader 和内置皮肤解锁插件。";
            }
            else if (st.Plugins.Count > 0)
            {
                // 缓存日志都清完了，plugins 里还有插件——那些不是本工具的产物，删不掉也不该删
                StatusText = "未启用";
                // 这里不写数量：组件区与插件区是分开显示的，写总数会和下面的列表对不上
                StatusHint = "plugins 里还有插件，loader 还没装，游戏不会加载";
                ToggleHint = "打开后会安装 loader 和内置皮肤解锁插件，现有插件也会随之生效。";
            }
            else
            {
                StatusText = "未启用";
                StatusHint = "游戏目录干净，可以安全安装";
                ToggleHint = "打开会安装 BepInEx 和内置皮肤解锁插件；关闭会完整还原。";
            }
            StatusBrush = Brush.Parse("#9AA1AA");
        }
        else if (st.ManagedByUs)
        {
            var door = string.IsNullOrWhiteSpace(st.DoorstopVersion) ? "" : $" · Doorstop {st.DoorstopVersion}";
            StatusText = "已启用";
            StatusHint = $"BepInEx {st.BepInExVersion ?? "未知"}{door} · 本工具安装";
            ToggleHint = "关闭会移除内置解锁插件和本工具安装的 loader，并还原被覆盖的原文件。";
            StatusBrush = Brush.Parse("#16A34A");
        }
        else
        {
            StatusText = "已存在（他人安装）";
            StatusHint = "检测到来源不明的 BepInEx，工具不会擅自删除";
            ToggleHint = "可以覆盖安装（原文件会备份），但卸载只能还原本工具写入的部分。";
            StatusBrush = Brush.Parse("#D97706");
        }

        ShowCleanResidue = st.ResidueOnly;

        // 切开关会反复刷新状态，同样的提醒只写一次
        var warnStamp = string.Join("\n", st.Warnings);
        if (warnStamp != _lastWarnStamp)
        {
            _lastWarnStamp = warnStamp;
            foreach (var w in st.Warnings) Log($"提醒：{w}");
        }

        // 组件：本工具安装、可整包开关与移除的第三方插件
        var comps = _components.List(_game);
        Components.Clear();
        foreach (var c in comps) Components.Add(new ComponentItem(this, c, st.CorePresent));

        // 散装插件列表：组件内的 dll 由组件那一段管，这里不再重复出现
        var pluginsDir = Path.Combine(_game.Root, "BepInEx", "plugins");
        Plugins.Clear();
        foreach (var p in st.Plugins)
        {
            if (ComponentManager.IsInside(pluginsDir, p.DllPath, comps)) continue;
            Plugins.Add(new PluginItem(this, p, st.CorePresent));
        }
        NotifyPlugins();

        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanManageComponents));
        OnPropertyChanged(nameof(StatusBrush));
        NotifyHeroState();
    }

    private void NotifyPlugins()
    {
        OnPropertyChanged(nameof(HasNoPlugins));
        OnPropertyChanged(nameof(PluginCountText));
        OnPropertyChanged(nameof(HasNoComponents));
        OnPropertyChanged(nameof(ComponentCountText));
    }

    // ------------------------------------------------------------ 安装 / 卸载

    private async Task ApplyToggleAsync(bool on)
    {
        if (_game == null) { RefreshStatus(); return; }
        if (on) await InstallAsync();
        else await UninstallAsync();
    }

    private async Task InstallAsync()
    {
        var game = _game!;

        if (ProcessGuard.IsGameRunning())
        {
            Log("游戏正在运行，请先完全退出游戏。");
            GameRunning = true;
            RefreshStatus();
            return;
        }

        var st = LoaderInspector.Inspect(game, _store);
        var force = false;

        if (!st.CorePresent)
        {
            // 没有 loader 核心：残留也好、只剩插件也好，都不是一份能用的 BepInEx，
            // 不该拿「他人安装」去吓用户，直接覆盖装一份完整的
            force = true;
        }
        else if (st.Present && !st.ManagedByUs)
        {
            var ok = await ConfirmAsync("目录里已有 BepInEx",
                "检测到一份不是本工具安装的 BepInEx。继续会覆盖其中的同名文件（原文件先备份，关闭开关时还原）。是否继续？");
            if (!ok) { RefreshStatus(); return; }
            force = true;
        }
        else if (st.Present && st.ManagedByUs)
        {
            force = true;
        }

        var pkg = ResolvePackage();
        if (pkg == null)
        {
            ShowPickPackage = true;
            Log("没有找到加载器包。把 bd2-loader.zip 放到本程序旁边，或点「选择加载器包」。");
            RefreshStatus();
            return;
        }

        IsBusy = true;
        BusyText = $"正在安装 BepInEx {pkg.Version ?? ""}…";
        try
        {
            var dir = pkg.Directory;
            var r = await Task.Run(() => _installer.Install(game, dir, force));
            WriteResult(r);
            if (r.Success)
            {
                ShowPickPackage = false;
                BusyText = "正在安装内置解锁插件…";
                var pluginOk = await InstallBuiltInCoreAsync(game);
                if (pluginOk)
                    Log("已启用 Mod 支持，并安装内置皮肤解锁插件。启动游戏即可生效。");
                else
                    Log("Mod 支持已启用，但内置解锁插件安装失败；请关闭再打开开关重试。");
            }
            else
            {
                Log("安装未完成，游戏目录已回滚到操作前的状态。");
            }
        }
        catch (Exception ex)
        {
            Log($"安装出错：{ex.Message}");
        }
        finally
        {
            pkg.Dispose();
            IsBusy = false;
            BusyText = "";
            RefreshStatus();
        }
    }

    private async Task UninstallAsync()
    {
        var game = _game!;
        var st = LoaderInspector.Inspect(game, _store);

        if (!st.Present)
        {
            Log("未检测到 BepInEx，无需还原。");
            RefreshStatus();
            return;
        }

        if (!st.ManagedByUs)
        {
            // 核心早就没了，剩下的缓存日志可以顺手清掉，不必让用户去手工删目录
            if (st.ResidueOnly) { await CleanResidueCoreAsync(game, askConfirm: true); return; }

            await ConfirmAsync("无法自动还原",
                "这份 BepInEx 不是本工具安装的，工具不会删除来源不明的文件。\n\n如需彻底移除，请手工删除 winhttp.dll、doorstop_config.ini、.doorstop_version 和 BepInEx 目录。");
            RefreshStatus();
            return;
        }

        if (ProcessGuard.IsGameRunning())
        {
            Log("游戏正在运行，请先完全退出游戏。");
            GameRunning = true;
            RefreshStatus();
            return;
        }

        var ok = await ConfirmAsync("还原游戏目录",
            "将删除本工具写入的文件，并把被覆盖的原文件还原回去。游戏目录会回到安装前的状态。");
        if (!ok) { RefreshStatus(); return; }

        IsBusy = true;
        BusyText = "正在还原…";
        try
        {
            var r = await Task.Run(() => _installer.Uninstall(game));
            WriteResult(r);
        }
        catch (Exception ex)
        {
            Log($"还原出错：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            RefreshStatus();
        }
    }

    // ------------------------------------------------------------ 残留清理

    /// <summary>「清理残留」按钮：只清缓存与日志，不动 plugins 里的插件。</summary>
    private Task CleanResidueAsync()
    {
        if (_game == null) { RefreshStatus(); return Task.CompletedTask; }
        return CleanResidueCoreAsync(_game, askConfirm: true);
    }

    private async Task CleanResidueCoreAsync(GameInstall game, bool askConfirm)
    {
        if (ProcessGuard.IsGameRunning())
        {
            Log("游戏正在运行，请先完全退出游戏再清理。");
            GameRunning = true;
            RefreshStatus();
            return;
        }

        if (askConfirm)
        {
            var ok = await ConfirmAsync("清理残留",
                "loader 的核心文件已经不在（winhttp.dll / BepInEx/core 都缺），游戏不会加载任何插件。\n\n" +
                "这次只删 BepInEx 缓存、日志和 loader 自带的两份配置；plugins 目录里的插件会原样保留。");
            if (!ok) { RefreshStatus(); return; }
        }

        IsBusy = true;
        BusyText = "正在清理残留…";
        try
        {
            var r = await Task.Run(() => _installer.CleanResidue(game));
            WriteResult(r);
        }
        catch (Exception ex)
        {
            Log($"清理出错：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            RefreshStatus();
        }
    }

    public async Task SetPluginEnabledAsync(PluginItem item, bool enabled)
    {
        var game = _game;
        if (game == null) { item.SetSilently(!enabled); return; }

        try
        {
            var r = await Task.Run(() => _installer.SetPluginEnabled(game.Root, item.DllPath, enabled));
            WriteResult(r);
            if (r.Success) item.SyncPathAfterToggle(enabled);
            else item.SetSilently(!enabled);
        }
        catch (Exception ex)
        {
            Log($"切换插件失败：{ex.Message}");
            item.SetSilently(!enabled);
        }
    }

    // ------------------------------------------------------------ 组件

    private async Task InstallComponentAsync()
    {
        if (_game == null) { Log("还没有找到游戏。"); return; }
        if (!ToggleOn) { Log("请先打开「启用 Mod 支持」，再安装组件。"); return; }
        if (ProcessGuard.IsGameRunning())
        {
            Log("游戏正在运行，请先完全退出游戏。");
            GameRunning = true;
            return;
        }

        var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择插件目录（含 .dll；也可选解压后 BepInEx 的上一层）",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        IsBusy = true;
        BusyText = "正在安装组件…";
        try
        {
            var game = _game;
            var r = await Task.Run(() => _components.Install(game, path));
            WriteResult(r);
        }
        catch (Exception ex)
        {
            Log($"安装组件出错：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            RefreshStatus();
        }
    }

    private async Task InstallBuiltInAsync()
    {
        if (_game == null) { Log("还没有找到游戏。"); return; }
        if (!ToggleOn) { Log("请先打开「启用 Mod 支持」，再安装内置插件。"); return; }
        if (ProcessGuard.IsGameRunning())
        {
            Log("游戏正在运行，请先完全退出游戏。");
            GameRunning = true;
            return;
        }

        IsBusy = true;
        BusyText = "正在安装内置插件…";
        try
        {
            await InstallBuiltInCoreAsync(_game);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            RefreshStatus();
        }
    }

    /// <summary>安装内嵌的 BD2SkinUnlock.dll；调用方负责 IsBusy / BusyText。</summary>
    private async Task<bool> InstallBuiltInCoreAsync(GameInstall game)
    {
        byte[]? payload;
        try
        {
            payload = ReadEmbedded("BD2SkinUnlock.dll");
        }
        catch (Exception ex)
        {
            Log($"读取内置插件失败：{ex.Message}");
            return false;
        }

        if (payload == null)
        {
            Log("这个构建缺少内置解锁插件，请使用官方 Release 的 GUI，或自行构建。");
            return false;
        }

        var staging = Path.Combine(Path.GetTempPath(), "bd2mm-builtin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, "BD2SkinUnlock.dll"), payload);

            var r = await Task.Run(() => _components.Install(game, staging));
            WriteResult(r);
            return r.Success;
        }
        catch (Exception ex)
        {
            Log($"安装内置插件出错：{ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }
    }

    private static byte[]? ReadEmbedded(string name)
    {
        var assembly = typeof(MainViewModel).Assembly;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream == null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public async Task SetComponentEnabledAsync(ComponentItem item, bool enabled)
    {
        var game = _game;
        if (game == null) { item.SetSilently(!enabled); return; }

        try
        {
            var r = await Task.Run(() => _components.SetEnabled(game, item.Name, enabled));
            WriteResult(r);
            if (!r.Success) item.SetSilently(!enabled);
        }
        catch (Exception ex)
        {
            Log($"切换组件失败：{ex.Message}");
            item.SetSilently(!enabled);
        }
    }

    public async Task RemoveComponentAsync(ComponentItem item)
    {
        var game = _game;
        if (game == null) return;

        if (ProcessGuard.IsGameRunning())
        {
            Log("游戏正在运行，请先完全退出游戏。");
            GameRunning = true;
            return;
        }

        var ok = await ConfirmAsync($"移除组件「{item.Name}」",
            "会删除本工具安装的这些文件（被改动过的文件会保留）。下次启动游戏就不再加载它。");
        if (!ok) return;

        IsBusy = true;
        BusyText = $"正在移除 {item.Name}…";
        try
        {
            var r = await Task.Run(() => _components.Remove(game, item.Name));
            WriteResult(r);
        }
        catch (Exception ex)
        {
            Log($"移除组件出错：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            RefreshStatus();
        }
    }

    // ------------------------------------------------------------ 选择目录 / 包

    private async Task BrowseAsync()
    {
        var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 Brown Dust 2 安装目录（里面有 BrownDust II.exe）",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var g = GameLocator.TryBuild(path, "手动指定")
                ?? GameLocator.ScanForGame(path, 2).FirstOrDefault();

        if (g == null)
        {
            Log("这个目录里没有找到 BrownDust II.exe 和 BrownDust II_Data，请选到游戏主目录。");
            return;
        }

        _game = g;
        Log($"已指定游戏目录：{g.Root}");
        RefreshStatus();
    }

    private async Task PickPackageAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择加载器包（bd2-loader.zip）",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("加载器包") { Patterns = new[] { "*.zip" } }
            }
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        _manualPackage?.Dispose();
        _manualPackage = LoaderPackage.OpenPath(path);
        if (!_manualPackage.IsValid)
        {
            Log($"这个包不能用：{_manualPackage.Problem}");
            _manualPackage.Dispose();
            _manualPackage = null;
            return;
        }

        Log($"已选择加载器包：{Path.GetFileName(path)}（loader {_manualPackage.Version}）");
        ShowPickPackage = false;
    }

    private PackageHandle? ResolvePackage()
    {
        if (_manualPackage is { IsValid: true }) return _manualPackage;

        var h = LoaderPackage.AutoLocate(AppContext.BaseDirectory, typeof(MainViewModel).Assembly);
        if (h != null)
        {
            Log($"加载器包：内嵌 / 随程序附带（loader {h.Version}）");
            return h;
        }
        return null;
    }

    private void OpenGameFolder()
    {
        if (_game == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _game.Root,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"打开目录失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------ 确认框

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        _confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmVisible = true;

        var ok = await _confirm.Task;
        ConfirmVisible = false;
        _confirm = null;
        return ok;
    }

    // ------------------------------------------------------------ 日志

    private void Log(string line)
    {
        AppendLogLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    }

    private void AppendLogLine(string line)
    {
        LogLines.Add(line);
        // 只留最近若干行，避免长时间运行后列表无限增长
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        OnPropertyChanged(nameof(LogCountText));
        if (!LogsSheetOpen) UnreadLogs = true;
    }

    private void ClearLogs()
    {
        LogLines.Clear();
        UnreadLogs = false;
        OnPropertyChanged(nameof(LogCountText));
    }

    private void WriteResult(OpResult r)
    {
        foreach (var l in r.Log) AppendLogLine("  · " + l);
        foreach (var e in r.Errors) AppendLogLine("  ✗ " + e);
        Log(r.Success ? "完成。" : "失败。");
    }



    // ------------------------------------------------------------ 基础设施

    private static string ReadVersion()
    {
        var asm = typeof(MainViewModel).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info!.IndexOf('+');
            if (plus > 0) info = info[..plus];
            return "v" + info;
        }
        return "v" + (asm.GetName().Version?.ToString(3) ?? "0.0.0");
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
