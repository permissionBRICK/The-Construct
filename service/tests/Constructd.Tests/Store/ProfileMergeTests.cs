using System.Text.Json.Nodes;
using Constructd.Sync.Profiles;

namespace Constructd.Tests.Store;

/// <summary>Every row of the per-field table of 9.6 and every merge case of C.5, with base, ours and theirs.</summary>
public sealed class ProfileMergeTests
{
    private static JsonObject P(string fields) => JsonNode.Parse("{\"name\":\"p\"" + (fields.Length > 0 ? "," + fields : "") + "}")!.AsObject();

    private static ProfileMergeResult Merge(string b, string o, string t) => ProfileMerge.Merge(P(b), P(o), P(t));

    private static string Field(ProfileMergeResult result, string field) => result.Merged[field]?.ToJsonString() ?? "null";

    private static void Clean(ProfileMergeResult result) => Assert.True(result.Clean, "conflicts: " + string.Join(", ", result.Conflicts));

    // ---- one side only ---------------------------------------------------------------------------

    [Fact]
    public void A_field_changed_on_one_side_takes_that_side()
    {
        var result = Merge("""
            "sdks":{"node":"20"},"provisionCommands":["a"]
            """, """
            "sdks":{"node":"22"},"provisionCommands":["a"]
            """, """
            "sdks":{"node":"20"},"provisionCommands":["a","b"]
            """);
        Clean(result);
        Assert.Equal("""{"node":"22"}""", Field(result, "sdks"));
        Assert.Equal("""["a","b"]""", Field(result, "provisionCommands"));
    }

    // ---- repos -----------------------------------------------------------------------------------

    [Fact]
    public void Repos_match_by_url_and_directory_and_keep_both_additions()
    {
        var result = Merge("""
            "repos":[{"url":"u/a"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"u/b"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"u/c"}]
            """);
        Clean(result);
        Assert.Equal("""[{"url":"u/a"},{"url":"u/b"},{"url":"u/c"}]""", Field(result, "repos"));
    }

    [Fact]
    public void A_changed_repo_url_is_a_removal_plus_an_addition()
    {
        // Ours moved u/b to u/b2; theirs added u/c. Removal of u/b holds because theirs left it alone.
        var result = Merge("""
            "repos":[{"url":"u/a"},{"url":"u/b"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"u/b2"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"u/b"},{"url":"u/c"}]
            """);
        Clean(result);
        Assert.Equal("""[{"url":"u/a"},{"url":"u/b2"},{"url":"u/c"}]""", Field(result, "repos"));

        // The same repository with a directory is another item.
        result = Merge("""
            "repos":[{"url":"u/a"},{"url":"u/b"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"u/b","directory":"x"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"u/b"},{"url":"u/c"}]
            """);
        Clean(result);
        Assert.Equal("""[{"url":"u/a"},{"url":"u/b","directory":"x"},{"url":"u/c"}]""", Field(result, "repos"));
    }

    [Fact]
    public void A_merged_first_repository_that_differs_from_either_side_is_a_conflict()
    {
        // Theirs puts a new repository first, ours appends: the merge would start with u/a, which
        // is not theirs' first repository, and that changes where the commands run.
        var result = Merge("""
            "repos":[{"url":"u/a"}],"sdks":{"node":"20"}
            """, """
            "repos":[{"url":"u/a"},{"url":"u/b"}],"sdks":{"node":"22"}
            """, """
            "repos":[{"url":"u/z"},{"url":"u/a"}],"sdks":{"node":"20"}
            """);
        Assert.Equal(["repos"], result.Conflicts);
        Assert.Equal("""[{"url":"u/z"},{"url":"u/a"}]""", Field(result, "repos"));
        // The other fields carry the merge.
        Assert.Equal("""{"node":"22"}""", Field(result, "sdks"));
    }

    [Fact]
    public void A_first_repository_changed_on_one_side_is_a_conflict_when_the_other_side_changed_anything()
    {
        // Ours moves the profile to another first repository, theirs changes a command that runs in
        // the old one: merged, theirs' command would silently run somewhere else.
        var result = Merge("""
            "repos":[{"url":"u/a"}],"provisionCommands":["build"]
            """, """
            "repos":[{"url":"u/b"}],"provisionCommands":["build"]
            """, """
            "repos":[{"url":"u/a"}],"provisionCommands":["test"]
            """);
        Assert.Equal(["repos"], result.Conflicts);
        Assert.Equal("""["test"]""", Field(result, "provisionCommands"));
    }

