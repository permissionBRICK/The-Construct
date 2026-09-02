using System.Text.Json;
using Constructd.Api.Admin;
using Constructd.Core.Configuration;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// <c>constructd admin …</c> — the way the first admin exists before anybody can authenticate, and the
/// way the installer script seeds one. Every verb is driven against in-memory stores: the exit code,
/// the JSON, and the audit entry it leaves behind.
/// </summary>
public sealed class AdminCliTests
{
    [Fact]
    public async Task Users_add_creates_the_user_with_the_requested_role_and_quota()
    {
        var cli = new Cli();

        var exit = await cli.RunAsync("users", "add", @"DOMAIN\christoph", "--role", "Admin", "--max-vms", "10");

        Assert.Equal(AdminExitCode.Ok, exit);
        var user = await cli.Users.GetAsync(@"DOMAIN\christoph", CancellationToken.None);
        Assert.Equal(Role.Admin, user!.Role);
        Assert.Equal(10, user.MaxVms);
        Assert.True(user.AllowHostForwards);
        Assert.Contains("role Admin", cli.Output);
    }

    [Fact]
    public async Task Users_add_defaults_to_a_plain_user_with_no_quota()
    {
        // A quota of 0 means "may not create VMs": the safe default for a name typed without thinking.
        var cli = new Cli();

        await cli.RunAsync("users", "add", "alice");

        var user = await cli.Users.GetAsync("alice", CancellationToken.None);
        Assert.Equal(Role.User, user!.Role);
        Assert.Equal(0, user.MaxVms);
    }

    [Fact]
    public async Task Users_add_can_deny_host_forwards()
    {
        var cli = new Cli();

        await cli.RunAsync("users", "add", "alice", "--max-vms", "2", "--no-host-forwards");

        var user = await cli.Users.GetAsync("alice", CancellationToken.None);
        Assert.False(user!.AllowHostForwards);
    }

    [Fact]
    public async Task Users_add_reports_a_conflict_rather_than_overwriting()
    {
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice");

        var exit = await cli.RunAsync("users", "add", "alice", "--role", "Admin");

        Assert.Equal(AdminExitCode.Conflict, exit);
        Assert.Equal(Role.User, (await cli.Users.GetAsync("alice", CancellationToken.None))!.Role);
    }

    [Theory]
    [InlineData("--role", "superuser")]
    [InlineData("--max-vms", "lots")]
    [InlineData("--quota", "5")]
    public async Task A_bad_option_is_a_usage_error_and_creates_nothing(string option, string value)
    {
        var cli = new Cli();

        var exit = await cli.RunAsync("users", "add", "alice", option, value);

        Assert.Equal(AdminExitCode.Usage, exit);
        Assert.Null(await cli.Users.GetAsync("alice", CancellationToken.None));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("Admin ")]
    public async Task A_numeric_role_is_refused_rather_than_parsed(string role)
    {
        // Enum.TryParse accepts any number, defined or not, so "--role 7" would otherwise create a user
        // whose role nothing in the service knows how to authorize. Names only.
        var cli = new Cli();

        var exit = await cli.RunAsync("users", "add", "alice", "--role", role);

        if (role.Trim() == "Admin")
        {
            // Surrounding whitespace is a typo, not a different role.
            Assert.Equal(AdminExitCode.Ok, exit);
            Assert.Equal(Role.Admin, (await cli.Users.GetAsync("alice", CancellationToken.None))!.Role);
            return;
        }

        Assert.Equal(AdminExitCode.Usage, exit);
        Assert.Null(await cli.Users.GetAsync("alice", CancellationToken.None));
    }

    [Theory]
    [InlineData("users", "list", "extra")]
    [InlineData("users", "list", "--role")]
    [InlineData("users", "remove", "alice", "--force")]
    [InlineData("tokens", "revoke-all", "alice", "--all")]
    [InlineData("forwards", "reconcile", "--now")]
    public async Task Trailing_arguments_are_a_usage_error_rather_than_silently_ignored(params string[] args)
    {
        // A verb that quietly ignores what it was not expecting is how somebody ends up believing they
        // asked for something they did not ask for.
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice");
        cli.Reset();

        var exit = await cli.RunAsync(args);

        Assert.Equal(AdminExitCode.Usage, exit);
        Assert.NotNull(await cli.Users.GetAsync("alice", CancellationToken.None));
    }

    [Fact]
    public async Task Users_add_without_a_name_is_a_usage_error()
    {
        var cli = new Cli();

        Assert.Equal(AdminExitCode.Usage, await cli.RunAsync("users", "add"));
        Assert.Equal(AdminExitCode.Usage, await cli.RunAsync("users", "add", "--role", "Admin"));
    }

