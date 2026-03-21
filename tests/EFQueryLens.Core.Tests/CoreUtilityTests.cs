using System.Text;
using EFQueryLens.Core.Common;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Contracts.Explain;
using EFQueryLens.Core.Daemon;
using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests;

public class CoreUtilityTests
{
    // ─── DaemonWorkspaceIdentity ──────────────────────────────────────────────

    [Fact]
    public void NormalizeWorkspacePath_TrailingSeparator_IsTrimmed()
    {
        var path = Path.Combine(Path.GetTempPath(), "ql-test-workspace") + Path.DirectorySeparatorChar;
        var result = DaemonWorkspaceIdentity.NormalizeWorkspacePath(path);
        Assert.False(result.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeWorkspacePath_RelativePath_IsResolvedToAbsolute()
    {
        var result = DaemonWorkspaceIdentity.NormalizeWorkspacePath(".");
        Assert.True(Path.IsPathRooted(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeWorkspacePath_NullOrWhitespace_Throws(string? path)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        // and ArgumentException for empty/whitespace — both are ArgumentException subtypes.
        Assert.ThrowsAny<ArgumentException>(() => DaemonWorkspaceIdentity.NormalizeWorkspacePath(path!));
    }

    [Fact]
    public void ComputeWorkspaceHash_SamePath_ReturnsSameHash()
    {
        var path = Path.Combine(Path.GetTempPath(), "ql-test-hash");
        var hash1 = DaemonWorkspaceIdentity.ComputeWorkspaceHash(path);
        var hash2 = DaemonWorkspaceIdentity.ComputeWorkspaceHash(path);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeWorkspaceHash_Returns12HexCharacters()
    {
        var hash = DaemonWorkspaceIdentity.ComputeWorkspaceHash(Path.GetTempPath());
        Assert.Equal(12, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    [Fact]
    public void ComputeWorkspaceHash_DifferentPaths_ReturnDifferentHashes()
    {
        var hash1 = DaemonWorkspaceIdentity.ComputeWorkspaceHash(@"C:\ProjectA");
        var hash2 = DaemonWorkspaceIdentity.ComputeWorkspaceHash(@"C:\ProjectB");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void BuildPidFilePath_ContainsWorkspaceHashAndJsonExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), "ql-pid-test");
        var hash = DaemonWorkspaceIdentity.ComputeWorkspaceHash(path);

        var pidPath = DaemonWorkspaceIdentity.BuildPidFilePath(path);

        Assert.Contains(hash, pidPath, StringComparison.Ordinal);
        Assert.EndsWith(".json", pidPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPidFilePath_IsUnderQueryLensPidsDirectory()
    {
        var pidPath = DaemonWorkspaceIdentity.BuildPidFilePath(Path.GetTempPath());
        Assert.Contains(".querylens", pidPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pids", pidPath, StringComparison.OrdinalIgnoreCase);
    }

    // ─── TimestampedTextWriter ────────────────────────────────────────────────

    [Fact]
    public void TimestampedTextWriter_WriteLine_PrependsBracketedTimestamp()
    {
        var sb = new StringBuilder();
        using var inner = new StringWriter(sb);
        using var writer = new TimestampedTextWriter(inner);

        writer.WriteLine("hello");

        var output = sb.ToString();
        Assert.StartsWith("[", output, StringComparison.Ordinal);
        Assert.Contains("hello", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimestampedTextWriter_WriteLineAsync_PrependsBracketedTimestamp()
    {
        var sb = new StringBuilder();
        var inner = new StringWriter(sb);
        await using var writer = new TimestampedTextWriter(inner);

        await writer.WriteLineAsync("async-message");

        var output = sb.ToString();
        Assert.StartsWith("[", output, StringComparison.Ordinal);
        Assert.Contains("async-message", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TimestampedTextWriter_WriteChar_DelegatesToInner()
    {
        var sb = new StringBuilder();
        using var inner = new StringWriter(sb);
        using var writer = new TimestampedTextWriter(inner);

        writer.Write('X');

        Assert.Equal("X", sb.ToString());
    }

    [Fact]
    public void TimestampedTextWriter_Encoding_ReturnsInnerEncoding()
    {
        using var inner = new StringWriter();
        using var writer = new TimestampedTextWriter(inner);

        Assert.Equal(inner.Encoding, writer.Encoding);
    }

    [Fact]
    public void TimestampedTextWriter_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TimestampedTextWriter(null!));
    }

    // ─── ProjectKeyHelper ─────────────────────────────────────────────────────

    [Fact]
    public void ProjectKeyHelper_GetProjectKey_WhenCsprojInParentDir_ReturnsConsistentHash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ql-projectkey-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write a .csproj in the temp directory
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), "<Project />");

            var sourceFile = Path.Combine(tempDir, "SomeClass.cs");
            File.WriteAllText(sourceFile, "class C {}");

            var key1 = ProjectKeyHelper.GetProjectKey(sourceFile);
            var key2 = ProjectKeyHelper.GetProjectKey(sourceFile);

            Assert.False(string.IsNullOrWhiteSpace(key1));
            Assert.Equal(key1, key2); // Same input → same key (cached)
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ProjectKeyHelper_GetProjectKey_Returns12HexCharacters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ql-projectkey2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), "<Project />");
            var sourceFile = Path.Combine(tempDir, "SomeClass.cs");
            File.WriteAllText(sourceFile, "class C {}");

            var key = ProjectKeyHelper.GetProjectKey(sourceFile);

            Assert.Equal(12, key.Length);
            Assert.Matches("^[0-9a-f]+$", key);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ProjectKeyHelper_GetProjectKey_WhenNoCsproj_ReturnsFallbackHash()
    {
        // Use a path in a temp subdir that has no .csproj anywhere in the chain
        // (within the isolated temp subdirectory — the real temp dir won't have one either)
        var isolated = Path.Combine(Path.GetTempPath(), "ql-noproj-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(isolated);

        try
        {
            var sourceFile = Path.Combine(isolated, "NoProject.cs");
            File.WriteAllText(sourceFile, "class C {}");

            var key = ProjectKeyHelper.GetProjectKey(sourceFile);

            // Should return a non-empty fallback hash — length 12 hex chars
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.Equal(12, key.Length);
        }
        finally
        {
            try { Directory.Delete(isolated, recursive: true); } catch { /* best-effort */ }
        }
    }

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
    [InlineData("true",  true)]
    [InlineData("True",  true)]
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
    [InlineData("1",   true)]
    [InlineData("yes", true)]
    [InlineData("on",  true)]
    [InlineData("0",   false)]
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