    [Fact]
    public void Repos_with_the_same_effective_directory_are_a_conflict_beyond_existing_duplicates()
    {
        // Ours adds git@x:org/tool.git, theirs adds https://x/org/tool: both check out into "tool".
        var result = Merge("""
            "repos":[{"url":"u/a"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"git@x:org/tool.git"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"https://x/org/tool"}]
            """);
        Assert.Equal(["repos"], result.Conflicts);

        // A duplicate a side already had is preserved, not reported.
        result = Merge("""
            "repos":[{"url":"u/a"},{"url":"u/a"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"u/a"},{"url":"u/b"}]
            """, """
            "repos":[{"url":"u/a"},{"url":"u/a"},{"url":"u/c"}]
            """);
        Clean(result);
        Assert.Equal("""[{"url":"u/a"},{"url":"u/a"},{"url":"u/b"},{"url":"u/c"}]""", Field(result, "repos"));
    }

    // ---- mcp -------------------------------------------------------------------------------------

    [Fact]
    public void Mcp_items_match_by_name_and_merge_field_by_field()
    {
        var result = Merge("""
            "mcp":[{"name":"m","command":"npx","args":["a"]}]
            """, """
            "mcp":[{"name":"m","command":"npx","args":["b"]}]
            """, """
            "mcp":[{"name":"m","command":"npx","args":["a"],"env":{"K":"V"}},{"name":"n","url":"https://n"}]
            """);
        Clean(result);
        Assert.Equal("""[{"name":"m","type":"stdio","command":"npx","args":["b"],"env":{"K":"V"}},{"name":"n","type":"http","url":"https://n"}]""", Field(result, "mcp"));
    }

    [Fact]
    public void An_mcp_type_changed_on_both_sides_is_a_conflict()
    {
        var result = Merge("""
            "mcp":[{"name":"m","type":"stdio","command":"npx"}]
            """, """
            "mcp":[{"name":"m","type":"http","url":"https://a"}]
            """, """
            "mcp":[{"name":"m","type":"http","url":"https://b"}]
            """);
        Assert.Equal(["mcp"], result.Conflicts);
    }

    [Fact]
    public void An_mcp_type_changed_on_both_sides_is_a_conflict_even_when_the_rest_merges()
    {
        var result = Merge("""
            "mcp":[{"name":"m","type":"stdio","command":"npx"}]
            """, """
            "mcp":[{"name":"m","type":"http","url":"https://a"}]
            """, """
            "mcp":[{"name":"m","type":"http","url":"https://a","headers":{"X":"1"}}]
            """);
        Assert.Equal(["mcp"], result.Conflicts);
    }

    [Fact]
    public void Mcp_servers_sharing_a_name_that_a_side_already_had_are_preserved()
    {
        var result = Merge("""
            "mcp":[{"name":"m","command":"a"},{"name":"m","command":"b"}]
            """, """
            "mcp":[{"name":"m","command":"a"},{"name":"m","command":"b"},{"name":"o","command":"c"}]
            """, """
            "mcp":[{"name":"m","command":"a2"},{"name":"m","command":"b"}]
            """);
        Clean(result);
        Assert.Equal(["m", "m", "o"], result.Merged["mcp"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()));
        Assert.Equal("a2", result.Merged["mcp"]![0]!["command"]!.GetValue<string>());
    }

    // ---- forwards ----------------------------------------------------------------------------------

    [Fact]
    public void Forwards_match_by_port_and_target_after_defaults_are_filled()
    {
        // Theirs spells the default target out; it is the same forward as ours' bare port.
        var result = Merge("""
            "forwards":[{"port":3000}]
            """, """
            "forwards":[{"port":3000,"label":"web"}]
            """, """
            "forwards":[{"port":3000,"to":"client"},{"port":5173,"to":"host"}]
            """);
        Clean(result);
        Assert.Equal("""[{"port":3000,"to":"client","label":"web"},{"port":5173,"to":"host"}]""", Field(result, "forwards"));

        // The same port to another target is another forward.
        result = Merge("", """
            "forwards":[{"port":3000}]
            """, """
            "forwards":[{"port":3000,"to":"host"}]
            """);
        Clean(result);
        Assert.Equal(2, result.Merged["forwards"]!.AsArray().Count);
    }

