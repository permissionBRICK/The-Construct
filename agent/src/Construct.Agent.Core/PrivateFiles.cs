namespace Construct.Agent;

/// <summary>Files only the user reads: the directory 0700, each file 0600 from its creation on, written atomically.</summary>
public static class PrivateFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The per-user profile directory is already private to the user.
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, DirectoryMode);
        File.SetUnixFileMode(path, DirectoryMode);
    }

    /// <summary>Writes to a 0600 temporary file next to <paramref name="path"/> and moves it over.</summary>
    public static void Write(string path, ReadOnlySpan<byte> content)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("n")[..8] + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
