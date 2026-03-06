using QueryLens.Lsp.Parsing;

namespace QueryLens.Core.Tests.Lsp;

public class LspSyntaxHelperTests
{
    [Fact]
    public void TryExtractLinqExpression_UsesRootContextVariable_ForComplexChain()
    {
        var source = """
            var query = context .MedicsAccountRoles.AsNoTracking()
                .Where(s => s.IsNotDeleted && s.AccountId == accountId)
                .Select(s => new { s.MedicsRole.RoleType, s.MedicsRole.WorkflowType })
                .Distinct();
            """;

        var (line, character) = FindPosition(source, "Distinct");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("context", contextVariableName);
        Assert.StartsWith("context", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HoverInsideLambda_StillUsesRootContextVariable()
    {
        var source = """
            var query = context.MedicsAccountRoles
                .Where(s => s.IsNotDeleted && s.AccountId == accountId)
                .Select(s => s.MedicsRole.RoleType)
                .Distinct();
            """;

        var (line, character) = FindPosition(source, "AccountId");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("context", contextVariableName);
        Assert.StartsWith("context", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractUsingContext_CollectsImportsAliasesAndStaticUsings()
    {
        var source = """
            using System.Linq;
            using Enums = Share.Medics.Applications.Core.Entities.Enums;
            using static System.Math;

            namespace Demo;

            internal sealed class C;
            """;

        var context = LspSyntaxHelper.ExtractUsingContext(source);

        Assert.Contains("System.Linq", context.Imports);
        Assert.Contains("System.Math", context.StaticTypes);
        Assert.True(context.Aliases.TryGetValue("Enums", out var aliasTarget));
        Assert.Equal("Share.Medics.Applications.Core.Entities.Enums", aliasTarget);
    }

    [Fact]
    public void ExtractUsingContext_DeduplicatesRepeatedImports()
    {
        var source = """
            using System.Linq;
            using System.Linq;
            using static System.Math;
            using static System.Math;

            namespace Demo;

            internal sealed class C;
            """;

        var context = LspSyntaxHelper.ExtractUsingContext(source);

        Assert.Single(context.Imports, i => i == "System.Linq");
        Assert.Single(context.StaticTypes, s => s == "System.Math");
    }

    private static (int line, int character) FindPosition(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Marker '{marker}' not found in source text.");

        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return (line, character);
    }
}