    [Fact]
    public void A_forward_label_changed_differently_is_a_conflict()
    {
        var result = Merge("""
            "forwards":[{"port":3000}]
            """, """
            "forwards":[{"port":3000,"label":"a"}]
            """, """
            "forwards":[{"port":3000,"label":"b"}]
            """);
        Assert.Equal(["forwards"], result.Conflicts);
    }

    // ---- hostPackages ------------------------------------------------------------------------------

    [Fact]
    public void Host_packages_are_a_set()
    {
        var result = Merge("""
            "hostPackages":["a","b","c"]
            """, """
            "hostPackages":["a","c","d"]
            """, """
            "hostPackages":["a","b","c","e"]
            """);
        Clean(result);
        // Ours removed b (theirs left it alone), both additions kept.
        Assert.Equal("""["a","c","d","e"]""", Field(result, "hostPackages"));

        // Added on both sides: kept once.
        result = Merge("""
            "hostPackages":["a"]
            """, """
            "hostPackages":["a","x"]
            """, """
            "hostPackages":["a","x","y"]
            """);
        Clean(result);
        Assert.Equal("""["a","x","y"]""", Field(result, "hostPackages"));
    }

    // ---- sdks, tests -------------------------------------------------------------------------------

    [Fact]
    public void Sdks_and_tests_merge_key_by_key_and_a_value_is_one_unit()
    {
        var result = Merge("""
            "sdks":{"node":"20","python":"3.12","dotnet":["8"]},"tests":{"unit":"npm test"}
            """, """
            "sdks":{"node":"22","python":"3.12","dotnet":["8","9"]},"tests":{"unit":"npm test","e2e":"npm run e2e"}
            """, """
            "sdks":{"node":"20","python":"3.13","dotnet":["8"],"go":"1.23"},"tests":{"unit":"npm run unit"}
            """);
        Clean(result);
        Assert.Equal("""{"node":"22","python":"3.13","dotnet":["8","9"],"go":"1.23"}""", Field(result, "sdks"));
        Assert.Equal("""{"unit":"npm run unit","e2e":"npm run e2e"}""", Field(result, "tests"));
    }

    [Fact]
    public void A_key_changed_differently_on_both_sides_is_a_conflict_for_that_key()
    {
        var result = Merge("""
            "sdks":{"node":"20","dotnet":["8"]}
            """, """
            "sdks":{"node":"22","dotnet":["8","9"]}
            """, """
            "sdks":{"node":"21","dotnet":["8","10"]}
            """);
        Assert.Equal(["sdks.node", "sdks.dotnet"], result.Conflicts);

        result = Merge("""
            "tests":{"unit":"a"}
            """, """
            "tests":{"unit":"b","lint":"l"}
            """, """
            "tests":{}
            """);
        Assert.Equal(["tests.unit"], result.Conflicts);
        Assert.Equal("""{"lint":"l"}""", Field(result, "tests"));
    }

    // ---- provisionCommands -------------------------------------------------------------------------

    [Fact]
    public void Commands_both_appended_yield_ours_then_theirs()
    {
        var result = Merge("""
            "provisionCommands":["a"]
            """, """
            "provisionCommands":["a","o1","o2"]
            """, """
            "provisionCommands":["a","t1"]
            """);
        Clean(result);
        Assert.Equal("""["a","o1","o2","t1"]""", Field(result, "provisionCommands"));
    }

    [Fact]
    public void An_identical_command_insertion_appears_once()
    {
        var result = Merge("""
            "provisionCommands":["a"]
            """, """
            "provisionCommands":["a","x","o"]
            """, """
            "provisionCommands":["a","x"]
            """);
        Clean(result);
        Assert.Equal("""["a","x","o"]""", Field(result, "provisionCommands"));
    }

    [Fact]
    public void Commands_inserted_at_the_head_on_one_side_and_the_tail_on_the_other_keep_both_positions()
    {
        var result = Merge("""
            "provisionCommands":["a","b"]
            """, """
            "provisionCommands":["h","a","b"]
            """, """
            "provisionCommands":["a","b","t"]
            """);
        Clean(result);
        Assert.Equal("""["h","a","b","t"]""", Field(result, "provisionCommands"));
    }

