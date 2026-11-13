using System.Text;

namespace Constructd.Core.Services;

/// <summary>
/// A job's secrets, in this process's memory only (design 7.1, 7.4): never persisted, gone when the
/// job ends or the engine restarts (a retried job is given them again). <see cref="Redact"/> replaces
/// every value — raw, base64-encoded and, for a base64 value, its decoded text and each of its lines —
/// in a line before the line is reported, so no progress line, error or result can carry one.
///
/// Residual exposure, as the design states it: .NET strings cannot be zeroed, so a value stays in the
/// managed heap until it is collected; crash dumps and the pagefile can hold it.
/// </summary>
public sealed class JobSecretBag
{
    public const string Mask = "[secret]";

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<string> _forms = [];
    private readonly Lock _gate = new();

    /// <param name="base64">The value is base64: its decoded text is masked too.</param>
    public void Set(string name, string? value, bool base64 = true)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_gate)
        {
            _values[name] = value;
            AddForms(value, base64);
            _forms.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
    }

    public string? Get(string name)
    {
        lock (_gate) return _values.TryGetValue(name, out var value) ? value : null;
    }

    public bool Has(string name) => Get(name) is not null;

    /// <summary>The secret names and values, for staging them on the guest.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Entries(IEnumerable<string> names)
    {
        lock (_gate) return names.Where(_values.ContainsKey).Select(n => new KeyValuePair<string, string>(n, _values[n])).ToList();
    }

    public string Redact(string text)
    {
        lock (_gate)
        {
            foreach (var form in _forms)
                if (text.Contains(form, StringComparison.Ordinal)) text = text.Replace(form, Mask, StringComparison.Ordinal);
            return text;
        }
    }

    /// <summary>Drops every value. Called when the job ends, whatever its outcome.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            _forms.Clear();
        }
    }

    private void AddForms(string value, bool base64)
    {
        // Every form, however short: a short secret masks ordinary words in the log, which is the lesser harm.
        void Add(string form) { if (form.Length > 0 && !_forms.Contains(form)) _forms.Add(form); }
        Add(value);
        Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
        if (!base64 || !TryDecodeBase64(value, out var decoded)) return;
        Add(decoded);
        foreach (var line in decoded.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ');
            Add(trimmed);
            // A git credential line: proto://user:token@host — the token alone can also be printed.
            var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
            var at = trimmed.LastIndexOf('@');
            if (scheme > 0 && at > scheme)
            {
                var userInfo = trimmed[(scheme + 3)..at];
                var colon = userInfo.IndexOf(':');
                if (colon >= 0)
                {
                    var token = userInfo[(colon + 1)..];
                    Add(token);
                    Add(Uri.UnescapeDataString(token));
                }
            }
        }
    }

    private static bool TryDecodeBase64(string value, out string decoded)
    {
        decoded = string.Empty;
        var buffer = new byte[value.Length];
        if (!Convert.TryFromBase64String(value, buffer, out var written)) return false;
        try { decoded = new UTF8Encoding(false, true).GetString(buffer, 0, written); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}
