using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NextGen.Fiesta.ServerManager.ViewModels;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BuildNavigationShell();
        DataContext = new MainViewModel();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Re-groups the proven 0.3.4 views into the scalable main/sub-navigation from
    /// docs/UI_TARGET.md without recreating view content or command bindings.
    ///
    /// The legacy TabItems themselves are moved, not copied. That deliberately keeps
    /// every existing Binding, InputBinding and Command instance in the XAML intact.
    /// If the expected baseline views cannot be found, the legacy navigation is left
    /// untouched instead of risking a partially inaccessible UI.
    /// </summary>
    private void BuildNavigationShell()
    {
        if (Content is not Grid root)
        {
            return;
        }

        var legacyNavigation = root.Children
            .OfType<TabControl>()
            .FirstOrDefault(tab => Grid.GetRow(tab) == 2);

        if (legacyNavigation is null)
        {
            return;
        }

        var legacyTabs = legacyNavigation.Items.OfType<TabItem>().ToList();
        var byHeader = legacyTabs
            .Where(tab => tab.Header is not null)
            .GroupBy(tab => tab.Header?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (!TryGetRequiredTabs(byHeader, out var tabs))
        {
            return;
        }

        var knownTabs = new HashSet<TabItem>(tabs.All);
        var uncategorizedTabs = legacyTabs.Where(tab => !knownTabs.Contains(tab)).ToList();

        // Detach first so no TabItem ever has two logical parents.
        legacyNavigation.Items.Clear();
        root.Children.Remove(legacyNavigation);

        var mainNavigation = new TabControl
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0)
        };
        Grid.SetRow(mainNavigation, 2);

        ConfigurePrimaryTab(
            tabs.Dashboard,
            "▦",
            "Dashboard",
            "Übersicht & Status");
        mainNavigation.Items.Add(tabs.Dashboard);

        mainNavigation.Items.Add(CreateCategoryTab(
            "▥",
            "Serverleistung",
            "Scaling, Performance & Limits",
            tabs.ZoneScaling,
            tabs.Performance,
            tabs.Limits,
            tabs.AdaptiveHooks));

        mainNavigation.Items.Add(CreateCategoryTab(
            "◎",
            "Diagnostic",
            "Logs, Timeline & Symbole",
            tabs.Logs,
            tabs.LiveTimeline,
            tabs.Pdb));

        var toolTabs = new List<TabItem> { tabs.ClientMapSafety };
        // Future/parallel legacy tabs stay reachable until they receive an explicit
        // category in the preservation matrix.
        toolTabs.AddRange(uncategorizedTabs);
        mainNavigation.Items.Add(CreateCategoryTab(
            "◇",
            "Tools",
            "Client, Maps & Erweiterungen",
            toolTabs.ToArray()));

        // Transitional utility placement: visibly smaller than the four main
        // categories, while the existing Settings/Hints view remains fully reachable.
        tabs.Settings.Header = "Einstellungen";
        tabs.Settings.Style = (Style)FindResource("SecondaryNavigationTab");
        tabs.Settings.ToolTip = "Sicherheit, Smart Start und Betriebshinweise";
        mainNavigation.Items.Add(tabs.Settings);

        root.Children.Add(mainNavigation);
    }

    private TabItem CreateCategoryTab(string glyph, string title, string subtitle, params TabItem[] childTabs)
    {
        var subNavigation = new TabControl
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 6, 0, 0)
        };

        foreach (var child in childTabs)
        {
            child.Style = (Style)FindResource("SecondaryNavigationTab");
            subNavigation.Items.Add(child);
        }

        return new TabItem
        {
            Header = CreatePrimaryHeader(glyph, title, subtitle),
            Style = (Style)FindResource("PrimaryNavigationTab"),
            Content = subNavigation
        };
    }

    private void ConfigurePrimaryTab(TabItem tab, string glyph, string title, string subtitle)
    {
        tab.Header = CreatePrimaryHeader(glyph, title, subtitle);
        tab.Style = (Style)FindResource("PrimaryNavigationTab");
    }

    private static FrameworkElement CreatePrimaryHeader(string glyph, string title, string subtitle)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        header.Children.Add(new TextBlock
        {
            Text = glyph,
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Opacity = 0.82,
            Margin = new Thickness(0, 1, 0, 0)
        });
        header.Children.Add(text);

        return header;
    }

    private static bool TryGetRequiredTabs(
        IReadOnlyDictionary<string, TabItem> tabs,
        out NavigationTabs result)
    {
        static bool Get(IReadOnlyDictionary<string, TabItem> source, string name, out TabItem tab)
            => source.TryGetValue(name, out tab!);

        if (Get(tabs, "Dashboard", out var dashboard) &&
            Get(tabs, "Zone Auslastung / Scaling", out var zoneScaling) &&
            Get(tabs, "Performance / Vertical Scaling", out var performance) &&
            Get(tabs, "Limits / Capacity", out var limits) &&
            Get(tabs, "Adaptive Hooks", out var adaptiveHooks) &&
            Get(tabs, "Logs & Diagnose", out var logs) &&
            Get(tabs, "Live Timeline", out var liveTimeline) &&
            Get(tabs, "PDB / Symbole", out var pdb) &&
            Get(tabs, "Client / Map Safety", out var clientMapSafety) &&
            Get(tabs, "Einstellungen / Hinweise", out var settings))
        {
            result = new NavigationTabs(
                dashboard,
                zoneScaling,
                performance,
                limits,
                adaptiveHooks,
                logs,
                liveTimeline,
                pdb,
                clientMapSafety,
                settings);
            return true;
        }

        result = null!;
        return false;
    }

    private sealed record NavigationTabs(
        TabItem Dashboard,
        TabItem ZoneScaling,
        TabItem Performance,
        TabItem Limits,
        TabItem AdaptiveHooks,
        TabItem Logs,
        TabItem LiveTimeline,
        TabItem Pdb,
        TabItem ClientMapSafety,
        TabItem Settings)
    {
        public IEnumerable<TabItem> All =>
        [
            Dashboard,
            ZoneScaling,
            Performance,
            Limits,
            AdaptiveHooks,
            Logs,
            LiveTimeline,
            Pdb,
            ClientMapSafety,
            Settings
        ];
    }
}
