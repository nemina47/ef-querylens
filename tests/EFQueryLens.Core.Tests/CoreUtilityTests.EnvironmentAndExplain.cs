using EFQueryLens.Core.Common;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Contracts.Explain;

namespace EFQueryLens.Core.Tests;

public partial class CoreUtilityTests
{
    // ─── EnvironmentVariableParser ────────────────────────────────────────────

    [Fact]
    public void EnvironmentVariableParser_ReadBool_WhenNotSet_ReturnsFallback()
    {
        var varName = "QL_TEST_BOOL_UNSET_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(varName, null);

        Assert.False(EnvironmentVariableParser.ReadBool(varName, fallback: false));
        Assert.True(EnvironmentVariableParser.ReadBool(varName, fallback: true));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void EnvironmentVariableParser_ReadBool_BoolParseable_ReturnsParsedValue(string raw, bool expected)
    {
        var varName = "QL_TEST_BOOL_PARSE_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Environment.SetEnvironmentVariable(varName, raw);
            Assert.Equal(expected, EnvironmentVariableParser.ReadBool(varName, fallback: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    public void EnvironmentVariableParser_ReadBool_AlternativeTruthyStrings(string raw, bool expected)
    {
        var varName = "QL_TEST_BOOL_ALT_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Environment.SetEnvironmentVariable(varName, raw);
            Assert.Equal(expected, EnvironmentVariableParser.ReadBool(varName, fallback: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void EnvironmentVariableParser_ReadInt_WhenNotSet_ReturnsFallback()
    {
        var varName = "QL_TEST_INT_UNSET_" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(42, EnvironmentVariableParser.ReadInt(varName, fallback: 42, min: 0, max: 100));
    }

    [Fact]
    public void EnvironmentVariableParser_ReadInt_ValidValue_ReturnsParsedValue()
    {
        var varName = "QL_TEST_INT_VALID_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Environment.SetEnvironmentVariable(varName, "55");
            Assert.Equal(55, EnvironmentVariableParser.ReadInt(varName, fallback: 0, min: 0, max: 100));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void EnvironmentVariableParser_ReadInt_BelowMin_ClampsToMin()
    {
        var varName = "QL_TEST_INT_MIN_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Environment.SetEnvironmentVariable(varName, "-10");
            Assert.Equal(0, EnvironmentVariableParser.ReadInt(varName, fallback: 5, min: 0, max: 100));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void EnvironmentVariableParser_ReadInt_AboveMax_ClampsToMax()
    {
        var varName = "QL_TEST_INT_MAX_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Environment.SetEnvironmentVariable(varName, "999");
            Assert.Equal(100, EnvironmentVariableParser.ReadInt(varName, fallback: 5, min: 0, max: 100));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void EnvironmentVariableParser_ReadInt_NonNumericValue_ReturnsFallback()
    {
        var varName = "QL_TEST_INT_NAN_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Environment.SetEnvironmentVariable(varName, "not-a-number");
            Assert.Equal(42, EnvironmentVariableParser.ReadInt(varName, fallback: 42, min: 0, max: 100));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    // ─── ExplainNode ──────────────────────────────────────────────────────────

    [Fact]
    public void ExplainNode_RowEstimateAccuracy_WhenActualRowsNull_ReturnsNull()
    {
        var node = new ExplainNode
        {
            OperationType = "Scan",
            EstimatedRows = 100,
            ActualRows = null,
        };

        Assert.Null(node.RowEstimateAccuracy);
    }

    [Fact]
    public void ExplainNode_RowEstimateAccuracy_WhenEstimatedRowsZero_ReturnsNull()
    {
        var node = new ExplainNode
        {
            OperationType = "Scan",
            EstimatedRows = 0,
            ActualRows = 50,
        };

        Assert.Null(node.RowEstimateAccuracy);
    }

    [Fact]
    public void ExplainNode_RowEstimateAccuracy_WhenBothSet_ReturnsRatio()
    {
        var node = new ExplainNode
        {
            OperationType = "Scan",
            EstimatedRows = 100,
            ActualRows = 200,
        };

        Assert.Equal(2.0, node.RowEstimateAccuracy!.Value, precision: 5);
    }

    [Fact]
    public void ExplainNode_DefaultChildrenAndWarnings_AreEmpty()
    {
        var node = new ExplainNode { OperationType = "Test" };

        Assert.Empty(node.Children);
        Assert.Empty(node.Warnings);
    }

    // ─── ExplainResult ────────────────────────────────────────────────────────

    [Fact]
    public void ExplainResult_DelegatesPropertiesToTranslation()
    {
        var translation = new QueryTranslationResult
        {
            Success = true,
            Sql = "SELECT 1",
            ErrorMessage = null,
            Metadata = new TranslationMetadata
            {
                DbContextType = "MyCtx",
                ProviderName = "MySql",
                EfCoreVersion = "9.0",
                TranslationTime = TimeSpan.FromMilliseconds(50),
            },
        };

        var result = new ExplainResult
        {
            Translation = translation,
            IsActualExecution = false,
        };

        Assert.True(result.Success);
        Assert.Equal("SELECT 1", result.Sql);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("MyCtx", result.Metadata.DbContextType);
    }

    [Fact]
    public void ExplainResult_WithPlan_ExposesPlanAndServerVersion()
    {
        var plan = new ExplainNode { OperationType = "Index Scan", EstimatedRows = 10 };
        var result = new ExplainResult
        {
            Translation = new QueryTranslationResult { Success = true, Metadata = new TranslationMetadata() },
            Plan = plan,
            IsActualExecution = true,
            ServerVersion = "8.0.32",
        };

        Assert.Same(plan, result.Plan);
        Assert.True(result.IsActualExecution);
        Assert.Equal("8.0.32", result.ServerVersion);
    }
}
