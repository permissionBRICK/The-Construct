using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Construct.Agent.Http;

/// <summary>Marks a property that carries a secret; the document flags it with <c>x-construct-secret: true</c> (6.1).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ConstructSecretAttribute : Attribute;

/// <summary>
/// The local API as an OpenAPI 3.1 document (<c>agent/openapi/agent.v1.json</c>), generated from the
/// routes the way the service's is; a test fails when the committed file differs.
/// </summary>
public static class AgentOpenApi
{
    public const string DocumentName = "agent-v1";
    public const string SecretExtension = "x-construct-secret";
    public const string ProblemSchema = "AgentProblem";

    public static IServiceCollection AddAgentOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "construct-agent",
                    Version = "v1",
                    Description =
                        "The device agent's local API on loopback (http://127.0.0.1:<port>, both in the token file). Every route but GET /api/health " +
                        "takes `Authorization: Bearer <token>`. Engine calls go through the proxy `/api/engines/{id}/api/v1/...`, which forwards to " +
                        "that engine's `/api/v1/...` (its own contract, service/openapi/constructd.v1.json) over the pinned connection with this " +
                        "device's credential. Every error answer is an AgentProblem.",
                };
                document.Servers = [];
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, _, _) =>
            {
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("default", new OpenApiResponse
                {
                    Description = "An error.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(ProblemSchema) },
                    },
                });
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer((document, context, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
                document.Components.Schemas[ProblemSchema] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Required = new HashSet<string> { "title", "status", "code" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                        ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    },
                };
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer((schema, context, _) =>
            {
                // Numbers are written as numbers; the exporter's [integer, string] form is reduced to the written one.
                if (schema.Type is { } type && type.HasFlag(JsonSchemaType.String) && (type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number)))
                {
                    schema.Type = type & ~JsonSchemaType.String;
                    schema.Pattern = null;
                }

                if (context.JsonPropertyInfo?.AttributeProvider is ICustomAttributeProvider provider &&
                    provider.IsDefined(typeof(ConstructSecretAttribute), true))
                {
                    schema.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                    schema.Extensions[SecretExtension] = new JsonNodeExtension(JsonValue.Create(true));
                }

                return Task.CompletedTask;
            });
        });
}
