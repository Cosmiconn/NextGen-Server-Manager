using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NextGen.Fiesta.ServerManager.ViewModels;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow : Window
{
    private TabControl? _mainNavigation;
    private ContentControl? _utilityOverlay;
    private ContentControl? _utilityContentHost;
    private TextBlock? _utilityTitle;
    private FrameworkElement? _settingsContent;

    private TextBlock? _runningZonesValue;
    private TextBlock? _clientSessionsValue;
    private TextBlock? _wmCpuValue;
    private TextBlock? _wmRamValue;
    private TextBlock? _highestLoadValue;
    private TextBlock? _capacityStatusValue;
    private TextBlock? _capacityStatusDetail;

    public MainWindow()
    {
        InitializeComponent();

        // The accepted target is a custom dark operations shell. Keep native resizing,
        // but draw the complete visible chrome ourselves so the UI matches the reference.
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Width = 1536;
        Height = 864;
        MinWidth = 1180;
        MinHeight = 720;

        DataContext = new MainViewModel();
        BuildTargetShell();

        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Builds the exact target shell while moving the proven 0.3.4 views instead of
    /// recreating their command bindings. The current screenshot is the visual source
    /// of truth; no legacy function is discarded simply because it is not visible in
    /// the screenshot.
    /// </summary>
    private void BuildTargetShell()
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

        legacyNavigation.Items.Clear();
        root.Children.Remove(legacyNavigation);

        // Settings is a global utility in the target. Preserve its exact legacy content
        // and re-parent only the content into a dedicated utility overlay.
        _settingsContent = tabs.Settings.Content as FrameworkElement;
        tabs.Settings.Content = null;

        // Replace the legacy shell rows, leaving only the content views that are moved
        // into the new navigation below.
        foreach (var child in root.Children.Cast<UIElement>()
                     .Where(child => Grid.GetRow(child) is 0 or 1 or 3)
                     .ToList())
        {
            root.Children.Remove(child);
        }

        root.Margin = new Thickness(0);
        root.Background = (Brush)FindResource("Bg");
        root.RowDefinitions[0].Height = GridLength.Auto;
        root.RowDefinitions[1].Height = GridLength.Auto;
        root.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        root.RowDefinitions[3].Height = GridLength.Auto;

        BuildZoneCapacityView(tabs.ZoneScaling);

        var mainNavigation = new TabControl
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(16, 0, 16, 0)
        };
        _mainNavigation = mainNavigation;
        Grid.SetRow(mainNavigation, 2);

        ConfigurePrimaryTab(
            tabs.Dashboard,
            "\uE80F",
            "Dashboard",
            "Übersicht & Status",
            showChevron: false);
        mainNavigation.Items.Add(tabs.Dashboard);

        var serverSubNavigation = CreateSubNavigation();
        ConfigureSecondaryTab(tabs.ZoneScaling, "▥", "Zone Auslastung / Scaling", 270);
        ConfigureSecondaryTab(tabs.Limits, "◔", "Limits / Capacity", 220);
        ConfigureSecondaryTab(tabs.AdaptiveHooks, "\uE713", "Adaptive Hooks", 215, useIconFont: true);
        serverSubNavigation.Items.Add(tabs.ZoneScaling);
        serverSubNavigation.Items.Add(tabs.Limits);
        serverSubNavigation.Items.Add(tabs.AdaptiveHooks);

        // Performance remains fully reachable without adding a fourth visible subtab,
        // because the accepted screenshot contains exactly three visible server tabs.
        ConfigureHiddenSecondaryTab(tabs.Performance);
        serverSubNavigation.Items.Add(tabs.Performance);

        var serverTab = new TabItem
        {
            Style = (Style)FindResource("PrimaryNavigationTab"),
            Content = serverSubNavigation
        };
        serverTab.Header = CreatePrimaryHeader(
            "▥",
            "Serverleistung",
            "Zonen · Ressourcen · Scaling",
            showChevron: true,
            chevronAction: button => ShowServerOverflow(button, serverSubNavigation, tabs.Performance));
        mainNavigation.Items.Add(serverTab);

        var diagnosticSubNavigation = CreateSubNavigation();
        ConfigureSecondaryTab(tabs.Logs, "\uE8A5", "Logs & Diagnose", 215, useIconFont: true);
        ConfigureSecondaryTab(tabs.LiveTimeline, "\uE823", "Live Timeline", 190, useIconFont: true);
        ConfigureSecondaryTab(tabs.Pdb, "PDB", "PDB / Symbole", 190);
        diagnosticSubNavigation.Items.Add(tabs.Logs);
        diagnosticSubNavigation.Items.Add(tabs.LiveTimeline);
        diagnosticSubNavigation.Items.Add(tabs.Pdb);
        var diagnosticTab = new TabItem
        {
            Header = CreatePrimaryHeader("\uE8A5", "Diagnostic", "Logs · Timeline · PDB", true, null, true),
            Style = (Style)FindResource("PrimaryNavigationTab"),
            Content = diagnosticSubNavigation
        };
        mainNavigation.Items.Add(diagnosticTab);

        var toolsSubNavigation = CreateSubNavigation();
        ConfigureSecondaryTab(tabs.ClientMapSafety, "\uE90F", "Client / Map Safety", 220, useIconFont: true);
        toolsSubNavigation.Items.Add(tabs.ClientMapSafety);
        foreach (var child in uncategorizedTabs)
        {
            child.Style = (Style)FindResource("SecondaryNavigationTab");
            toolsSubNavigation.Items.Add(child);
        }
        var toolsTab = new TabItem
        {
            Header = CreatePrimaryHeader("\uE90F", "Tools", "Spielfunktionen · DLL Hook · OPTool", true, null, true),
            Style = (Style)FindResource("PrimaryNavigationTab"),
            Content = toolsSubNavigation
        };
        mainNavigation.Items.Add(toolsTab);

        root.Children.Add(CreateHeader());
        root.Children.Add(CreateServerToolbar());
        root.Children.Add(mainNavigation);

        _utilityOverlay = new ContentControl
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(16, 0, 16, 0),
            Content = CreateUtilityOverlay()
        };
        Grid.SetRow(_utilityOverlay, 2);
        root.Children.Add(_utilityOverlay);

        root.Children.Add(CreateFooter());

        // The reference opens directly on Serverleistung > Zone Auslastung / Scaling.
        mainNavigation.SelectedItem = serverTab;
        serverSubNavigation.SelectedItem = tabs.ZoneScaling;

        SizeChanged += (_, _) => UpdatePrimaryTabWidths();
        Loaded += (_, _) => UpdatePrimaryTabWidths();

        if (DataContext is MainViewModel vm)
        {
            vm.ZoneCapacities.CollectionChanged += (_, _) => UpdateZoneSummaryCards(vm);
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateZoneSummaryCards(vm);
        }
    }

    private Border CreateHeader()
    {
        var header = new Border
        {
            Height = 88,
            Background = (Brush)FindResource("HeaderGradient"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 12, 20, 10)
        };
        Grid.SetRow(header, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

        var logo = CreateServerLogo();
        Grid.SetColumn(logo, 0);
        grid.Children.Add(logo);

        var titleArea = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(13, 0, 0, 0)
        };
        titleArea.Children.Add(new TextBlock
        {
            Text = "NextGen Fiesta Server Manager",
            Style = (Style)FindResource("HeaderTitle")
        });
        titleArea.Children.Add(new TextBlock
        {
            Text = "NA2016 Diagnose · Service Control · Smart Start · PDB/Symbolanalyse",
            Foreground = (Brush)FindResource("MutedStrong"),
            FontSize = 14,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(titleArea, 1);
        grid.Children.Add(titleArea);

        var healthCard = new Border
        {
            Width = 190,
            Height = 56,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = BrushFrom("#0B2033"),
            BorderBrush = (Brush)FindResource("BorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(13, 7, 12, 7),
            Margin = new Thickness(0, 6, 10, 0)
        };
        var healthGrid = new Grid();
        healthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        healthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        healthGrid.Children.Add(new Ellipse
        {
            Width = 15,
            Height = 15,
            Fill = (Brush)FindResource("Good"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        });
        var healthText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var healthTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        healthTitle.SetBinding(TextBlock.TextProperty, new Binding("HealthText"));
        healthText.Children.Add(healthTitle);
        var adminState = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)FindResource("Muted")
        };
        adminState.SetBinding(TextBlock.TextProperty, new Binding("AdminText"));
        healthText.Children.Add(adminState);
        Grid.SetColumn(healthText, 1);
        healthGrid.Children.Add(healthText);
        healthCard.Child = healthGrid;
        Grid.SetColumn(healthCard, 2);
        grid.Children.Add(healthCard);

        var elevatedButton = new Button
        {
            Width = 230,
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
            Content = CreateButtonContent("\uE7EF", "Als Administrator neu starten")
        };
        elevatedButton.SetBinding(Button.CommandProperty, new Binding("RunElevatedCommand"));
        Grid.SetColumn(elevatedButton, 3);
        grid.Children.Add(elevatedButton);

        var captionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -12, -20, 0)
        };
        Grid.SetColumn(captionButtons, 3);
        Grid.SetColumnSpan(captionButtons, 1);
        Panel.SetZIndex(captionButtons, 10);

        var minimize = new Button { Content = "—", Style = (Style)FindResource("WindowCaptionButton") };
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        var maximize = new Button { Content = "□", Style = (Style)FindResource("WindowCaptionButton") };
        maximize.Click += (_, _) => ToggleMaximize();
        var close = new Button { Content = "×", Style = (Style)FindResource("WindowCaptionButton") };
        close.Click += (_, _) => Close();
        captionButtons.Children.Add(minimize);
        captionButtons.Children.Add(maximize);
        captionButtons.Children.Add(close);
        grid.Children.Add(captionButtons);

        header.MouseLeftButtonDown += OnCustomHeaderMouseLeftButtonDown;
        header.Child = grid;
        return header;
    }

    private void OnCustomHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractiveHeaderSource(e.OriginalSource as DependencyObject))
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        try
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            DragMove();
            e.Handled = true;
        }
        catch
        {
            // Remote/pen input can change window state while a drag is already in progress.
        }
    }

    private static bool IsInteractiveHeaderSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetHeaderParent(current))
        {
            if (current is ButtonBase
                or TextBoxBase
                or ComboBox
                or Slider
                or ScrollBar)
                return true;
        }
        return false;
    }

    private static DependencyObject? GetHeaderParent(DependencyObject child)
    {
        if (child is FrameworkContentElement content)
            return content.Parent;
        try { return VisualTreeHelper.GetParent(child); }
        catch { return null; }
    }

    private FrameworkElement CreateServerLogo()
    {
        var outer = new Border
        {
            Width = 60,
            Height = 60,
            CornerRadius = new CornerRadius(6),
            Background = BrushFrom("#08284A"),
            BorderBrush = BrushFrom("#0B3D6C"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        for (var i = 0; i < 3; i++)
        {
            var rack = new Border
            {
                Width = 38,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Background = (Brush)FindResource("Accent"),
                Margin = new Thickness(0, 2, 0, 2)
            };
            var rackGrid = new Grid();
            rackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            rackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rackGrid.Children.Add(new Ellipse
            {
                Width = 3,
                Height = 3,
                Fill = BrushFrom("#06233B"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            var line = new Border
            {
                Height = 2,
                Width = 20,
                Background = BrushFrom("#06233B"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(line, 1);
            rackGrid.Children.Add(line);
            rack.Child = rackGrid;
            stack.Children.Add(rack);
        }
        outer.Child = stack;
        return outer;
    }

    private Border CreateServerToolbar()
    {
        var border = new Border
        {
            Style = (Style)FindResource("ToolbarBorder"),
            Margin = new Thickness(16, 10, 16, 10),
            Padding = new Thickness(8, 6, 8, 6)
        };
        Grid.SetRow(border, 1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

        var rootLabel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 8, 0)
        };
        rootLabel.Children.Add(new TextBlock
        {
            Text = "\uE8B7",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Foreground = (Brush)FindResource("Cyan"),
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        });
        rootLabel.Children.Add(new TextBlock
        {
            Text = "Server Root",
            FontSize = 12,
            Foreground = (Brush)FindResource("Text"),
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(rootLabel);

        var rootText = new TextBox
        {
            Margin = new Thickness(0, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        rootText.SetBinding(TextBox.TextProperty, new Binding("ServerRoot")
        {
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        Grid.SetColumn(rootText, 1);
        grid.Children.Add(rootText);

        var rootBrowseIcon = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE8B7",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16
            },
            Margin = new Thickness(0, 0, 5, 0),
            Padding = new Thickness(0)
        };
        rootBrowseIcon.SetBinding(Button.CommandProperty, new Binding("BrowseRootCommand"));
        Grid.SetColumn(rootBrowseIcon, 2);
        grid.Children.Add(rootBrowseIcon);

        var folderButton = CreateToolbarButton("\uE8B7", "Ordner…", "BrowseRootCommand");
        Grid.SetColumn(folderButton, 3);
        grid.Children.Add(folderButton);

        var scanButton = CreateToolbarButton("\uE721", "Scannen", "ScanCommand");
        Grid.SetColumn(scanButton, 4);
        grid.Children.Add(scanButton);

        var refreshButton = CreateToolbarButton("\uE72C", "Status aktualisieren", "RefreshCommand", primary: true);
        Grid.SetColumn(refreshButton, 5);
        grid.Children.Add(refreshButton);

        var settings = CreateUtilityToolbarButton("\uE713", "Einstellungen");
        settings.Click += (_, _) => ShowUtilityPage("Einstellungen", _settingsContent ?? CreatePlaceholder("Einstellungen nicht verfügbar."));
        Grid.SetColumn(settings, 7);
        grid.Children.Add(settings);

        var manual = CreateUtilityToolbarButton("\uE82D", "Handbuch");
        manual.Click += (_, _) => ShowUtilityPage("Handbuch", CreateManualContent());
        Grid.SetColumn(manual, 8);
        grid.Children.Add(manual);

        var credits = CreateUtilityToolbarButton("\uE946", "Credits", compact: true);
        credits.Click += (_, _) => ShowUtilityPage("Credits", CreateCreditsContent());
        Grid.SetColumn(credits, 9);
        grid.Children.Add(credits);

        border.Child = grid;
        return border;
    }

    private Button CreateToolbarButton(string glyph, string text, string commandPath, bool primary = false)
    {
        var button = new Button
        {
            Content = CreateButtonContent(glyph, text),
            Margin = new Thickness(3, 0, 3, 0),
            Style = primary
                ? (Style)FindResource("PrimaryActionButton")
                : (Style)FindResource(typeof(Button))
        };
        button.SetBinding(Button.CommandProperty, new Binding(commandPath));
        return button;
    }

    private Button CreateUtilityToolbarButton(string glyph, string text, bool compact = false)
        => new()
        {
            Content = CreateButtonContent(glyph, text, compact ? 11 : 12),
            Style = (Style)FindResource("UtilityButton"),
            Margin = new Thickness(2, 0, 2, 0),
            Padding = compact ? new Thickness(7, 6, 7, 6) : new Thickness(9, 6, 9, 6)
        };

    private static StackPanel CreateButtonContent(string glyph, string text, double fontSize = 12)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = fontSize + 3,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private TabControl CreateSubNavigation()
        => new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 10, 0, 0)
        };

    private void ConfigurePrimaryTab(TabItem tab, string glyph, string title, string subtitle, bool showChevron)
    {
        tab.Header = CreatePrimaryHeader(glyph, title, subtitle, showChevron, null, true);
        tab.Style = (Style)FindResource("PrimaryNavigationTab");
    }

    private void ConfigureSecondaryTab(TabItem tab, string glyph, string title, double width, bool useIconFont = false)
    {
        tab.Header = CreateSecondaryHeader(glyph, title, useIconFont);
        tab.Style = (Style)FindResource("SecondaryNavigationTab");
        tab.Width = width;
    }

    private void ConfigureHiddenSecondaryTab(TabItem tab)
    {
        tab.Header = string.Empty;
        tab.Style = (Style)FindResource("SecondaryNavigationTab");
        tab.Width = 0;
        tab.Height = 0;
        tab.Padding = new Thickness(0);
        tab.Margin = new Thickness(0);
        tab.Opacity = 0;
        tab.IsHitTestVisible = false;
    }

    private FrameworkElement CreatePrimaryHeader(
        string glyph,
        string title,
        string subtitle,
        bool showChevron,
        Action<Button>? chevronAction = null,
        bool useIconFont = false)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showChevron ? 28 : 0) });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = useIconFont ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Segoe UI Symbol"),
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#E6F2FF"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text")
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = (Brush)FindResource("MutedStrong"),
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (showChevron)
        {
            if (chevronAction is null)
            {
                var chevron = new TextBlock
                {
                    Text = "⌄",
                    FontSize = 18,
                    Foreground = (Brush)FindResource("MutedStrong"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsHitTestVisible = false
                };
                Grid.SetColumn(chevron, 2);
                grid.Children.Add(chevron);
            }
            else
            {
                var chevron = new Button
                {
                    Content = "⌄",
                    Style = (Style)FindResource("WindowCaptionButton"),
                    Width = 26,
                    Height = 26,
                    FontSize = 17
                };
                chevron.Click += (_, e) =>
                {
                    e.Handled = true;
                    chevronAction(chevron);
                };
                Grid.SetColumn(chevron, 2);
                grid.Children.Add(chevron);
            }
        }

        return grid;
    }

    private FrameworkElement CreateSecondaryHeader(string glyph, string title, bool useIconFont)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = useIconFont ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Segoe UI Symbol"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private void ShowServerOverflow(Button anchor, TabControl serverSubNavigation, TabItem performanceTab)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            Background = (Brush)FindResource("Panel2"),
            Foreground = (Brush)FindResource("Text"),
            BorderBrush = (Brush)FindResource("BorderStrong"),
            BorderThickness = new Thickness(1)
        };
        var performance = new MenuItem
        {
            Header = "Performance / Vertical Scaling",
            Foreground = (Brush)FindResource("Text"),
            Background = (Brush)FindResource("Panel2")
        };
        performance.Click += (_, _) => serverSubNavigation.SelectedItem = performanceTab;
        menu.Items.Add(performance);
        menu.IsOpen = true;
    }

    private void UpdatePrimaryTabWidths()
    {
        if (_mainNavigation is null || _mainNavigation.Items.Count < 4)
        {
            return;
        }

        var available = Math.Max(900, ActualWidth - 32);
        var width = Math.Max(220, (available - 15) / 4.0);
        for (var i = 0; i < 4; i++)
        {
            if (_mainNavigation.Items[i] is TabItem tab)
            {
                tab.Width = width;
            }
        }
    }

    private FrameworkElement CreateUtilityOverlay()
    {
        var grid = new Grid
        {
            Background = (Brush)FindResource("Bg")
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 10, 0, 8),
            Padding = new Thickness(12, 8, 12, 8)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _utilityTitle = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerGrid.Children.Add(_utilityTitle);
        var back = new Button { Content = "Zurück zur Hauptansicht", Padding = new Thickness(14, 7, 14, 7) };
        back.Click += (_, _) => HideUtilityPage();
        Grid.SetColumn(back, 1);
        headerGrid.Children.Add(back);
        header.Child = headerGrid;
        grid.Children.Add(header);

        _utilityContentHost = new ContentControl();
        Grid.SetRow(_utilityContentHost, 1);
        grid.Children.Add(_utilityContentHost);
        return grid;
    }

    private void ShowUtilityPage(string title, FrameworkElement content)
    {
        if (_mainNavigation is null || _utilityOverlay is null || _utilityContentHost is null || _utilityTitle is null)
        {
            return;
        }

        _utilityContentHost.Content = null;
        _utilityTitle.Text = title;
        _utilityContentHost.Content = content;
        _mainNavigation.Visibility = Visibility.Collapsed;
        _utilityOverlay.Visibility = Visibility.Visible;
    }

    private void HideUtilityPage()
    {
        if (_mainNavigation is null || _utilityOverlay is null || _utilityContentHost is null)
        {
            return;
        }

        _utilityContentHost.Content = null;
        _utilityOverlay.Visibility = Visibility.Collapsed;
        _mainNavigation.Visibility = Visibility.Visible;
    }

    private FrameworkElement CreateManualContent()
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(16),
            MaxWidth = 1100,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        stack.Children.Add(CreateInfoHeading("NextGen Server Manager – Handbuch"));
        stack.Children.Add(CreateInfoParagraph("Dashboard: Dienste prüfen und kontrolliert starten, stoppen, neu starten, Recovery setzen oder neu registrieren. Smart Start wartet auf echte WorldManager-Readiness, bevor Zonen gestaffelt gestartet werden."));
        stack.Children.Add(CreateInfoHeading("Serverleistung"));
        stack.Children.Add(CreateInfoParagraph("Zone Auslastung zeigt Client-Sessions, CPU-Core-Druck, privaten RAM, Map-/BlockInfo-Belegung und den sicheren Provisionierungsplan. Limits / Capacity dokumentiert verifizierte NA2016-Limits. Adaptive Hooks bleiben build-/hashgebunden und blockieren unvollständig verifizierte Zone-Pool-Rebases."));
        stack.Children.Add(CreateInfoHeading("Diagnostic"));
        stack.Children.Add(CreateInfoParagraph("Logs werden rekursiv in den Serviceordnern gefunden. Live Timeline korreliert Log-, Prozess- und Portereignisse. PDB / Symbole indexiert und durchsucht die verfügbaren Symbolinformationen."));
        stack.Children.Add(CreateInfoHeading("Tools"));
        stack.Children.Add(CreateInfoParagraph("Client / Map Safety trennt Terrain-, SHBD-/BlockInfo- und Gameplay-Koordinatenlimits. Weitere Spielfunktions-, DLL-Hook- und OPTool-Module werden in dieser Kategorie ergänzt."));
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private FrameworkElement CreateCreditsContent()
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(24),
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        stack.Children.Add(CreateInfoHeading("NextGen Fiesta Server Manager"));
        stack.Children.Add(CreateInfoParagraph("Native Windows-Verwaltung und Diagnose für den verifizierten Fiesta Online NA2016 Serverstack."));
        stack.Children.Add(CreateInfoParagraph("UI-Ziel: Dark-Blue/Charcoal Operations Dashboard · Baseline: 0.3.4 Hardware Aware · .NET 8 / WPF."));
        return stack;
    }

    private static TextBlock CreateInfoHeading(string text)
        => new()
        {
            Text = text,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 6)
        };

    private TextBlock CreateInfoParagraph(string text)
        => new()
        {
            Text = text,
            Foreground = (Brush)FindResource("MutedStrong"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 18)
        };

    private static FrameworkElement CreatePlaceholder(string text)
        => new TextBlock { Text = text, Margin = new Thickness(20) };

    private Border CreateFooter()
    {
        var footer = new Border
        {
            Background = BrushFrom("#081522"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 5, 16, 5),
            Margin = new Thickness(0)
        };
        Grid.SetRow(footer, 3);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock
        {
            Text = "\uE823",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Foreground = (Brush)FindResource("MutedStrong"),
            Margin = new Thickness(0, 0, 8, 0)
        });
        var status = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 11
        };
        status.SetBinding(TextBlock.TextProperty, new Binding("StatusLine"));
        left.Children.Add(status);
        grid.Children.Add(left);

        var right = new TextBlock
        {
            Text = "NextGen Fiesta Server Manager   |   NA2016",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 11
        };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        footer.Child = grid;
        return footer;
    }

    private void BuildZoneCapacityView(TabItem zoneTab)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var summary = CreateZoneSummaryHeader();
        Grid.SetRow(summary, 0);
        root.Children.Add(summary);

        var cards = new UniformGrid
        {
            Columns = 6,
            Margin = new Thickness(0, 0, 0, 8)
        };
        cards.Children.Add(CreateMetricCard("Laufende Zonen", "▤", BrushFrom("#16D97A"), out _runningZonesValue));
        cards.Children.Add(CreateMetricCard("Clients gesamt", "●●", BrushFrom("#168CFF"), out _clientSessionsValue));
        cards.Children.Add(CreateMetricCard("CPU (WorldManager)", "▣", BrushFrom("#14A9FF"), out _wmCpuValue));
        cards.Children.Add(CreateMetricCard("RAM (WorldManager)", "▥", BrushFrom("#A94DFF"), out _wmRamValue));
        cards.Children.Add(CreateMetricCard("Höchste Zonen-Auslastung", "▲", BrushFrom("#FF465C"), out _highestLoadValue));
        cards.Children.Add(CreateMetricCard("Status", "●", BrushFrom("#16D97A"), out _capacityStatusValue, out _capacityStatusDetail));
        Grid.SetRow(cards, 1);
        root.Children.Add(cards);

        var table = CreateZoneCapacityTable();
        Grid.SetRow(table, 2);
        root.Children.Add(table);

        var provision = CreateProvisioningPanel();
        Grid.SetRow(provision, 3);
        root.Children.Add(provision);

        zoneTab.Content = root;
    }

    private Border CreateZoneSummaryHeader()
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 9, 12, 9)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = "▥",
            Foreground = (Brush)FindResource("Cyan"),
            FontSize = 25,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        title.Children.Add(new TextBlock
        {
            Text = "Live-Zone-Kapazität",
            Style = (Style)FindResource("SectionTitle"),
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(title);
        left.Children.Add(CreateBoundMetaText("ZoneCapacitySummary", 2));
        left.Children.Add(CreateBoundMetaText("ScaleRecommendationText", 1, (Brush)FindResource("MutedStrong")));
        left.Children.Add(CreateBoundMetaText("WorldManagerCapacity.Summary", 1));
        left.Children.Add(CreateBoundMetaText("WorldManagerCapacity.Recommendation", 1));
        left.Children.Add(new TextBlock
        {
            Text = "Messwerte: etablierte TCP-Client-Sessions, CPU-Core-Druck, Privat-/Working-Set-RAM und konfigurierte Maps. Mob-/NPC-Poolbelegung wird nicht erfunden; Überlaufmeldungen aus Logs werden separat als Diagnose erfasst.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 20, 0)
        });
        grid.Children.Add(left);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(15, 3, 0, 0)
        };
        var refresh = new Button
        {
            Content = CreateButtonContent("\uE72C", "Auslastung aktualisieren"),
            Padding = new Thickness(14, 7, 14, 7)
        };
        refresh.SetBinding(Button.CommandProperty, new Binding("RefreshZoneCapacityCommand"));
        actions.Children.Add(refresh);
        var plan = new Button
        {
            Content = CreateButtonContent("\uE710", "Neue Zone planen"),
            Style = (Style)FindResource("PrimaryActionButton"),
            Padding = new Thickness(15, 7, 15, 7),
            Margin = new Thickness(7, 3, 0, 3)
        };
        plan.SetBinding(Button.CommandProperty, new Binding("PlanNewZoneCommand"));
        actions.Children.Add(plan);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        border.Child = grid;
        return border;
    }

    private TextBlock CreateBoundMetaText(string path, double topMargin, Brush? foreground = null)
    {
        var text = new TextBlock
        {
            Foreground = foreground ?? (Brush)FindResource("Muted"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(42, topMargin, 20, 0)
        };
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        return text;
    }

    private Border CreateMetricCard(string label, string icon, Brush accent, out TextBlock value)
        => CreateMetricCard(label, icon, accent, out value, out _);

    private Border CreateMetricCard(string label, string icon, Brush accent, out TextBlock value, out TextBlock? detail)
    {
        var card = new Border
        {
            Style = (Style)FindResource("SummaryCard"),
            Margin = new Thickness(0, 0, 7, 0),
            Height = 66
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new Border
        {
            Width = 4,
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-11, -9, 0, -9)
        });
        var iconText = new TextBlock
        {
            Text = icon,
            Foreground = accent,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(iconText, 1);
        grid.Children.Add(iconText);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("MutedStrong"),
            FontSize = 10
        });
        value = new TextBlock
        {
            Text = "–",
            Foreground = (Brush)FindResource("Text"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 1, 0, 0)
        };
        text.Children.Add(value);
        detail = new TextBlock
        {
            Text = string.Empty,
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 9,
            Margin = new Thickness(0, -1, 0, 0)
        };
        text.Children.Add(detail);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        card.Child = grid;
        return card;
    }

    private DataGrid CreateZoneCapacityTable()
    {
        var table = new DataGrid
        {
            AlternationCount = 2,
            Margin = new Thickness(0, 0, 0, 8),
            RowHeight = 32,
            ColumnHeaderHeight = 32
        };
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("ZoneCapacities"));

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
        rowStyle.Setters.Add(new Setter(DataGridRow.BorderBrushProperty, (Brush)FindResource("Stopped")));
        AddRowPressureTrigger(rowStyle, "KRITISCH", (Brush)FindResource("Bad"));
        AddRowPressureTrigger(rowStyle, "AUSBAU", (Brush)FindResource("Warn"));
        AddRowPressureTrigger(rowStyle, "WARNUNG", BrushFrom("#FFB21A"));
        AddRowPressureTrigger(rowStyle, "OK", (Brush)FindResource("Good"));
        table.RowStyle = rowStyle;

        table.Columns.Add(TextColumn("Zone", "ZoneName", 82));
        table.Columns.Add(PillColumn("Status", "State", 88, new StateBrushConverter()));
        table.Columns.Add(TextColumn("Clients", "ClientText", 95));
        table.Columns.Add(TextColumn("Client %", "ClientPercentText", 82));
        table.Columns.Add(TextColumn("CPU", "CpuText", 170));
        table.Columns.Add(TextColumn("RAM", "MemoryText", 180));
        table.Columns.Add(TextColumn("Maps / BlockInfo", "MapText", 126));
        table.Columns.Add(TextColumn("Gesamt", "OverallText", 76));
        table.Columns.Add(PillColumn("Druck", "Pressure", 94, new PressureBrushConverter()));
        table.Columns.Add(TextColumn("Trend", "Trend", 96));
        table.Columns.Add(TextColumn("Empfehlung", "Recommendation", new DataGridLength(1, DataGridLengthUnitType.Star)));
        table.Columns.Add(StaticTextColumn(string.Empty, "⋮", 34));
        return table;
    }

    private static void AddRowPressureTrigger(Style style, string value, Brush brush)
    {
        var trigger = new DataTrigger { Binding = new Binding("Pressure"), Value = value };
        trigger.Setters.Add(new Setter(DataGridRow.BorderBrushProperty, brush));
        style.Triggers.Add(trigger);
    }

    private static DataGridTextColumn TextColumn(string header, string path, double width)
        => TextColumn(header, path, new DataGridLength(width));

    private static DataGridTextColumn TextColumn(string header, string path, DataGridLength width)
        => new()
        {
            Header = header,
            Binding = new Binding(path),
            Width = width
        };

    private static DataGridTemplateColumn PillColumn(string header, string path, double width, IValueConverter converter)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(Border.PaddingProperty, new Thickness(7, 2, 7, 2));
        border.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.SetValue(Border.MinWidthProperty, 62d);
        border.SetBinding(Border.BackgroundProperty, new Binding(path) { Converter = converter });

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        text.SetValue(TextBlock.FontSizeProperty, 10d);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        border.AppendChild(text);

        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(width),
            CellTemplate = new DataTemplate { VisualTree = border }
        };
    }

    private static DataGridTemplateColumn StaticTextColumn(string header, string textValue, double width)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, textValue);
        text.SetValue(TextBlock.FontSizeProperty, 20d);
        text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(width),
            CellTemplate = new DataTemplate { VisualTree = text }
        };
    }

    private Border CreateProvisioningPanel()
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(10, 7, 10, 7)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        title.Children.Add(new TextBlock
        {
            Text = "\uE713",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Foreground = (Brush)FindResource("Cyan"),
            FontSize = 18,
            Margin = new Thickness(0, 0, 8, 0)
        });
        title.Children.Add(new TextBlock
        {
            Text = "Provisionierungsplan",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(title);

        var fields = new Grid();
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });

        AddProvisionField(fields, 0, "Neue Zone", "ProvisionPlan.ZoneName");
        AddProvisionField(fields, 1, "Template", "ProvisionPlan.SourceZoneName");
        AddProvisionField(fields, 2, "Ports", "ProvisionPlan.PortsText");
        AddProvisionField(fields, 3, "Status", "ProvisionPlan.ValidText");
        AddProvisionField(fields, 4, "Bereit", "ProvisionPlan.TargetDirectory");

        var openTarget = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE8B7",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15
            },
            Margin = new Thickness(3, 16, 3, 0),
            Padding = new Thickness(0)
        };
        openTarget.Click += (_, _) => OpenProvisionTarget();
        Grid.SetColumn(openTarget, 5);
        fields.Children.Add(openTarget);

        var create = new Button
        {
            Content = CreateButtonContent("\uE768", "Zone jetzt anlegen"),
            Style = (Style)FindResource("PrimaryActionButton"),
            Margin = new Thickness(8, 16, 0, 0),
            Padding = new Thickness(15, 8, 15, 8)
        };
        create.SetBinding(Button.CommandProperty, new Binding("CreateNewZoneCommand"));
        Grid.SetColumn(create, 6);
        fields.Children.Add(create);

        Grid.SetRow(fields, 1);
        root.Children.Add(fields);

        var status = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        status.SetBinding(TextBlock.TextProperty, new Binding("ProvisionStatus"));
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        border.Child = root;
        return border;
    }

    private void AddProvisionField(Grid grid, int column, string label, string path)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("MutedStrong"),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 2)
        });
        var field = new TextBox
        {
            IsReadOnly = true,
            IsTabStop = false,
            FontSize = 11,
            Padding = new Thickness(7, 4, 7, 4)
        };
        field.SetBinding(TextBox.TextProperty, new Binding(path));
        stack.Children.Add(field);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private void OpenProvisionTarget()
    {
        if (DataContext is not MainViewModel vm || vm.ProvisionPlan is null)
        {
            return;
        }

        var path = vm.ProvisionPlan.TargetDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Explorer launch is a convenience only; provisioning remains unaffected.
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm)
        {
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.WorldManagerCapacity) or nameof(MainViewModel.ZoneCapacitySummary))
        {
            UpdateZoneSummaryCards(vm);
        }
    }

    private void UpdateZoneSummaryCards(MainViewModel vm)
    {
        if (_runningZonesValue is null || _clientSessionsValue is null || _wmCpuValue is null ||
            _wmRamValue is null || _highestLoadValue is null || _capacityStatusValue is null ||
            _capacityStatusDetail is null)
        {
            return;
        }

        var zones = vm.ZoneCapacities.ToList();
        var running = zones.Count(x => string.Equals(x.State, "Running", StringComparison.OrdinalIgnoreCase));
        _runningZonesValue.Text = $"{running} / {zones.Count}";
        _clientSessionsValue.Text = $"{vm.WorldManagerCapacity.ClientSessions:N0} / {vm.WorldManagerCapacity.ClientLimit:N0}";
        _wmCpuValue.Text = $"{vm.WorldManagerCapacity.CpuCorePercent:F0}%";
        _wmRamValue.Text = $"{vm.WorldManagerCapacity.PrivateMemoryMb:F0} MB";

        var hottest = zones
            .Where(x => string.Equals(x.State, "Running", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.OverallPercent)
            .FirstOrDefault() ?? zones.OrderByDescending(x => x.OverallPercent).FirstOrDefault();

        _highestLoadValue.Text = hottest is null ? "–" : $"{hottest.OverallPercent:F0}% ({hottest.ZoneName})";
        _highestLoadValue.Foreground = hottest?.Pressure == "KRITISCH"
            ? (Brush)FindResource("Bad")
            : hottest?.Pressure is "WARNUNG" or "AUSBAU"
                ? (Brush)FindResource("Warn")
                : (Brush)FindResource("Text");

        if (zones.Any(x => x.Pressure == "KRITISCH"))
        {
            _capacityStatusValue.Text = "KRITISCH";
            _capacityStatusDetail.Text = "Handlungsbedarf";
            _capacityStatusValue.Foreground = (Brush)FindResource("Bad");
        }
        else if (zones.Any(x => x.Pressure is "AUSBAU" or "WARNUNG"))
        {
            _capacityStatusValue.Text = "WARNUNG";
            _capacityStatusDetail.Text = "Reserve prüfen";
            _capacityStatusValue.Foreground = (Brush)FindResource("Warn");
        }
        else
        {
            _capacityStatusValue.Text = "OK";
            _capacityStatusDetail.Text = "Genügend Reserve";
            _capacityStatusValue.Foreground = (Brush)FindResource("Good");
        }
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static Brush BrushFrom(string hex)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

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

    private sealed class PressureBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value?.ToString() switch
            {
                "KRITISCH" => BrushFrom("#B9223C"),
                "AUSBAU" => BrushFrom("#B96C08"),
                "WARNUNG" => BrushFrom("#B96C08"),
                "OK" => BrushFrom("#0A9356"),
                _ => BrushFrom("#506278")
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    private sealed class StateBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), "Running", StringComparison.OrdinalIgnoreCase)
                ? BrushFrom("#087E4A")
                : BrushFrom("#506278");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
