using System.Text.Json.Nodes;
using Construct.Agent.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Construct.Agent.Tests;

/// <summary>
/// The committed contract (<c>agent/openapi/agent.v1.json</c>) is exactly what the agent generates.
/// Regenerate it with <c>CONSTRUCT_AGENT_UPDATE_OPENAPI=1 dotnet test agent/Construct.Agent.slnx --filter OpenApiDocumentTests</c>.
/// </summary>
public sealed class OpenApiDocumentTests
{
    private const string UpdateVariable = "CONSTRUCT_AGENT_UPDATE_OPENAPI";

    [Fact]
    public async Task The_committed_document_matches_the_agent()
    {
        var generated = await GenerateAsync();
        var path = CommittedPath();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1") await File.WriteAllTextAsync(path, generated);

        Assert.True(File.Exists(path), $"{path} is missing. Generate it with {UpdateVariable}=1 dotnet test agent/Construct.Agent.slnx --filter OpenApiDocumentTests.");
        Assert.True((await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n") == generated,
            $"{path} differs from what the agent generates. Regenerate it with {UpdateVariable}=1 dotnet test agent/Construct.Agent.slnx --filter OpenApiDocumentTests and commit it.");
    }

    [Fact]
    public async Task Secrets_are_marked_and_every_operation_has_the_problem_shape()
    {
        var document = JsonNode.Parse(await GenerateAsync())!;
        Assert.StartsWith("3.1", document["openapi"]!.GetValue<string>());
        var schemas = document["components"]!["schemas"]!;
        Assert.True(schemas["RecoveryCodeResponse"]!["properties"]!["recoveryCode"]![AgentOpenApi.SecretExtension]!.GetValue<bool>());
        Assert.True(schemas["AddEngineRequest"]!["properties"]!["userToken"]![AgentOpenApi.SecretExtension]!.GetValue<bool>());
        foreach (var (_, item) in document["paths"]!.AsObject())
        foreach (var (_, operation) in item!.AsObject())
            Assert.NotNull(operation!["responses"]!["default"]);

        // No view type carries a token or a key.
        foreach (var name in new[] { "DeviceView", "EngineView", "AgentStateView", "PairingView" })
        {
            foreach (var (property, _) in schemas[name]!["properties"]!.AsObject())
                Assert.DoesNotContain("token", property, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string> GenerateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-test-openapi-" + Guid.NewGuid().ToString("n"));
        try
        {
            await using var host = Construct.Agent.Http.AgentHost.Create(new AgentOptions { DataDirectory = directory, Protection = "file", PublishEndpoint = false });
            // The routes reach the document's data source when the pipeline is built, at start.
            await host.StartAsync();
            var provider = host.App.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(AgentOpenApi.DocumentName);
            var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None);
            return (await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1)).ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CommittedPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "agent", "Construct.Agent.slnx")))
                return Path.Combine(directory.FullName, "agent", "openapi", "agent.v1.json");
        }

        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
