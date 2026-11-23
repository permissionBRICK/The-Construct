using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Constructd.Sync.Profiles;

/// <summary>
/// One profile name as a VM's projects folder holds it: the file (null when absent), the revision
/// in <c>.state/&lt;name&gt;.rev</c> (null when absent) and whether <c>.deleted/&lt;name&gt;</c> exists.
/// </summary>
public sealed record VmProfileEntry(string Name, long? Revision, byte[]? File, bool Marker)
{
    /// <summary>What the write-back guard compares (see <see cref="VmProfileFolder.Fingerprint"/>).</summary>
    public string Fingerprint => VmProfileFolder.Fingerprint(Revision, File, Marker);
}

/// <summary>A read of a VM's projects folder.</summary>
public sealed record VmFolderRead(bool StoreExists, IReadOnlyList<VmProfileEntry> Entries);

/// <summary>One guarded write into a VM's projects folder.</summary>
/// <param name="ExpectedFingerprint">The state the engine last saw; anything else is left in place.</param>
/// <param name="Content">The new file, or null to remove it.</param>
public sealed record VmProfileWrite(string Name, string ExpectedFingerprint, long Revision, byte[]? Content);

/// <summary>
/// The guest side of profile sync as text (docs/config-sync.md, "The new stack"): the script that
/// reads <c>/opt/construct/projects</c> (a port of <c>Read-ConstructVmStore</c>), the guarded write
/// (<c>Write-ConstructVmStore</c>, guarded by a fingerprint instead of the content alone), and the
/// canonical file form. The scripts read the store path from <c>CONSTRUCT_PROJECTS_STORE</c> when
/// set, which only the tests do. Pure.
/// </summary>
public static class VmProfileFolder
{
    public const string StorePath = "/opt/construct/projects";

    private static readonly JsonWriterOptions FileJson = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// <c>&lt;revision&gt;:&lt;sha256 of the file or "absent"&gt;:&lt;1 when a deletion marker exists, else 0&gt;</c>,
    /// computed the same way by the write script.
    /// </summary>
    public static string Fingerprint(long? revision, byte[]? file, bool marker) =>
        $"{revision?.ToString(CultureInfo.InvariantCulture) ?? ""}:{(file is null ? "absent" : Convert.ToHexStringLower(SHA256.HashData(file)))}:{(marker ? 1 : 0)}";

