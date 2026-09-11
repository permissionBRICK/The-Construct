namespace Construct.Companion.Core.Abstractions;

// Key paths are relative to HKCU; null value names mean the default value.
// Whole keys are only ever removed by the PowerShell uninstaller.
public interface IRegistry
{
    string? ReadString(string key, string? name);
    void WriteString(string key, string? name, string value);
    void DeleteValue(string key, string? name);
}