    [Fact]
    public async Task Users_list_prints_every_user()
    {
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice", "--max-vms", "2");
        await cli.RunAsync("users", "add", "bob", "--role", "Admin", "--max-vms", "5");
        cli.Reset();

        var exit = await cli.RunAsync("users", "list");

        Assert.Equal(AdminExitCode.Ok, exit);
        Assert.Contains("alice", cli.Output);
        Assert.Contains("bob", cli.Output);
    }

    [Fact]
    public async Task Users_list_speaks_json()
    {
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice", "--max-vms", "2");
        cli.Reset();

        await cli.RunAsync("users", "list", "--json");

        var users = JsonSerializer.Deserialize<JsonElement>(cli.Output);
        Assert.Equal(JsonValueKind.Array, users.ValueKind);
        Assert.Equal("alice", users[0].GetProperty("name").GetString());
        Assert.Equal(2, users[0].GetProperty("maxVms").GetInt32());
    }

    [Fact]
    public async Task Users_remove_revokes_the_tokens_with_the_user()
    {
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice");
        await cli.RunAsync("tokens", "issue", "alice");
        cli.Reset();

        var exit = await cli.RunAsync("users", "remove", "alice");

        Assert.Equal(AdminExitCode.Ok, exit);
        Assert.Null(await cli.Users.GetAsync("alice", CancellationToken.None));
        Assert.Empty(await cli.Tokens.ListAsync("alice", CancellationToken.None));
    }

    [Fact]
    public async Task Users_remove_refuses_while_the_user_still_owns_vms()
    {
        // Their VMs would be left with an owner that does not exist — the same rule the API enforces.
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice", "--max-vms", "2");
        await cli.AddVmAsync("work-vm", "alice");
        cli.Reset();

        var exit = await cli.RunAsync("users", "remove", "alice");

        Assert.Equal(AdminExitCode.Conflict, exit);
        Assert.NotNull(await cli.Users.GetAsync("alice", CancellationToken.None));
        Assert.Contains("still owns 1 VM", cli.Error);
    }

    [Fact]
    public async Task Removing_a_user_who_does_not_exist_says_so()
    {
        var cli = new Cli();

        Assert.Equal(AdminExitCode.NotFound, await cli.RunAsync("users", "remove", "nobody"));
        Assert.Contains("no such user", cli.Error);
    }

    [Fact]
    public async Task Tokens_issue_prints_a_working_token_exactly_once()
    {
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice", "--max-vms", "1");
        cli.Reset();

        var exit = await cli.RunAsync("tokens", "issue", "alice", "--label", "laptop", "--json");

        Assert.Equal(AdminExitCode.Ok, exit);
        var issued = JsonSerializer.Deserialize<JsonElement>(cli.Output);
        Assert.Equal("alice", issued.GetProperty("user").GetString());
        Assert.Equal("laptop", issued.GetProperty("label").GetString());

        var plaintext = issued.GetProperty("token").GetString()!;
        var principal = await cli.Tokens.ValidateAsync(plaintext, CancellationToken.None);
        Assert.Equal("alice", principal!.Name);

        // Only the hash is stored: nothing can print it a second time.
        Assert.DoesNotContain(plaintext, (await cli.Tokens.ListAsync("alice", CancellationToken.None))[0].TokenHash);
    }

