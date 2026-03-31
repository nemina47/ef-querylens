using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Engine;
using Newtonsoft.Json.Linq;

namespace EFQueryLens.Lsp.Handlers;

internal sealed class GenerateFactoryHandler
{
    private readonly IQueryLensEngine _engine;

    public GenerateFactoryHandler(IQueryLensEngine engine)
    {
        _engine = engine;
    }

    public async Task<JObject> HandleAsync(JObject request, CancellationToken ct)
    {
        var assemblyPath = request["assemblyPath"]?.Value<string>();
        var dbContextTypeName = request["dbContextTypeName"]?.Value<string>();

        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = "Missing required parameter: assemblyPath.",
            };
        }

        try
        {
            var result = await _engine.GenerateFactoryAsync(
                new FactoryGenerationRequest
                {
                    AssemblyPath = assemblyPath,
                    DbContextTypeName = dbContextTypeName,
                },
                ct);

            return new JObject
            {
                ["success"] = true,
                ["content"] = result.Content,
                ["suggestedFileName"] = result.SuggestedFileName,
                ["dbContextTypeFullName"] = result.DbContextTypeFullName,
            };
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = ex.Message,
            };
        }
    }
}