    /// <summary>The file form every writer uses: two-space indent, one element per line, a trailing newline.</summary>
    public static byte[] Format(JsonNode profile)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, FileJson)) profile.WriteTo(writer);
        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    /// <summary>A name the engine reads and writes: one path component the guest's CLI would accept.</summary>
    public static bool SafeName(string name) =>
        name.Length is > 0 and <= 200 && name != "." && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..", StringComparison.Ordinal)
        && !name.Any(char.IsControl) && name.Trim() == name;

    public const string ReadScript = """
        set -u
        store="${CONSTRUCT_PROJECTS_STORE:-/opt/construct/projects}"
        if [ ! -d "$store" ]; then printf 'NOSTORE\n'; printf 'END\n'; exit 0; fi
        {
          for f in "$store"/*.json; do [ -f "$f" ] && basename "$f" .json; done
          for f in "$store"/.deleted/*; do [ -f "$f" ] && basename "$f"; done
          for f in "$store"/.state/*.rev; do [ -f "$f" ] && basename "$f" .rev; done
        } | sort -u | while IFS= read -r name; do
          rev=""
          [ -f "$store/.state/$name.rev" ] && rev="$(head -n 1 "$store/.state/$name.rev" | tr -dc '0-9')"
          b64=-
          [ -f "$store/$name.json" ] && b64="$(base64 < "$store/$name.json" | tr -d '\n')"
          marker=0
          [ -f "$store/.deleted/$name" ] && marker=1
          printf 'P\t%s\t%s\t%s\t%s\n' "$name" "$rev" "$b64" "$marker"
        done
        printf 'END\n'
        """;

    /// <summary>Parses the read script's output; null when it did not finish (no <c>END</c>).</summary>
    public static VmFolderRead? ParseRead(IEnumerable<string> lines)
    {
        var exists = true;
        var entries = new List<VmProfileEntry>();
        foreach (var line in lines)
        {
            if (line == "END") return new VmFolderRead(exists, entries);
            if (line == "NOSTORE") { exists = false; continue; }
            var parts = line.Split('\t');
            if (parts.Length != 5 || parts[0] != "P" || !SafeName(parts[1])) continue;
            long? revision = long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : null;
            byte[]? file = null;
            if (parts[3] != "-")
            {
                try { file = Convert.FromBase64String(parts[3]); }
                catch (FormatException) { continue; }
            }

            entries.Add(new VmProfileEntry(parts[1], revision, file, parts[4] == "1"));
        }

        return null;
    }

    /// <summary>
    /// The guarded write: each operation applies only when the name's fingerprint is still the
    /// expected one, then writes (or removes) the file, writes the revision and clears a deletion
    /// marker. Prints <c>D\t&lt;name&gt;\t&lt;new fingerprint&gt;</c> or <c>S\t&lt;name&gt;</c>, then <c>END</c>.
    /// </summary>
    public static string WriteScript(IEnumerable<VmProfileWrite> writes)
    {
        var script = new StringBuilder("""
            set -u
            umask 022
            store="${CONSTRUCT_PROJECTS_STORE:-/opt/construct/projects}"
            mkdir -p "$store/.state"
            fp() {
              local n="$1" rev="" sha=absent m=0
              [ -f "$store/.state/$n.rev" ] && rev="$(head -n 1 "$store/.state/$n.rev" | tr -dc '0-9')"
              [ -f "$store/$n.json" ] && sha="$(sha256sum < "$store/$n.json" | cut -d ' ' -f 1)"
              [ -f "$store/.deleted/$n" ] && m=1
              printf '%s:%s:%s' "$rev" "$sha" "$m"
            }
            op() {
              local n="$1" expect="$2" rev="$3" b64="$4"
              if [ "$(fp "$n")" != "$expect" ]; then printf 'S\t%s\n' "$n"; return 0; fi
              if [ "$b64" = - ]; then
                rm -f "$store/$n.json"
              else
                printf '%s' "$b64" | base64 -d > "$store/.$n.json.tmp" && mv -f "$store/.$n.json.tmp" "$store/$n.json" || { printf 'S\t%s\n' "$n"; return 0; }
              fi
              printf '%s\n' "$rev" > "$store/.state/$n.rev"
              rm -f "$store/.deleted/$n"
              printf 'D\t%s\t%s\n' "$n" "$(fp "$n")"
            }

            """);
        foreach (var write in writes)
        {
            if (!SafeName(write.Name)) throw new ArgumentException($"Unsafe profile name '{write.Name}'.", nameof(writes));
            script.Append(CultureInfo.InvariantCulture,
                $"op {Quote(write.Name)} {Quote(write.ExpectedFingerprint)} {write.Revision} {(write.Content is null ? "-" : Convert.ToBase64String(write.Content))}\n");
        }

        script.Append("printf 'END\\n'\n");
        return script.ToString();
    }

    /// <summary>The names written, with their new fingerprints, and the names skipped; null when the script did not finish.</summary>
    public static (IReadOnlyDictionary<string, string> Done, IReadOnlyList<string> Skipped)? ParseWrite(IEnumerable<string> lines)
    {
        var done = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var line in lines)
        {
            if (line == "END") return (done, skipped);
            var parts = line.Split('\t');
            if (parts.Length == 3 && parts[0] == "D") done[parts[1]] = parts[2];
            else if (parts.Length == 2 && parts[0] == "S") skipped.Add(parts[1]);
        }

        return null;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
