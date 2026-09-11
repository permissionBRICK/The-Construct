using System.Text.Json;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Tests.Parity;

public sealed class DesktopWebviewParityTests
{
    public static IEnumerable<object[]> Rows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Parity","Fixtures","desktop-webviews.json")));
        return document.RootElement.EnumerateArray().Select(r=>new object[]{r.Clone()}).ToArray();
    }
    [Theory] [MemberData(nameof(Rows))]
    public void SharedMediaMatchesJavascript(JsonElement row)
    {
        string S(string key)=>row.GetProperty(key).GetString()!;
        var actual = S("kind") switch
        {
            "theme"=>WebViewDocument.ThemeCss(row.GetProperty("input").GetString()),
            "document"=>WebViewDocument.Render(S("template"),S("surface")+".js",S("theme"),S("nonce")),
            "picker"=>WebViewDocument.ThemePicker(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Media","theme-picker.html")),S("nonce"),row.GetProperty("cards").Deserialize<ThemeCard[]>(IpcJson.Options)!),
            _=>throw new InvalidOperationException()
        };
        Assert.Equal(S("output"),actual);
    }
}
