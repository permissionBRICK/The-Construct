using System.Text.RegularExpressions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Fakes;
namespace Construct.Companion.Tests.Desktop;

public class DesktopPaletteTests
{
    private static IEnumerable<string> MediaVariables()
    {
        var media = Path.Combine(AppContext.BaseDirectory, "Media");
        var files = Directory.EnumerateFiles(media, "*.css", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(media, "*.js")).Concat(Directory.EnumerateFiles(media, "*.html"));
        return files.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"var\((--vscode-[A-Za-z0-9-]+)").Select(m => m.Groups[1].Value)).Distinct();
    }
    [Theory, InlineData(true), InlineData(false)]
    public void EveryVariableTheMediaUsesHasAValue(bool dark)
    {
        var palette = DesktopPalette.Variables(dark);
        var used = MediaVariables().ToArray();
        Assert.NotEmpty(used);
        Assert.All(used, v => Assert.True(palette.ContainsKey(v), v + " has no palette value"));
        Assert.All(palette.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }
    [Fact]
    public void DarkAndLightDefineTheSameVariables()
    {
        Assert.Equal(DesktopPalette.Variables(true).Keys.Order(), DesktopPalette.Variables(false).Keys.Order());
        Assert.NotEqual(DesktopPalette.Variables(true)["--vscode-foreground"], DesktopPalette.Variables(false)["--vscode-foreground"]);
    }
    [Theory, InlineData("0", true), InlineData("1", false), InlineData(null, false)]
    public void AppThemeComesFromThePersonalizeKey(string? value, bool dark)
    {
        var registry = new FakeRegistry();
        if (value is not null) registry.WriteString(DesktopPalette.PersonalizeKey, DesktopPalette.AppsUseLightThemeValue, value);
        Assert.Equal(dark, DesktopPalette.IsDark(registry));
    }
}
