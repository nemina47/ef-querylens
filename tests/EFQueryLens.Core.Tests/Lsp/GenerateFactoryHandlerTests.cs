using EFQueryLens.Core.Tests.Lsp.Fakes;
using EFQueryLens.Lsp.Handlers;
using Newtonsoft.Json.Linq;

namespace EFQueryLens.Core.Tests.Lsp;

public class GenerateFactoryHandlerTests
{
    [Fact]
    public async Task HandleAsync_MissingAssemblyPath_ReturnsFailure()
    {
        var handler = new GenerateFactoryHandler(new TestQueryLensEngine());

        var result = await handler.HandleAsync(new JObject(), CancellationToken.None);

        Assert.False(result["success"]!.Value<bool>());
        Assert.Contains("assemblyPath", result["message"]!.Value<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_EngineThrows_ReturnsFailureMessage()
    {
        var engine = new TestQueryLensEngine { ThrowOnGenerateFactory = true };
        var handler = new GenerateFactoryHandler(engine);

        var result = await handler.HandleAsync(
            new JObject { ["assemblyPath"] = "C:/app/app.dll", ["dbContextTypeName"] = "MyDb" },
            CancellationToken.None);

        Assert.False(result["success"]!.Value<bool>());
        Assert.Contains("factory failed", result["message"]!.Value<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_Success_ReturnsPayload()
    {
        var handler = new GenerateFactoryHandler(new TestQueryLensEngine());

        var result = await handler.HandleAsync(
            new JObject { ["assemblyPath"] = "C:/app/app.dll", ["dbContextTypeName"] = "MyDb" },
            CancellationToken.None);

        Assert.True(result["success"]!.Value<bool>());
        Assert.Equal("// generated", result["content"]!.Value<string>());
        Assert.Equal("Factory.cs", result["suggestedFileName"]!.Value<string>());
        Assert.Equal("MyDb", result["dbContextTypeFullName"]!.Value<string>());
    }
}
