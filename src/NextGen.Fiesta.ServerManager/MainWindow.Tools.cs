using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<MainWindow, object> TargetTools = new();
    private static readonly bool ToolsLoadedHookRegistered = RegisterToolsLoadedHook();

    private static bool RegisterToolsLoadedHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnToolsWindowLoaded));
        return true;
    }

    private static void OnToolsWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || TargetTools.TryGetValue(window, out _))
            return;

        var mainNavigation = window._mainNavigation;
        if (mainNavigation is null || mainNavigation.Items.Count < 4 ||
            mainNavigation.Items[3] is not TabItem toolsTab ||
            toolsTab.Content is not TabControl toolsNavigation ||
            toolsNavigation.Items.Count == 0 ||
            toolsNavigation.Items[0] is not TabItem clientMapSafety)
            return;

        TargetTools.Add(window, new object());
        window.BuildClientMapSafetyTargetView(clientMapSafety);
    }

    private void BuildClientMapSafetyTargetView(TabItem tab)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.82, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.18, GridUnitType.Star) });

        root.Children.Add(CreateDiagnosticHeader(
            "\uE7C3",
            "NA2016 Client / Map Safety",
            "Terrainformat, SHBD/BlockInfo und Gameplay-/Netzwerkkoordinaten getrennt auf Stock-Sicherheit prüfen",
            ("\uE8B7", "Fiesta.bin auswählen", "BrowseClientBinaryCommand", false),
            ("\uE72C", "Client neu analysieren", "AnalyzeClientTerrainCommand", true)));

        var info = CreateClientInfoCard();
        Grid.SetRow(info, 1);
        root.Children.Add(info);

        var profiles = CreateModuleTableCard("Terrain-Größenprofile", "ClientTerrainProfiles", table =>
        {
            table.Columns.Add(ModuleTextColumn("Terrain-Quads", "TerrainText", 105));
            table.Columns.Add(ModuleTextColumn("Heightmap-Punkte", "HeightMapText", 125));
            table.Columns.Add(ModuleTextColumn("Weltseite @50", "WorldText", 145));
            table.Columns.Add(ModuleTextColumn("Chunks @64", "ChunkCount", 85));
            table.Columns.Add(ModuleTextColumn("3 Height-Puffer", "RawHeightMemoryText", 105));
            table.Columns.Add(ModuleTextColumn("HTD ~", "HtdSizeText", 85));
            table.Columns.Add(ModuleTextColumn("SHBD ~", "ShbdSizeText", 85));
            table.Columns.Add(ModuleTextColumn("Collision", "CollisionText", 125));
            table.Columns.Add(ModuleTextColumn("≤ signed16", "Signed16Text", 85));
            table.Columns.Add(ModuleTextColumn("Risiko", "Risk", 95));
            table.Columns.Add(ModuleTextColumn("Einordnung", "Recommendation", new DataGridLength(1, DataGridLengthUnitType.Star)));
        }, "ClientTerrainSummary");
        Grid.SetRow(profiles, 2);
        root.Children.Add(profiles);

        var maps = CreateModuleTableCard("Tatsächlich erkannte Client-HeightMaps (resmap/**/*.ini)", "ClientTerrainMaps", table =>
        {
            table.Columns.Add(ModuleTextColumn("Map", "MapName", 130));
            table.Columns.Add(ModuleTextColumn("Height-Punkte", "PointsText", 115));
            table.Columns.Add(ModuleTextColumn("Terrain-Quads", "QuadsText", 110));
            table.Columns.Add(ModuleTextColumn("Block W/H", "BlockText", 95));
            table.Columns.Add(ModuleTextColumn("Weltgröße", "WorldText", 135));
            table.Columns.Add(ModuleTextColumn("Chunks", "ChunkText", 95));
            table.Columns.Add(ModuleTextColumn("Risiko", "Risk", 90));
            table.Columns.Add(ModuleTextColumn("Einordnung", "Notes", 230));
            table.Columns.Add(ModuleTextColumn("Pfad", "RelativePath", new DataGridLength(1, DataGridLengthUnitType.Star)));
        });
        maps.Margin = new Thickness(0);
        Grid.SetRow(maps, 3);
        root.Children.Add(maps);
        tab.Content = root;
    }

    private Border CreateClientInfoCard()
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var root = new StackPanel();

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "Client-Baseline & Loader-Evidenz",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        var summary = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 650
        };
        summary.SetBinding(TextBlock.TextProperty, new Binding("ClientTerrainSummary"));
        Grid.SetColumn(summary, 1);
        heading.Children.Add(summary);
        root.Children.Add(heading);

        var metrics = new WrapPanel();
        metrics.Children.Add(CreateClientInfoTile("Client", "ClientTerrainInfo.BinaryName", 150));
        metrics.Children.Add(CreateClientInfoTile("PE", "ClientTerrainInfo.PeKind", 120));
        metrics.Children.Add(CreateClientInfoTile("LargeAddressAware", "ClientTerrainInfo.LaaText", 155));
        metrics.Children.Add(CreateClientInfoTile("SHA-256", "ClientTerrainInfo.HashShort", 180));
        metrics.Children.Add(CreateClientInfoTile("Baseline", "ClientTerrainInfo.BaselineText", 180));
        metrics.Children.Add(CreateClientInfoTile("Terrain-Signaturen", "ClientTerrainInfo.SignatureText", 185));
        root.Children.Add(metrics);

        root.Children.Add(CreateClientEvidenceRow("Pfad", "ClientTerrainInfo.ClientBinaryPath", false));
        root.Children.Add(CreateClientEvidenceRow("Binary-Evidenz", "ClientTerrainInfo.StaticLoaderEvidence", true));
        root.Children.Add(CreateClientEvidenceRow("Kompatibilität", "ClientTerrainInfo.CompatibilityAssessment", true));

        border.Child = root;
        return border;
    }

    private Border CreateClientInfoTile(string label, string path, double width)
    {
        var tile = new Border
        {
            Background = (Brush)FindResource("Panel2"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 0, 6, 6),
            Width = width,
            MinHeight = 46
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 9
        });
        var value = new TextBlock
        {
            Foreground = (Brush)FindResource("Text"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        value.SetBinding(TextBlock.TextProperty, new Binding(path) { TargetNullValue = "–", FallbackValue = "–" });
        stack.Children.Add(value);
        tile.Child = stack;
        return tile;
    }

    private FrameworkElement CreateClientEvidenceRow(string label, string path, bool muted)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 8, 0)
        });
        var value = new TextBlock
        {
            Foreground = (Brush)FindResource(muted ? "MutedStrong" : "Text"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        };
        value.SetBinding(TextBlock.TextProperty, new Binding(path) { TargetNullValue = "–", FallbackValue = "–" });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }
}
