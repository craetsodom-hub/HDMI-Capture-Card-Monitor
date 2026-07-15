using System.Windows.Media;
using System.Xml.Linq;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Presentation;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor.Tests;

public sealed class PhaseThreeUiTests
{
    private static readonly string[] RequiredThemeKeys =
    [
        "WindowBackgroundBrush", "SurfaceBrush", "SurfaceSecondaryBrush", "PreviewBrush",
        "PrimaryTextBrush", "MutedTextBrush", "AccentBrush", "BorderBrush", "FocusBrush",
        "StatusIdleBrush", "SuccessBrush", "WarningBrush", "DangerBrush",
        "DisabledSurfaceBrush", "DisabledTextBrush", "DisabledBorderBrush"
    ];

    [Theory]
    [InlineData(1, ApplicationTheme.Light)]
    [InlineData(0, ApplicationTheme.Dark)]
    [InlineData(null, ApplicationTheme.Light)]
    public void SystemAppearancePreferenceResolvesSafely(int? appsUseLightTheme, ApplicationTheme expected) =>
        Assert.Equal(expected, ApplicationThemeManager.ResolveTheme(appsUseLightTheme));

    [Theory]
    [InlineData(ApplicationThemeManager.LightThemePath)]
    [InlineData(ApplicationThemeManager.DarkThemePath)]
    public void ThemeDictionariesContainEveryRequiredSemanticBrush(string themePath)
    {
        var dictionary = LoadTheme(themePath);

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var key in RequiredThemeKeys)
        {
            Assert.Contains(dictionary.Descendants(), element =>
                element.Name.LocalName == nameof(SolidColorBrush) &&
                string.Equals((string?)element.Attribute(x + "Key"), key, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(ApplicationThemeManager.LightThemePath)]
    [InlineData(ApplicationThemeManager.DarkThemePath)]
    public void ThemeTextContrastMeetsReadableThresholds(string themePath)
    {
        var dictionary = LoadTheme(themePath);
        var surface = ColorValue(dictionary, "SurfaceColor");
        var primary = ColorValue(dictionary, "PrimaryTextColor");
        var muted = ColorValue(dictionary, "MutedTextColor");
        var disabledSurface = ColorValue(dictionary, "DisabledSurfaceColor");
        var disabledText = ColorValue(dictionary, "DisabledTextColor");

        Assert.True(Contrast(primary, surface) >= 4.5);
        Assert.True(Contrast(muted, surface) >= 4.5);
        Assert.True(Contrast(disabledText, disabledSurface) >= 3.0);
    }

    [Theory]
    [InlineData(719, true)]
    [InlineData(899, true)]
    [InlineData(900, false)]
    [InlineData(1180, false)]
    [InlineData(double.NaN, false)]
    public void ResponsivePolicyHasDeterministicNarrowLayout(double width, bool expected) =>
        Assert.Equal(expected, ResponsiveLayoutPolicy.UsesStackedSelectors(width));

    [Fact]
    public void AutomationLabelsAreSpecificUniqueAndProductionFacing()
    {
        string[] labels =
        [
            UiAutomationLabels.MainWindow, UiAutomationLabels.PreviewArea,
            UiAutomationLabels.DeviceSelector, UiAutomationLabels.FormatSelector,
            UiAutomationLabels.RefreshDevices, UiAutomationLabels.StartStopPreview,
            UiAutomationLabels.Settings, UiAutomationLabels.Help,
            UiAutomationLabels.FullscreenUpcoming, UiAutomationLabels.SnapshotUpcoming,
            UiAutomationLabels.RecordUpcoming
        ];

        Assert.All(labels, label =>
        {
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.DoesNotContain("Phase", label, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SelectorItemsExposeOnlySafeCustomerFacingNames()
    {
        var root = FindRepositoryRoot();
        var window = XDocument.Load(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "MainWindow.xaml"));
        var automationNames = window.Descendants()
            .Where(element => element.Name.LocalName == "Setter" &&
                              string.Equals((string?)element.Attribute("Property"), "AutomationProperties.Name", StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Value"))
            .OfType<string>()
            .ToArray();

        Assert.Contains("{Binding DisplayName}", automationNames);
        Assert.Contains("{Binding DisplayLabel}", automationNames);
        Assert.DoesNotContain("NativeMediaTypeIndex", window.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Phase", window.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FutureFeatureControlsRemainUnavailableAndHonest()
    {
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance);

        Assert.False(viewModel.CanFullscreen);
        Assert.False(viewModel.CanTakeSnapshot);
        Assert.False(viewModel.CanRecord);
        Assert.Equal("_Start", viewModel.StartStopAccessText);
        Assert.Contains("upcoming feature", UiAutomationLabels.RecordUpcoming, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InformationPanelsContainNoFakeSettingsOrPhaseLanguage()
    {
        var viewModel = new MainWindowViewModel(NullApplicationLogger.Instance);

        viewModel.ShowSettingsInformationCommand.Execute(null);
        Assert.True(viewModel.IsInformationDialogOpen);
        Assert.DoesNotContain("Phase", JoinDialog(viewModel), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no placeholder switches", viewModel.InformationDialogDescription, StringComparison.OrdinalIgnoreCase);

        viewModel.CloseInformationDialogCommand.Execute(null);
        viewModel.ShowHelpInformationCommand.Execute(null);
        Assert.DoesNotContain("Phase", JoinDialog(viewModel), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physical USB HDMI", viewModel.InformationDialogDetails, StringComparison.OrdinalIgnoreCase);

        viewModel.CloseInformationDialogCommand.Execute(null);
        Assert.False(viewModel.IsInformationDialogOpen);
    }

    [Fact]
    public void WindowMarkupDeclaresModalMainContentAndVisibleUpcomingGrouping()
    {
        var root = FindRepositoryRoot();
        var window = XDocument.Load(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var mainContent = window.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), "MainContent", StringComparison.Ordinal));

        Assert.Equal("{Binding IsMainContentEnabled}", (string?)mainContent.Attribute("IsEnabled"));
        var upcomingActions = window.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), "UpcomingActions", StringComparison.Ordinal));
        var upcomingLabel = upcomingActions.Elements().Single(element =>
            element.Name.LocalName == "TextBlock" &&
            string.Equals((string?)element.Attribute("Text"), "UPCOMING", StringComparison.Ordinal));

        Assert.Equal("Center", (string?)upcomingLabel.Attribute("TextAlignment"));
        Assert.Equal(3, upcomingActions.Descendants().Count(element => element.Name.LocalName == "Button"));
        Assert.Contains(window.Descendants(), element =>
            element.Name.LocalName == "Border" &&
            string.Equals((string?)element.Attribute("KeyboardNavigation.TabNavigation"), "Cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledPrimaryButtonOverridesItsAccentBorder()
    {
        var controls = LoadTheme("Resources/Themes/Controls.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var baseButton = FindStyle(controls, x, "BaseButton");
        var primaryButton = FindStyle(controls, x, "PrimaryButton");
        var disabledTrigger = baseButton.Descendants().Single(element =>
            element.Name.LocalName == "Trigger" &&
            string.Equals((string?)element.Attribute("Property"), "IsEnabled", StringComparison.Ordinal) &&
            string.Equals((string?)element.Attribute("Value"), "False", StringComparison.Ordinal));

        Assert.Contains(primaryButton.Elements(), element =>
            IsSetter(element, "BorderBrush", "{DynamicResource AccentBrush}"));
        Assert.Contains(disabledTrigger.Elements(), element =>
            IsSetter(element, "BorderBrush", "{DynamicResource DisabledBorderBrush}") &&
            string.Equals((string?)element.Attribute("TargetName"), "ButtonBorder", StringComparison.Ordinal));
    }

    [Fact]
    public void QuietButtonDeclaresEnabledHoverAndPressedStates()
    {
        var controls = LoadTheme("Resources/Themes/Controls.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var quietButton = FindStyle(controls, x, "QuietButton");
        var multiTriggers = quietButton.Descendants().Where(element => element.Name.LocalName == "MultiTrigger").ToArray();

        Assert.Contains(multiTriggers, trigger =>
            HasConditions(trigger, ("IsMouseOver", "True"), ("IsEnabled", "True")) &&
            trigger.Elements().Any(element => IsSetter(element, "Background", "{DynamicResource SurfaceSecondaryBrush}")) &&
            trigger.Elements().Any(element => IsSetter(element, "Foreground", "{DynamicResource PrimaryTextBrush}")));
        Assert.Contains(multiTriggers, trigger =>
            HasConditions(trigger, ("IsPressed", "True"), ("IsEnabled", "True")) &&
            trigger.Elements().Any(element => IsSetter(element, "Background", "{DynamicResource DisabledSurfaceBrush}")));
    }

    [Fact]
    public void TitleBarThemeIntegrationUsesGeneratedDocumentedDwmAttribute()
    {
        var root = FindRepositoryRoot();
        var nativeMethods = File.ReadAllText(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "NativeMethods.txt"));
        var windowCode = File.ReadAllText(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", "MainWindow.xaml.cs"));
        var currentTheme = typeof(ApplicationThemeManager).GetProperty(
            nameof(ApplicationThemeManager.CurrentTheme),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.Contains("DwmSetWindowAttribute", nativeMethods, StringComparison.Ordinal);
        Assert.Contains("DWMWINDOWATTRIBUTE", nativeMethods, StringComparison.Ordinal);
        Assert.Contains("DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE", windowCode, StringComparison.Ordinal);
        Assert.NotNull(currentTheme);
        Assert.NotNull(currentTheme.GetMethod);
        Assert.False(currentTheme.SetMethod?.IsPublic ?? false);
    }

    private static XDocument LoadTheme(string path)
    {
        var root = FindRepositoryRoot();
        return XDocument.Load(Path.Combine(root.FullName, "src", "HdmiCaptureCardMonitor", path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static XElement FindStyle(XDocument document, XNamespace x, string key) =>
        document.Descendants().Single(element =>
            element.Name.LocalName == "Style" &&
            string.Equals((string?)element.Attribute(x + "Key"), key, StringComparison.Ordinal));

    private static bool IsSetter(XElement element, string property, string value) =>
        element.Name.LocalName == "Setter" &&
        string.Equals((string?)element.Attribute("Property"), property, StringComparison.Ordinal) &&
        string.Equals((string?)element.Attribute("Value"), value, StringComparison.Ordinal);

    private static bool HasConditions(XElement trigger, params (string Property, string Value)[] expected)
    {
        var conditions = trigger.Descendants().Where(element => element.Name.LocalName == "Condition").ToArray();
        return expected.All(item => conditions.Any(condition =>
            string.Equals((string?)condition.Attribute("Property"), item.Property, StringComparison.Ordinal) &&
            string.Equals((string?)condition.Attribute("Value"), item.Value, StringComparison.Ordinal)));
    }

    private static Color ColorValue(XDocument dictionary, string key)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var text = dictionary.Descendants().Single(element =>
            element.Name.LocalName == nameof(Color) &&
            string.Equals((string?)element.Attribute(x + "Key"), key, StringComparison.Ordinal)).Value;
        return (Color)ColorConverter.ConvertFromString(text);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HDMI-Capture-Card-Monitor.sln"))) return directory;
        }

        throw new DirectoryNotFoundException("The test could not locate the repository root.");
    }

    private static double Contrast(Color first, Color second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color) =>
        (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

    private static double Channel(byte value)
    {
        var normalized = value / 255d;
        return normalized <= 0.03928 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    private static string JoinDialog(MainWindowViewModel viewModel) =>
        string.Join(' ', viewModel.InformationDialogEyebrow, viewModel.InformationDialogTitle, viewModel.InformationDialogDescription, viewModel.InformationDialogDetails);
}
