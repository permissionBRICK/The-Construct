using Construct.Companion.Core.Abstractions;
using Construct.Companion.Host.ConfigSync;
using Construct.Companion.Host.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Companion.Tests.ConfigSync;

public sealed class CompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TemporaryGitIndexPreservesMainIndexInEitherRegistrationOrder(bool runtimeFirst)
    {
        var services = new ServiceCollection();
        if (runtimeFirst) services.AddRuntime().AddConfigSync();
        else services.AddConfigSync().AddRuntime();
        using var provider = services.BuildServiceProvider();
        using var workspace = new GitTestWorkspace();
        var git = new GitRunner(provider.GetRequiredService<IProcessRunner>());
        var directory = workspace.Repo.Directory;
        await git.RequireAsync(directory, ["init", "--initial-branch=main"]);
        workspace.Host("baseline");
        workspace.Host("alternate");
        await git.RequireAsync(directory, ["add", "projects/baseline.json"]);
        var mainIndex = Path.Combine(directory, ".git", "index");
        var original = File.ReadAllBytes(mainIndex);
        var temporaryIndex = Path.Combine(directory, ".git", "integration-index");

        await git.RequireAsync(directory, ["read-tree", "--empty"], index: temporaryIndex);
        await git.RequireAsync(directory, ["add", "projects/alternate.json"], index: temporaryIndex);

        Assert.Equal("projects/alternate.json", await git.RequireAsync(directory, ["ls-files"], index: temporaryIndex));
        Assert.Equal("projects/baseline.json", await git.RequireAsync(directory, ["ls-files"]));
        Assert.Equal(original, File.ReadAllBytes(mainIndex));
    }
}