    [Fact]
    public async Task The_issued_token_never_reaches_the_audit_trail()
    {
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice");
        cli.Reset();
        await cli.RunAsync("tokens", "issue", "alice", "--label", "laptop", "--json");

        var plaintext = JsonSerializer.Deserialize<JsonElement>(cli.Output).GetProperty("token").GetString()!;
        var audit = await cli.Audit.QueryAsync(50, CancellationToken.None);

        Assert.Contains(audit, entry => entry.Action == "token.issue" && entry.Target == "alice");
        Assert.DoesNotContain(audit, entry => (entry.Detail ?? string.Empty).Contains(plaintext, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tokens_for_an_unknown_user_are_refused()
    {
        var cli = new Cli();

        Assert.Equal(AdminExitCode.NotFound, await cli.RunAsync("tokens", "issue", "nobody"));
        Assert.Equal(AdminExitCode.NotFound, await cli.RunAsync("tokens", "revoke-all", "nobody"));
    }

    [Fact]
    public async Task Tokens_revoke_all_invalidates_every_token_of_a_user()
    {
        var cli = new Cli();
        await cli.RunAsync("users", "add", "alice");
        cli.Reset();
        await cli.RunAsync("tokens", "issue", "alice", "--json");
        var plaintext = JsonSerializer.Deserialize<JsonElement>(cli.Output).GetProperty("token").GetString()!;
        cli.Reset();

        var exit = await cli.RunAsync("tokens", "revoke-all", "alice", "--json");

        Assert.Equal(AdminExitCode.Ok, exit);
        Assert.Equal(1, JsonSerializer.Deserialize<JsonElement>(cli.Output).GetProperty("revoked").GetInt32());
        Assert.Null(await cli.Tokens.ValidateAsync(plaintext, CancellationToken.None));
    }

    [Fact]
    public async Task Forwards_reconcile_needs_the_platform_that_materializes_them()
    {
        // No IPortForwardManager is registered off Windows, and saying so beats a silent "0 repaired".
        var cli = new Cli();

        var exit = await cli.RunAsync("forwards", "reconcile");

        Assert.Equal(AdminExitCode.Failed, exit);
        Assert.Contains("Windows host", cli.Error);
    }

    [Fact]
    public async Task Forwards_reconcile_reports_what_it_repaired()
    {
        var cli = new Cli(services => services.AddSingleton<IPortForwardManager>(
            new InMemoryPortForwardManager(
                new MutableClock(),
                new InMemoryVmRepository(),
                new InMemoryForwardStore(),
                new PortRangeOptions(2201, 2299),
                new PortRangeOptions(2300, 2999))));

        var exit = await cli.RunAsync("forwards", "reconcile", "--json");

        Assert.Equal(AdminExitCode.Ok, exit);
        Assert.Equal(0, JsonSerializer.Deserialize<JsonElement>(cli.Output).GetProperty("repaired").GetInt32());
    }

    [Fact]
    public async Task An_unknown_command_prints_usage_rather_than_doing_something_else()
    {
        var cli = new Cli();

        Assert.Equal(AdminExitCode.Usage, await cli.RunAsync("users", "promote", "alice"));
        Assert.Equal(AdminExitCode.Usage, await cli.RunAsync("nonsense"));
    }

    [Fact]
    public async Task No_arguments_prints_the_usage_and_fails()
    {
        var cli = new Cli();

        Assert.Equal(AdminExitCode.Usage, await cli.RunAsync());
        Assert.Contains("users add", cli.Output);
        Assert.Contains("Exit codes", cli.Output);
    }

    [Fact]
    public async Task Help_prints_the_usage_and_succeeds()
    {
        var cli = new Cli();

        Assert.Equal(AdminExitCode.Ok, await cli.RunAsync("--help"));
        Assert.Contains("tokens issue", cli.Output);
    }

    /// <summary>The CLI over in-memory stores, with its two output streams captured.</summary>
    private sealed class Cli
    {
        private readonly ServiceProvider _services;
        private StringWriter _output = new();
        private StringWriter _error = new();

        public Cli(Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(new MutableClock());
            services.AddSingleton<InMemoryUserStore>();
            services.AddSingleton<IUserStore>(sp => sp.GetRequiredService<InMemoryUserStore>());
            services.AddSingleton<InMemoryVmRepository>();
            services.AddSingleton<IVmRepository>(sp => sp.GetRequiredService<InMemoryVmRepository>());
            services.AddSingleton<InMemoryAuditLog>();
            services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<InMemoryAuditLog>());
            services.AddSingleton<InMemoryTokenService>();
            services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<InMemoryTokenService>());
            configure?.Invoke(services);

            _services = services.BuildServiceProvider();
        }

        public IUserStore Users => _services.GetRequiredService<IUserStore>();

        public ITokenService Tokens => _services.GetRequiredService<ITokenService>();

        public IAuditLog Audit => _services.GetRequiredService<IAuditLog>();

        public string Output => _output.ToString();

        public string Error => _error.ToString();

        public void Reset()
        {
            _output = new StringWriter();
            _error = new StringWriter();
        }

        public Task<int> RunAsync(params string[] args) =>
            AdminCli.RunAsync(args, _services, _output, _error, CancellationToken.None);

        public Task AddVmAsync(string name, string owner) =>
            _services.GetRequiredService<IVmRepository>().AddAsync(
                new Vm(name, owner, 4, 8, 100, DateTimeOffset.UtcNow, VmState.Running, null, null,
                    new IdlePolicy(120, IdleAction.Save), Vm.NoForwards),
                maxVms: 10,
                CancellationToken.None);
    }
}
