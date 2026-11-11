using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BD2ModManager.Core;

namespace BD2ModManager.Gui;

public partial class DisclaimerWindow : Window
{
    private readonly ModuleSettings _settings;
    private readonly StateStore _store = new();

    public bool Accepted { get; private set; }

    public DisclaimerWindow() : this(new ModuleSettings())
    {
    }

    public DisclaimerWindow(ModuleSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ApplySettings();
    }

    private void ApplySettings()
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

    private async void OnAgree(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StatusText.Text = "";
        if (AgreeCheck.IsChecked != true)
        {
            StatusText.Text = "请先勾选“我已阅读并同意上述免责声明与风险提示”。";
            return;
        }

        var newRisk = (CovenantCheck.IsChecked == true && !_settings.CovenantExperimental)
            || (AllCostumesCheck.IsChecked == true && !_settings.AllCostumes)
            || (AllCharactersCheck.IsChecked == true && !_settings.AllCharacters)
            || (AllDatingCheck.IsChecked == true && !_settings.AllDatingCostumes);

        if (newRisk && !await ConfirmRiskAsync())
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
        _settings.DisclaimerAccepted = true;
        _settings.DisclaimerVersion = ModuleSettings.CurrentVersion;
        _settings.Save(_store);

        Accepted = true;
        Close();
    }

    private void OnExit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }

    private async Task<bool> ConfirmRiskAsync()
    {
        var names = new List<string>();
        if (CovenantCheck.IsChecked == true && !_settings.CovenantExperimental) names.Add("心契之约实验扩展");
        if (AllCostumesCheck.IsChecked == true && !_settings.AllCostumes) names.Add("全服装本地镜像");
        if (AllCharactersCheck.IsChecked == true && !_settings.AllCharacters) names.Add("全角色本地镜像");
        if (AllDatingCheck.IsChecked == true && !_settings.AllDatingCostumes) names.Add("全情缘服装本地镜像");

        return await RiskConfirmation.ConfirmAsync(this, names);
    }
}