    [Fact]
    public void Command_changes_in_different_places_both_apply()
    {
        var result = Merge("""
            "provisionCommands":["a","b","c","d"]
            """, """
            "provisionCommands":["a2","b","c","d"]
            """, """
            "provisionCommands":["a","b","d","e"]
            """);
        Clean(result);
        Assert.Equal("""["a2","b","d","e"]""", Field(result, "provisionCommands"));
    }

    [Fact]
    public void Adjacent_commands_changed_on_different_sides_both_apply()
    {
        var result = Merge("""
            "provisionCommands":["a","b"]
            """, """
            "provisionCommands":["a2","b"]
            """, """
            "provisionCommands":["a","b2"]
            """);
        Clean(result);
        Assert.Equal("""["a2","b2"]""", Field(result, "provisionCommands"));

        // A removal next to an insertion of the other side.
        result = Merge("""
            "provisionCommands":["a","b","c"]
            """, """
            "provisionCommands":["a","c"]
            """, """
            "provisionCommands":["a","b","x","c"]
            """);
        Clean(result);
        Assert.Equal("""["a","x","c"]""", Field(result, "provisionCommands"));
    }

    [Fact]
    public void The_same_command_edited_on_both_sides_is_a_conflict_carrying_the_rest_of_the_merge()
    {
        var result = Merge("""
            "provisionCommands":["npm install","make"],"hostPackages":["a"],"sdks":{"node":"20"}
            """, """
            "provisionCommands":["npm ci","make"],"hostPackages":["a","b"],"sdks":{"node":"20"}
            """, """
            "provisionCommands":["pnpm install","make"],"hostPackages":["a"],"sdks":{"node":"22"}
            """);
        Assert.Equal(["provisionCommands"], result.Conflicts);
        Assert.Equal("""["pnpm install","make"]""", Field(result, "provisionCommands"));
        Assert.Equal("""["a","b"]""", Field(result, "hostPackages"));
        Assert.Equal("""{"node":"22"}""", Field(result, "sdks"));
    }

    [Fact]
    public void A_command_removed_on_one_side_and_changed_on_the_other_is_a_conflict()
    {
        var result = Merge("""
            "provisionCommands":["a","b","c"]
            """, """
            "provisionCommands":["a","c"]
            """, """
            "provisionCommands":["a","b2","c"]
            """);
        Assert.Equal(["provisionCommands"], result.Conflicts);
    }

    [Fact]
    public void Duplicate_commands_a_side_had_are_kept()
    {
        var result = Merge("""
            "provisionCommands":["x","x"]
            """, """
            "provisionCommands":["x","x","o"]
            """, """
            "provisionCommands":["t","x","x"]
            """);
        Clean(result);
        Assert.Equal("""["t","x","x","o"]""", Field(result, "provisionCommands"));
    }

    // ---- order of the keyed lists and the set ------------------------------------------------------

    [Fact]
    public void A_reorder_on_one_side_only_is_taken_and_the_other_sides_additions_follow()
    {
        var result = Merge("""
            "hostPackages":["a","b","c"]
            """, """
            "hostPackages":["a","b","c","o"]
            """, """
            "hostPackages":["c","b","a"]
            """);
        Clean(result);
        Assert.Equal("""["c","b","a","o"]""", Field(result, "hostPackages"));

        result = Merge("""
            "mcp":[{"name":"a","command":"x"},{"name":"b","command":"x"}]
            """, """
            "mcp":[{"name":"b","command":"x"},{"name":"a","command":"x"}]
            """, """
            "mcp":[{"name":"a","command":"x"},{"name":"b","command":"x"},{"name":"c","command":"x"}]
            """);
        Clean(result);
        Assert.Equal(["b", "a", "c"], result.Merged["mcp"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()));
    }

    [Fact]
    public void Both_sides_reordering_differently_is_a_conflict()
    {
        var result = Merge("""
            "hostPackages":["a","b","c"]
            """, """
            "hostPackages":["c","a","b"]
            """, """
            "hostPackages":["b","c","a"]
            """);
        Assert.Equal(["hostPackages"], result.Conflicts);

        // The same reorder on both sides is no conflict.
        result = Merge("""
            "hostPackages":["a","b"]
            """, """
            "hostPackages":["b","a","o"]
            """, """
            "hostPackages":["b","a","t"]
            """);
        Clean(result);
        Assert.Equal("""["b","a","o","t"]""", Field(result, "hostPackages"));
    }

