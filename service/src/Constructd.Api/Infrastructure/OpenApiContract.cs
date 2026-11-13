using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Constructd.Api.Infrastructure;

/// <summary>
/// Marks a request or response property that carries a secret (a token, a key, a password). The
/// OpenAPI document flags it with <c>x-construct-secret: true</c>, so generated clients and the audit
/// know not to log it (docs/plans/construct-platform-design.md 6.1).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true)]
public sealed class ConstructSecretAttribute : Attribute;

/// <summary>
/// The API contract as an OpenAPI 3.1 document (<c>service/openapi/constructd.v1.json</c>). The
/// document is generated from the routes and their typed records; nothing here changes the wire
/// format. Regenerate the committed file with
/// <c>CONSTRUCTD_UPDATE_OPENAPI=1 dotnet test service/Constructd.sln --filter OpenApiDocumentTests</c>.
/// </summary>
public static class OpenApiContract
{
    public const string DocumentName = "v1";
    public const string SecretExtension = "x-construct-secret";
    public const string ProblemSchema = "Problem";

    public static IServiceCollection AddConstructdOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            // Several endpoint classes nest a record called Request or View; the declaring class keeps
            // their schema names apart (PrimaryCpuEndpointsRequest, VmNetworkSettingsView).
            options.CreateSchemaReferenceId = type =>
                OpenApiOptions.CreateDefaultSchemaReferenceId(type) is { } id && type.Type.DeclaringType is { } outer && !type.Type.IsGenericType
                    ? outer.Name + id
                    : OpenApiOptions.CreateDefaultSchemaReferenceId(type);
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "constructd",
                    Version = "v1",
                    Description = "The Construct host service API. Every error answer is a Problem document.",
                };
                // The address depends on the install; clients supply it.
                document.Servers = [];
                document.Components ??= new OpenApiComponents();
                document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
                document.Components.Schemas[ProblemSchema] = Problem();
                // A JsonElement in the record (absent keeps, null clears); on the wire it is a boolean or null.
                if (document.Components.Schemas.TryGetValue(nameof(Contracts.UserUpdateRequest), out var update) && update.Properties is { } properties)
                    properties["allowNested"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Boolean | JsonSchemaType.Null,
                        Description = "true or false sets it, null clears it, absent keeps it.",
                    };
                // GET /host/config writes each section's fields with the section's source and updatedAt beside them.
                if (document.Components.Schemas.TryGetValue(nameof(Contracts.HostConfigResponse), out var config) && config.Properties is { } sections)
                {
                    document.Components.Schemas["HostConfigSectionMeta"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "source", "updatedAt" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["source"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("default")!, JsonValue.Create("stored")!],
                                Description = "default: the section was never stored; stored: an admin saved it." },
                            ["updatedAt"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, Format = "date-time" },
                        },
                    };
                    foreach (var name in sections.Keys.ToArray())
                        sections[name] = new OpenApiSchema { AllOf = [sections[name], new OpenApiSchemaReference("HostConfigSectionMeta")] };
                }
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, _, _) =>
            {
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("default", new OpenApiResponse
                {
                    Description = "An error, as a Problem document.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(ProblemSchema) },
                    },
                });
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer((schema, context, _) =>
            {
                // The service also reads numbers written as strings (JsonNumberHandling.AllowReadingFromString),
                // which the exporter states as type [integer, string] plus a digits pattern. The contract
                // describes the form the service writes and clients should send: a plain number.
                if (schema.Type is { } type && type.HasFlag(JsonSchemaType.String) &&
                    (type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number)))
                {
                    schema.Type = type & ~JsonSchemaType.String;
                    schema.Pattern = null;
                }

                // Enums are camelCase strings on the wire (ApiJson). The exporter shares one schema between
                // nullable and plain uses, without a type and with null among the values; nullability is
                // stated where the schema is referenced, so the shared one is a plain string enum.
                var clr = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
                if (clr.IsEnum && schema.Enum is { Count: > 0 } values)
                {
                    schema.Type = JsonSchemaType.String;
                    schema.Enum = [.. values.Where(value => value is not null)];
                }

                if (context.JsonPropertyInfo?.AttributeProvider?.IsDefined(typeof(ConstructSecretAttribute), true) == true)
                {
                    schema.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                    schema.Extensions[SecretExtension] = new JsonNodeExtension(JsonValue.Create(true));
                }

                return Task.CompletedTask;
            });
        });

    /// <summary>
    /// The error shape as it is today (<see cref="CodedProblems"/>, <see cref="Problems"/>): an RFC 9457
    /// problem document. <c>code</c> is set on coded problems; <c>title</c> repeats it there and is a
    /// human heading otherwise; <c>detail</c> is the message. Further members depend on the route.
    /// </summary>
    private static OpenApiSchema Problem() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "An RFC 9457 problem document. Routes may add further members.",
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["type"] = Text("urn:construct:problem:<code> on coded problems."),
            ["title"] = Text("The code on coded problems, a short heading otherwise."),
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Description = "The HTTP status." },
            ["detail"] = Text("The message: what went wrong, for a person."),
            ["code"] = Text("A stable, machine-readable error code (CodedProblems). Absent on older uncoded answers."),
            ["field"] = Text("On validation problems: the request field at fault."),
            ["reason"] = Text("On validation problems: why the field was refused."),
            ["retryAfterSeconds"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int32",
                Description = "When the request may be retried; the Retry-After header carries the same value.",
            },
        },
        AdditionalPropertiesAllowed = true,
    };

    private static OpenApiSchema Text(string description) => new() { Type = JsonSchemaType.String, Description = description };
}
