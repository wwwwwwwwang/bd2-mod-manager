using Avalonia.Controls;
using BD2ModManager.Core;

namespace BD2ModManager.Gui;

public partial class ModulesWindow : Window
{
    private readonly ModuleSettings _settings;
    private readonly StateStore _store;

    public bool Saved { get; private set; }

    public ModulesWindow() : this(new ModuleSettings(), new StateStore())
    {
    }

    public ModulesWindow(ModuleSettings settings, StateStore store)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        Apply();
    }

    private void Apply()
    {
        SkinUnlockCheck.IsChecked = _settings.SkinUnlock;
        InteractionsCheck.IsChecked = _settings.Interactions;
        CensorshipCheck.IsChecked = _settings.DisableCensorship;
        DiagnosticsCheck.IsChecked = _settings.Diagnostics;
        AutoUpdateCheck.IsChecked = _settings.AutoUpdate;
        CovenantCheck.IsChecked = _settings.CovenantExperimental;
        AllCostumesCheck.IsChecked = _settings.AllCostumes;
        AllCharactersCheck.IsChecked = _settings.AllCharacters;
        AllDatingCheck.IsChecked = _settings.AllDatingCostumes;
    }

    private async void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StatusText.Text = "";
        var names = new List<string>();
        if (CovenantCheck.IsChecked == true && !_settings.CovenantExperimental) names.Add("心契之约实验扩展");
        if (AllCostumesCheck.IsChecked == true && !_settings.AllCostumes) names.Add("全服装本地镜像");
        if (AllCharactersCheck.IsChecked == true && !_settings.AllCharacters) names.Add("全角色本地镜像");
        if (AllDatingCheck.IsChecked == true && !_settings.AllDatingCostumes) names.Add("全情缘服装本地镜像");

        if (names.Count > 0 && !await RiskConfirmation.ConfirmAsync(this, names))
            return;

        _settings.SkinUnlock = SkinUnlockCheck.IsChecked == true;
        _settings.Interactions = InteractionsCheck.IsChecked == true;
        _settings.DisableCensorship = CensorshipCheck.IsChecked == true;
        _settings.Diagnostics = DiagnosticsCheck.IsChecked == true;
        _settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        _settings.CovenantExperimental = CovenantCheck.IsChecked == true;
        _settings.AllCostumes = AllCostumesCheck.IsChecked == true;
        _settings.AllCharacters = AllCharactersCheck.IsChecked == true;
        _settings.AllDatingCostumes = AllDatingCheck.IsChecked == true;
        _settings.Save(_store);
        Saved = true;
        Close(true);
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Saved = false;
        Close(false);
    }
}
