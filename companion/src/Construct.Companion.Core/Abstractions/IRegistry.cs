namespace Construct.Companion.Core.Abstractions;

// Reads and writes HKCU values for Run, construct protocol, and toast AUMID keys.
// Key paths are relative to HKCU; null value names mean the default value.
public interface IRegistry
{
    string? ReadString(string key, string? name);
    void WriteString(string key, string? name, string value);
    void DeleteValue(string key, string? name);
    void DeleteTree(string key);
}
