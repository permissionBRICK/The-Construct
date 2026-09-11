namespace Construct.Companion.Core.ConfigSync;
public static class ConfigSharing
{
    public const string DefaultRepo = "permissionBRICK/The-Construct";
    public const string DefaultRef = "main";
    private static string Quote(string text) => "'"+text.Replace("'","''",StringComparison.Ordinal)+"'";
    private static string Start(string repo, string @ref) => "& ([scriptblock]::Create((irm "+Quote("https://raw.githubusercontent.com/"+repo+"/"+@ref+"/install.ps1")+")))";
    private static string Suffix(string repo, string @ref) => (repo != DefaultRepo ? " -Repo "+Quote(repo) : "")+(@ref != DefaultRef ? " -Ref "+Quote(@ref) : "");
    public static string BuildShareCommand(string url, IEnumerable<string> names, string installRepo = DefaultRepo, string installRef = DefaultRef)
    {
        if (ConfigSyncRules.UrlHasCredentials(url)) throw new ArgumentException("Share URLs must not contain credentials.");
        return Start(installRepo,installRef)+" -ConfigRepo "+Quote(url)+" -ImportConfigs "+Quote(string.Join(',',names))+" -Action add-config"+Suffix(installRepo,installRef);
    }
    public static string BuildDeployPs1(string installRepo = DefaultRepo, string installRef = DefaultRef) => "# Bootstraps Construct and imports the config files bundled next to this script.\n"+Start(installRepo,installRef)+" -ConfigDir \"$PSScriptRoot\" -Action add-config"+Suffix(installRepo,installRef)+"\n";
}
