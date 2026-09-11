namespace Construct.Companion.Host.ConfigSync;

// Its message is already redacted and safe to show in the panel.
public sealed class ConfigSyncException(string message) : Exception(message);
