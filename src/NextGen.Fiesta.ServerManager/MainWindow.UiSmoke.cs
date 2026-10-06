using System.Windows.Controls;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    internal TabControl? UiSmokeMainNavigation => _mainNavigation;

    internal void UiSmokeShowSettings()
        => ShowUtilityPage("Einstellungen", _settingsContent ?? CreatePlaceholder("Einstellungen nicht verfügbar."));

    internal void UiSmokeShowManual()
        => ShowUtilityPage("Handbuch", CreateManualContent());

    internal void UiSmokeShowCredits()
        => ShowUtilityPage("Credits", CreateCreditsContent());

    internal void UiSmokeHideUtility()
        => HideUtilityPage();
}
