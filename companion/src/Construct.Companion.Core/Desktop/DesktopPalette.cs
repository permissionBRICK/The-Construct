using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Desktop;

// The webview media is skinned by VS Code's injected theme variables. Outside VS Code the Companion
// supplies a complete palette itself (VS Code's Dark Modern / Light Modern values), chosen from the
// Windows app theme; a partial palette mixed with the CSS fallbacks produced black text on dark ground.
public static class DesktopPalette
{
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string AppsUseLightThemeValue = "AppsUseLightTheme";
    // Windows without the value (older builds, fresh profiles) uses the light app theme.
    public static bool IsDark(IRegistry registry) => registry.ReadInt32(PersonalizeKey, AppsUseLightThemeValue) == 0;
    public static IReadOnlyDictionary<string, string> Variables(bool dark) => dark ? Dark : Light;
    private static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["--vscode-font-family"] = "\"Segoe WPF\", \"Segoe UI\", sans-serif",
        ["--vscode-font-size"] = "13px",
        ["--vscode-editor-font-family"] = "Consolas, \"Courier New\", monospace",
        ["--vscode-foreground"] = "#cccccc",
        ["--vscode-descriptionForeground"] = "#9d9d9d",
        ["--vscode-disabledForeground"] = "#6e6e6e",
        ["--vscode-errorForeground"] = "#f88070",
        ["--vscode-editor-background"] = "#1f1f1f",
        ["--vscode-editorWidget-background"] = "#202020",
        ["--vscode-notifications-background"] = "#1f1f1f",
        ["--vscode-sideBar-background"] = "#181818",
        ["--vscode-sideBarTitle-foreground"] = "#cccccc",
        ["--vscode-widget-border"] = "#313131",
        ["--vscode-widget-shadow"] = "rgba(0, 0, 0, 0.36)",
        ["--vscode-list-hoverBackground"] = "#2a2d2e",
        ["--vscode-textLink-foreground"] = "#4daafc",
        ["--vscode-focusBorder"] = "#0078d4",
        ["--vscode-button-background"] = "#0078d4",
        ["--vscode-button-foreground"] = "#ffffff",
        ["--vscode-button-hoverBackground"] = "#026ec1",
        ["--vscode-button-secondaryBackground"] = "#313131",
        ["--vscode-button-secondaryForeground"] = "#cccccc",
        ["--vscode-button-secondaryHoverBackground"] = "#3c3c3c",
        ["--vscode-input-background"] = "#313131",
        ["--vscode-input-foreground"] = "#cccccc",
        ["--vscode-input-border"] = "#3c3c3c",
        ["--vscode-dropdown-background"] = "#313131",
        ["--vscode-dropdown-foreground"] = "#cccccc",
        ["--vscode-dropdown-border"] = "#3c3c3c",
        ["--vscode-badge-background"] = "#616161",
        ["--vscode-badge-foreground"] = "#f8f8f8",
        ["--vscode-charts-green"] = "#89d185",
        ["--vscode-charts-yellow"] = "#cca700",
        ["--vscode-charts-blue"] = "#3794ff",
    };
    private static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["--vscode-font-family"] = "\"Segoe WPF\", \"Segoe UI\", sans-serif",
        ["--vscode-font-size"] = "13px",
        ["--vscode-editor-font-family"] = "Consolas, \"Courier New\", monospace",
        ["--vscode-foreground"] = "#3b3b3b",
        ["--vscode-descriptionForeground"] = "#616161",
        ["--vscode-disabledForeground"] = "#a0a0a0",
        ["--vscode-errorForeground"] = "#f85149",
        ["--vscode-editor-background"] = "#ffffff",
        ["--vscode-editorWidget-background"] = "#f8f8f8",
        ["--vscode-notifications-background"] = "#ffffff",
        ["--vscode-sideBar-background"] = "#f8f8f8",
        ["--vscode-sideBarTitle-foreground"] = "#3b3b3b",
        ["--vscode-widget-border"] = "#e5e5e5",
        ["--vscode-widget-shadow"] = "rgba(0, 0, 0, 0.16)",
        ["--vscode-list-hoverBackground"] = "#f2f2f2",
        ["--vscode-textLink-foreground"] = "#005fb8",
        ["--vscode-focusBorder"] = "#005fb8",
        ["--vscode-button-background"] = "#005fb8",
        ["--vscode-button-foreground"] = "#ffffff",
        ["--vscode-button-hoverBackground"] = "#0258a8",
        ["--vscode-button-secondaryBackground"] = "#e5e5e5",
        ["--vscode-button-secondaryForeground"] = "#3b3b3b",
        ["--vscode-button-secondaryHoverBackground"] = "#cccccc",
        ["--vscode-input-background"] = "#ffffff",
        ["--vscode-input-foreground"] = "#3b3b3b",
        ["--vscode-input-border"] = "#cecece",
        ["--vscode-dropdown-background"] = "#ffffff",
        ["--vscode-dropdown-foreground"] = "#3b3b3b",
        ["--vscode-dropdown-border"] = "#cecece",
        ["--vscode-badge-background"] = "#cccccc",
        ["--vscode-badge-foreground"] = "#3b3b3b",
        ["--vscode-charts-green"] = "#388a34",
        ["--vscode-charts-yellow"] = "#bf8803",
        ["--vscode-charts-blue"] = "#005fb8",
    };
}
