using System.Text.Json.Nodes;
using Constructd.Api.Infrastructure;
using Constructd.Tests.Support;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Constructd.Tests.Api;

/// <summary>
/// The committed contract (<c>service/openapi/constructd.v1.json</c>) is exactly what the service
/// generates. Regenerate it with
/// <c>CONSTRUCTD_UPDATE_OPENAPI=1 dotnet test service/Constructd.sln --filter OpenApiDocumentTests</c>.
/// </summary>
public class OpenApiDocumentTests
{
    public const string UpdateVariable = "CONSTRUCTD_UPDATE_OPENAPI";

    [Fact]
    public async Task CommittedDocumentMatchesTheService()
    {
        var generated = await GenerateAsync();
        var path = CommittedPath();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, generated);
        }

        Assert.True(File.Exists(path), $"{path} is missing. Generate it with {UpdateVariable}=1 dotnet test service/Constructd.sln --filter OpenApiDocumentTests.");
        var committed = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        Assert.True(committed == generated,
            $"{path} differs from what the service generates. Regenerate it with {UpdateVariable}=1 dotnet test service/Constructd.sln --filter OpenApiDocumentTests and commit the result.");
    }

    [Fact]
    public async Task DocumentIsOpenApi31WithTheProblemShapeOnEveryOperation()
    {
        var document = JsonNode.Parse(await GenerateAsync())!;

        Assert.StartsWith("3.1", document["openapi"]!.GetValue<string>());
        var problem = document["components"]!["schemas"]![OpenApiContract.ProblemSchema]!;
        foreach (var member in new[] { "code", "detail", "title", "status", "retryAfterSeconds" })
        {
            Assert.NotNull(problem["properties"]![member]);
        }

        var operations = 0;
        foreach (var (_, path) in document["paths"]!.AsObject())
        {
            foreach (var (_, operation) in path!.AsObject())
            {
                operations++;
                Assert.Equal("#/components/schemas/Problem",
                    operation!["responses"]!["default"]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>());
            }
        }

        Assert.True(operations > 100, $"Only {operations} operations in the document.");
    }

    [Fact]
    public async Task SecretFieldsAreMarked()
    {
        var schemas = JsonNode.Parse(await GenerateAsync())!["components"]!["schemas"]!;

        Assert.True(IsSecret(schemas["TokenIssuedResponse"]!, "token"));
        Assert.False(IsSecret(schemas["TokenIssuedResponse"]!, "label"));
    }

    internal static bool IsSecret(JsonNode schema, string property) =>
        schema["properties"]![property]![OpenApiContract.SecretExtension]?.GetValue<bool>() == true;

    internal static async Task<string> GenerateAsync()
    {
        using var app = new TestApp();
        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(OpenApiContract.DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None);
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1);
        return json.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
    }

    internal static string CommittedPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "service", "Constructd.sln")))
            {
                return Path.Combine(directory.FullName, "service", "openapi", "constructd.v1.json");
            }
        }

        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
