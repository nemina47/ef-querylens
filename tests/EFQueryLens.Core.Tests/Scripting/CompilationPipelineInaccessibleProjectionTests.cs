using System.Collections.Generic;
using EFQueryLens.Core.Scripting.Evaluation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace EFQueryLens.Core.Tests.Scripting;

/// <summary>
/// Regression tests for compile-retry projection normalization when inaccessible DTOs
/// are created with object initializer syntax.
/// </summary>
public class CompilationPipelineInaccessibleProjectionTests
{
    [Fact]
    public void TryNormalizeInaccessibleProjectionTypeFromErrors_ObjectInitializerProjection_PreservesInitializerMembers()
    {
        var diagnostics = new List<Diagnostic>
        {
            Diagnostic.Create(
                new DiagnosticDescriptor(
                    "CS0122",
                    "Inaccessible type",
                    "type is inaccessible",
                    "Compiler",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                Location.None),
        };

        var expression = "db.Orders.Select(o => new PrivateDto { Id = o.Id, Name = o.Customer.Name })";

        var changed = CompilationPipeline.TryNormalizeInaccessibleProjectionTypeFromErrors(
            diagnostics,
            expression,
            out var normalizedExpression);

        Assert.True(changed);
        Assert.Contains("Select(o => new", normalizedExpression, StringComparison.Ordinal);
        Assert.Contains("Id=o.Id", normalizedExpression, StringComparison.Ordinal);
        Assert.Contains("Name=o.Customer.Name", normalizedExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("__ql0 = null", normalizedExpression, StringComparison.Ordinal);
    }
}
