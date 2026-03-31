using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task Evaluate_WhereClauseReceivesExpressionPredicateVariable_ReturnsSql()
    {
        var result = await _evaluator.EvaluateAsync(_alcCtx, new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            DbContextTypeName = DefaultMySqlDbContextType,
            Expression = "db.Orders.Where(filter).ToListAsync(ct)",
            LocalVariableTypes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["filter"] = "System.Linq.Expressions.Expression<System.Func<SampleMySqlApp.Domain.Entities.Order, bool>>",
            },
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WhereClauseReceivesShortExpressionPredicateVariable_ReturnsSql()
    {
        var result = await _evaluator.EvaluateAsync(_alcCtx, new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            DbContextTypeName = DefaultMySqlDbContextType,
            Expression = "db.Orders.Where(filter).ToListAsync(ct)",
            AdditionalImports = ["System.Linq.Expressions"],
            LocalVariableTypes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["filter"] = "Expression<Func<SampleMySqlApp.Domain.Entities.Order, bool>>",
            },
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WhereWithParam_ParsesParameters()
    {
        var result = await TranslateAsync("db.Orders.Where(o => o.UserId == 5)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);

        if (result.Parameters.Count > 0)
        {
            var p = result.Parameters[0];
            Assert.StartsWith("@", p.Name);
            Assert.Equal("5", p.InferredValue);
        }
        else
        {
            Assert.Contains("5", result.Sql, StringComparison.Ordinal);
        }
    }
}