    [Fact]
    public void Removing_a_list_item_the_other_side_changed_is_a_conflict()
    {
        var result = Merge("""
            "mcp":[{"name":"m","command":"a"}]
            """, """
            "mcp":[]
            """, """
            "mcp":[{"name":"m","command":"b"}]
            """);
        Assert.Equal(["mcp"], result.Conflicts);
    }

    // ---- name and later fields ---------------------------------------------------------------------

    [Fact]
    public void Name_and_fields_added_later_conflict_when_both_changed_differently()
    {
        var result = ProfileMerge.Merge(
            JsonNode.Parse("""{"name":"p","futureField":1}""")!.AsObject(),
            JsonNode.Parse("""{"name":"q","futureField":2}""")!.AsObject(),
            JsonNode.Parse("""{"name":"r","futureField":3}""")!.AsObject());
        Assert.Contains("name", result.Conflicts);
        Assert.Contains("futureField", result.Conflicts);

        // One side changing a later field takes that side (the schema check then decides).
        result = ProfileMerge.Merge(
            JsonNode.Parse("""{"name":"p"}""")!.AsObject(),
            JsonNode.Parse("""{"name":"p","sdks":{"go":"1"}}""")!.AsObject(),
            JsonNode.Parse("""{"name":"p","hostPackages":["x"]}""")!.AsObject());
        Clean(result);
    }

    // ---- checks after the merge --------------------------------------------------------------------

    [Fact]
    public void A_merge_that_fails_the_profile_checks_turns_the_field_into_a_conflict()
    {
        // Ours types the server as http (and gives it a url), theirs adds args to the stdio form:
        // field by field this merges into an http server with args, which the schema refuses.
        var result = Merge("""
            "mcp":[{"name":"m","type":"stdio","command":"npx"}]
            """, """
            "mcp":[{"name":"m","type":"http","url":"https://m"}]
            """, """
            "mcp":[{"name":"m","type":"stdio","command":"npx","args":["x"]}]
            """);
        Assert.Equal(["mcp"], result.Conflicts);
        Assert.Equal("""[{"name":"m","type":"stdio","command":"npx","args":["x"]}]""", Field(result, "mcp"));
        Assert.Empty(ProfileRules.Validate(result.Merged));
    }

    [Fact]
    public void A_forward_added_on_both_sides_with_different_labels_is_a_conflict()
    {
        // Ours moves the 3000 forward to 4000 while theirs adds its own 4000 with another label.
        var result = Merge("""
            "forwards":[{"port":3000,"label":"a"}]
            """, """
            "forwards":[{"port":4000,"label":"a"}]
            """, """
            "forwards":[{"port":3000,"label":"a"},{"port":4000,"label":"b"}]
            """);
        Assert.Equal(["forwards"], result.Conflicts);
    }

    [Fact]
    public void Without_a_base_every_disagreement_is_a_conflict()
    {
        var result = ProfileMerge.Merge(null,
            JsonNode.Parse("""{"name":"p","sdks":{"node":"22"},"hostPackages":["a"]}""")!.AsObject(),
            JsonNode.Parse("""{"name":"p","sdks":{"node":"20"},"hostPackages":["a"]}""")!.AsObject());
        Assert.Equal(["sdks"], result.Conflicts);
        Assert.Equal("""["a"]""", Field(result, "hostPackages"));
    }

    [Fact]
    public void The_same_two_edits_merge_in_either_order_and_both_results_pass_the_checks()
    {
        var b = P("""
            "provisionCommands":["a"],"hostPackages":["x"]
            """);
        var one = P("""
            "provisionCommands":["a","one"],"hostPackages":["x","y"]
            """);
        var two = P("""
            "provisionCommands":["a","two"],"hostPackages":["x","z"]
            """);
        var first = ProfileMerge.Merge(b, one, two);
        var second = ProfileMerge.Merge(b, two, one);
        Clean(first);
        Clean(second);
        Assert.Empty(ProfileRules.Validate(first.Merged));
        Assert.Empty(ProfileRules.Validate(second.Merged));
        Assert.Equal("""["a","one","two"]""", Field(first, "provisionCommands"));
        Assert.Equal("""["a","two","one"]""", Field(second, "provisionCommands"));
    }
}
