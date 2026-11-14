using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Vault;

// An approval prompt in parts (ApprovalDetails) and as the plain text older pages and apps show (Message). The
// host renders its prompts the same way (service VaultPrompts), so a host approval reads like a local one.
public static class VaultPromptText
{
    public static ApprovalPrompt Prompt(string title, string vm, ApprovalDetails details, string action = "Approve") => new(title, Message(vm, details), action, Details: details);

    // "The VM “dev” asks for access to:\n  • name — description (with username)\n\nAccess: …\n\nnote\nReason given: “…”\nRequested by …"
    public static string Message(string vm, ApprovalDetails d) =>
        $"The VM “{vm}” {d.Lead}:\n" + string.Join('\n', d.Secrets.Select(Bullet))
        + (d.Facts.Count > 0 ? "\n\n" + string.Join('\n', d.Facts.Select(f => $"{f.Label}: {f.Value}")) : "")
        + (d.Note is { Length: > 0 } note ? "\n\n" + note : "")
        + (d.Reason.Length > 0 ? $"\nReason given: “{d.Reason}”" : "") + (d.Source.Length > 0 ? $"\nRequested by {d.Source}" : "");
    public static string Bullet(ApprovalSecret s) => $"  • {s.Name}{(s.Description.Length > 0 ? " — " + s.Description : "")}{(s.HasUsername ? " (with username)" : "")}";
}
