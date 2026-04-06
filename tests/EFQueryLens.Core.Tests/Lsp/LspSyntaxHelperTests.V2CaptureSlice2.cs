using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
    [Fact]
    public void TryBuildV2CapturePlan_DirectChainLocalValue_UsesReplayInitializer()
    {
        var source = """
            class Demo
            {
                void Run(CancellationToken ct)
                {
                    var minId = 5;
                    _ = dbContext.Users.Where(u => u.Id > minId).ToListAsync(ct);
                }
            }
            """;

        var capturePlan = BuildCapturePlanAtMarker(source, "ToListAsync");

        var minId = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "minId", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.ReplayInitializer, minId.CapturePolicy);
        Assert.Equal("5", minId.InitializerExpression);
        Assert.Empty(minId.Dependencies);
        Assert.Empty(capturePlan.Diagnostics);
    }

    [Fact]
    public void TryBuildV2CapturePlan_QueryExpressionParameter_UsesPlaceholder()
    {
        var source = """
            class Demo
            {
                void Run(Guid customerId, CancellationToken ct)
                {
                    _ = (from u in dbContext.Users
                         where u.CustomerId == customerId
                         select u)
                        .ToListAsync(ct);
                }
            }
            """;

        var capturePlan = BuildCapturePlanAtMarker(source, "where u.CustomerId == customerId");

        var customerId = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "customerId", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, customerId.CapturePolicy);
        Assert.Null(customerId.InitializerExpression);
        Assert.Empty(customerId.Dependencies);
        Assert.Empty(capturePlan.Diagnostics);
    }

    [Fact]
    public void TryBuildV2CapturePlan_HelperComposedExpressionParameter_CapturesDeterministically()
    {
        var source = """
            class Demo
            {
                IQueryable<User> ApplyFilter(Expression<Func<User, bool>> whereExpression)
                {
                    return dbContext.Users.Where(whereExpression);
                }

                void Run(CancellationToken ct)
                {
                    Expression<Func<User, bool>> whereExpression = u => u.IsActive;
                    _ = ApplyFilter(whereExpression).ToListAsync(ct);
                }
            }
            """;

        var capturePlan = BuildCapturePlanAtMarker(source, "ApplyFilter(whereExpression)");

        Assert.Empty(capturePlan.Diagnostics);
        Assert.True(capturePlan.IsComplete);
    }

    [Fact]
    public void TryBuildV2CapturePlan_StringSearchTerm_UsesTypedPlaceholder()
    {
        var source = """
            class Demo
            {
                void Run(string term, CancellationToken ct)
                {
                    _ = dbContext.Customers
                        .Where(c => c.Name.Contains(term) || c.Email.StartsWith(term))
                        .ToListAsync(ct);
                }
            }
            """;

        var capturePlan = BuildCapturePlanAtMarker(source, "ToListAsync");

        var term = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "term", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, term.CapturePolicy);
        Assert.True(
            string.Equals(term.TypeName, "string", StringComparison.OrdinalIgnoreCase)
            || term.TypeName.Contains("System.String", StringComparison.Ordinal),
            $"Expected string capture type for 'term' but got '{term.TypeName}'.");
        Assert.Empty(capturePlan.Diagnostics);
        Assert.True(capturePlan.IsComplete);
    }

    [Fact]
    public void TryBuildV2CapturePlan_FactoryRootSubstitution_DoesNotRequireSyntheticReceiverCapture()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;
            using System.Threading;
            using System.Threading.Tasks;

            class Rationale
            {
                public string Title { get; set; } = string.Empty;
            }

            class ApplicationDbContext : DbContext
            {
                public DbSet<Rationale> Rationales => Set<Rationale>();
            }

            class Demo
            {
                private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

                Demo(IDbContextFactory<ApplicationDbContext> contextFactory)
                {
                    _contextFactory = contextFactory;
                }

                async Task Run(CancellationToken ct)
                {
                    _ = (await _contextFactory.CreateDbContextAsync(ct)).Rationales
                        .AsNoTracking()
                        .OrderBy(x => x.Title)
                        .ToListAsync(ct);
                }
            }
            """;

        var (line, character) = FindPosition(source, "ToListAsync");
        var extractionSuccess = LspSyntaxHelper.TryBuildV2ExtractionPlan(
            source,
            @"c:\repo\Demo.cs",
            line,
            character,
            out var extractionPlan,
            out var extractionDiagnostics);

        Assert.True(extractionSuccess);
        Assert.NotNull(extractionPlan);
        Assert.Empty(extractionDiagnostics);

        var captureSuccess = LspSyntaxHelper.TryBuildV2CapturePlan(
            extractionPlan!.Expression,
            extractionPlan.ContextVariableName,
            source,
            extractionPlan.Origin.Line,
            extractionPlan.Origin.Character,
            targetAssemblyPath: null,
            out var capturePlan,
            dbContextTypeName: "ApplicationDbContext",
            factoryCandidateTypeNames: ["ApplicationDbContext"]);

        Assert.True(captureSuccess);
        Assert.NotNull(capturePlan);
        Assert.True(capturePlan!.IsComplete);
        Assert.Empty(capturePlan.Diagnostics);

        // Synthetic receiver must not appear as an unresolved or captured symbol.
        Assert.DoesNotContain(
            capturePlan.Entries,
            e => string.Equals(e.Name, "__qlFactoryContext", StringComparison.Ordinal));

        // Ensure factory variable is not emitted as placeholder anymore after substitution.
        Assert.DoesNotContain(
            capturePlan.Entries,
            e => string.Equals(e.Name, "_contextFactory", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildV2CapturePlanFromGraph_MissingDependency_RejectedWithDiagnostic()
    {
        var graph = new[]
        {
            new LocalSymbolGraphEntry
            {
                Name = "baseValue",
                TypeName = "global::System.Int32",
                Kind = "local",
                InitializerExpression = "10",
                DeclarationOrder = 1,
                Dependencies = [],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
            new LocalSymbolGraphEntry
            {
                Name = "threshold",
                TypeName = "global::System.Int32",
                Kind = "local",
                InitializerExpression = "baseValue + missingVar",
                DeclarationOrder = 2,
                Dependencies = ["baseValue", "missingVar"],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.Users.Where(u => u.Id > threshold).ToListAsync(ct)",
            graph);

        Assert.False(capturePlan.IsComplete);
        Assert.Contains(capturePlan.Diagnostics, d => d.Code == "QLV2_CAPTURE_MISSING_DEPENDENCY" && d.SymbolName == "threshold");
        Assert.Contains(
            capturePlan.Entries,
            e => string.Equals(e.Name, "threshold", StringComparison.Ordinal)
                && string.Equals(e.CapturePolicy, LocalSymbolReplayPolicies.Reject, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildV2CapturePlanFromGraph_UnsafeInitializer_KnownType_DowngradesToPlaceholder()
    {
        // Mirrors the real live scenario:
        //   var page = Math.Max(request.Page, 1);
        // page: ReplayInitializer, type=int, depends on request (which is UsePlaceholder — in graph).
        // ContainsUnsafeReplayInitializerSyntax fires because Math.Max() is InvocationExpressionSyntax.
        // Expected: downgrade to UsePlaceholder (int is catalog-known); no diagnostic emitted.
        var graph = new LocalSymbolGraphEntry[]
        {
            new()
            {
                Name = "request",
                TypeName = "global::MyApp.OrderQueryRequest",
                Kind = "parameter",
                InitializerExpression = null,
                DeclarationOrder = 0,
                Dependencies = [],
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
            new()
            {
                Name = "page",
                TypeName = "global::System.Int32",
                Kind = "local",
                InitializerExpression = "Math.Max(request.Page, 1)",
                DeclarationOrder = 1,
                Dependencies = ["request"],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.Orders.Skip((page - 1)).Take(10).ToListAsync(ct)",
            graph);

        var page = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "page", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, page.CapturePolicy);
        Assert.Null(page.InitializerExpression);
        Assert.Empty(page.Dependencies);
        // Type preserved for catalog synthesis
        Assert.Contains("Int32", page.TypeName, StringComparison.OrdinalIgnoreCase);
        // No diagnostic for known-type downgrade — it's normal synthesis
        Assert.Empty(capturePlan.Diagnostics);
    }

    [Fact]
    public void BuildV2CapturePlanFromGraph_UnsafeInitializer_UnknownType_StillRejects()
    {
        // Unsafe initializer for a custom type — cannot use IsKnownPlaceholderType → should reject
        var graph = new LocalSymbolGraphEntry[]
        {
            new()
            {
                Name = "filters",
                TypeName = "global::MyApp.FilterBuilder",
                Kind = "local",
                InitializerExpression = "BuildFilters(request)",
                DeclarationOrder = 1,
                Dependencies = ["request"],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.Orders.Where(filters).ToListAsync(ct)",
            graph);

        var filters = Assert.Single(capturePlan.Entries);
        Assert.Equal(LocalSymbolReplayPolicies.Reject, filters.CapturePolicy);
        Assert.False(capturePlan.IsComplete);
    }

    [Theory]
    [InlineData("global::System.Int32")]
    [InlineData("global::System.String")]
    [InlineData("global::System.Guid")]
    [InlineData("global::System.DateTime")]
    [InlineData("global::System.Threading.CancellationToken")]
    [InlineData("global::System.TimeSpan")]
    [InlineData("global::System.Boolean")]
    [InlineData("global::System.Decimal")]
    public void BuildV2CapturePlanFromGraph_UnsafeInitializer_AllCatalogTypes_DowngradeToPlaceholder(string typeName)
    {
        var graph = new LocalSymbolGraphEntry[]
        {
            new()
            {
                Name = "value",
                TypeName = typeName,
                Kind = "local",
                InitializerExpression = "ComputeValue(input)",   // method call = unsafe
                DeclarationOrder = 1,
                Dependencies = [],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.Orders.ToListAsync(value)",
            graph);

        var entry = Assert.Single(capturePlan.Entries);
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, entry.CapturePolicy);
        Assert.Null(entry.InitializerExpression);
        // No diagnostic - downgrade is silent
        Assert.Empty(capturePlan.Diagnostics);
    }

    [Fact]
    public void BuildV2CapturePlanFromGraph_UnsafeInitializer_UnknownTypeWithIntDependency_InferredAsIntPlaceholder()
    {
        var graph = new LocalSymbolGraphEntry[]
        {
            new()
            {
                Name = "lookbackDays",
                TypeName = "global::System.Int32",
                Kind = "parameter",
                InitializerExpression = null,
                DeclarationOrder = 0,
                Dependencies = [],
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
            new()
            {
                Name = "safeLookbackDays",
                TypeName = "?",
                Kind = "local",
                InitializerExpression = "Math.Clamp(lookbackDays, 1, 365)",
                DeclarationOrder = 1,
                Dependencies = ["lookbackDays"],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.Orders.Where(o => o.Id > safeLookbackDays).ToListAsync(ct)",
            graph);

        var safeLookbackDays = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "safeLookbackDays", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, safeLookbackDays.CapturePolicy);
        Assert.Equal("int", safeLookbackDays.TypeName);
        Assert.Null(safeLookbackDays.InitializerExpression);
        Assert.Empty(safeLookbackDays.Dependencies);
        Assert.Empty(capturePlan.Diagnostics);
    }

    [Fact]
    public void BuildV2CapturePlanFromGraph_UnknownTypeDateComputation_InferredAsDateTimePlaceholder()
    {
        var graph = new LocalSymbolGraphEntry[]
        {
            new()
            {
                Name = "utcNow",
                TypeName = "global::System.DateTime",
                Kind = "parameter",
                InitializerExpression = null,
                DeclarationOrder = 0,
                Dependencies = [],
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
            new()
            {
                Name = "lookbackDays",
                TypeName = "global::System.Int32",
                Kind = "parameter",
                InitializerExpression = null,
                DeclarationOrder = 1,
                Dependencies = [],
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
            new()
            {
                Name = "safeLookbackDays",
                TypeName = "?",
                Kind = "local",
                InitializerExpression = "Math.Clamp(lookbackDays, 1, 365)",
                DeclarationOrder = 2,
                Dependencies = ["lookbackDays"],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
            new()
            {
                Name = "fromUtc",
                TypeName = "?",
                Kind = "local",
                InitializerExpression = "utcNow.Date.AddDays(-safeLookbackDays)",
                DeclarationOrder = 3,
                Dependencies = ["safeLookbackDays", "utcNow"],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.Orders.Where(o => o.CreatedUtc >= fromUtc)",
            graph);

        var safeLookbackDays = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "safeLookbackDays", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, safeLookbackDays.CapturePolicy);
        Assert.Equal("int", safeLookbackDays.TypeName);

        var fromUtc = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "fromUtc", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, fromUtc.CapturePolicy);
        Assert.Equal("DateTime", fromUtc.TypeName);
        Assert.Null(fromUtc.InitializerExpression);
        Assert.Empty(fromUtc.Dependencies);
        Assert.Empty(capturePlan.Diagnostics);
    }

    [Fact]
    public void BuildV2CapturePlanFromGraph_UnknownCollectionReceiverContainsMember_InferredAsTypedListPlaceholder()
    {
        var graph = new LocalSymbolGraphEntry[]
        {
            new()
            {
                Name = "applicationId",
                TypeName = "global::System.Guid",
                Kind = "parameter",
                DeclarationOrder = 0,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
            new()
            {
                Name = "clearSections",
                TypeName = "?",
                Kind = "local",
                InitializerExpression = "GetClearSections()",
                DeclarationOrder = 1,
                Dependencies = [],
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
            new()
            {
                Name = "ct",
                TypeName = "global::System.Threading.CancellationToken",
                Kind = "parameter",
                DeclarationOrder = 2,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
        };

        var lambdaMemberTypes = new Dictionary<(string Receiver, string Member), string>
        {
            [("d", "Page")] = "global::MyApp.ApplicationPage",
            [("d", "ApplicationDetailsId")] = "global::System.Guid",
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.ApplicationDrafts.Where(d => d.ApplicationDetailsId == applicationId && clearSections.Contains(d.Page)).ToListAsync(ct)",
            graph,
            lambdaMemberTypes);

        var clearSections = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "clearSections", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, clearSections.CapturePolicy);
        Assert.Equal("global::System.Collections.Generic.List<global::MyApp.ApplicationPage>", clearSections.TypeName);
        Assert.Null(clearSections.InitializerExpression);
        Assert.Empty(clearSections.Dependencies);
        Assert.Empty(capturePlan.Diagnostics);
    }

    [Fact]
    public void BuildV2CapturePlanFromGraph_ValueMemberAccess_PromotesGuidPlaceholderToNullable()
    {
        var graph = new LocalSymbolGraphEntry[]
        {
            new()
            {
                Name = "applicationInputReplyDraftId",
                TypeName = "Guid",
                Kind = "parameter",
                DeclarationOrder = 0,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.ApplicationInputReplyDrafts.Where(w => w.ApplicationInputReplyDraftId == applicationInputReplyDraftId.Value)",
            graph);

        var id = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "applicationInputReplyDraftId", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, id.CapturePolicy);
        Assert.Equal("Guid?", id.TypeName);
        Assert.Empty(capturePlan.Diagnostics);
    }

    [Fact]
    public void BuildV2CapturePlanFromGraph_UnsafeInitializer_KnownCollectionType_DowngradesToPlaceholder()
    {
        var graph = new LocalSymbolGraphEntry[]
        {
            new()
            {
                Name = "checkTransactionDateStatus",
                TypeName = "global::System.Collections.Generic.List<global::System.DayOfWeek?>",
                Kind = "local",
                InitializerExpression = "new global::System.Collections.Generic.List<global::System.DayOfWeek?>()",
                DeclarationOrder = 0,
                ReplayPolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            },
        };

        var capturePlan = LspSyntaxHelper.BuildV2CapturePlanFromGraph(
            "dbContext.Orders.Where(o => checkTransactionDateStatus.Contains(o.CreatedUtc.DayOfWeek))",
            graph);

        var entry = Assert.Single(capturePlan.Entries, e => string.Equals(e.Name, "checkTransactionDateStatus", StringComparison.Ordinal));
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, entry.CapturePolicy);
        Assert.Equal("global::System.Collections.Generic.List<global::System.DayOfWeek?>", entry.TypeName);
        Assert.Null(entry.InitializerExpression);
        Assert.Empty(entry.Dependencies);
        Assert.Empty(capturePlan.Diagnostics);
    }

    private static V2CapturePlanSnapshot BuildCapturePlanAtMarker(string source, string marker)
    {
        var (line, character) = FindPosition(source, marker);

        var extractionSuccess = LspSyntaxHelper.TryBuildV2ExtractionPlan(
            source,
            @"c:\repo\Demo.cs",
            line,
            character,
            out var extractionPlan,
            out var extractionDiagnostics);

        Assert.True(extractionSuccess);
        Assert.NotNull(extractionPlan);
        Assert.Empty(extractionDiagnostics);

        var helperSubstitutionApplied = string.Equals(extractionPlan!.Origin.Scope, "helper-method", StringComparison.Ordinal);

        var captureSuccess = LspSyntaxHelper.TryBuildV2CapturePlan(
            extractionPlan.Expression,
            extractionPlan.ContextVariableName,
            source,
            extractionPlan.Origin.Line,
            extractionPlan.Origin.Character,
            targetAssemblyPath: null,
            out var capturePlan,
            secondarySourceText: helperSubstitutionApplied ? source : null,
            secondaryLine: helperSubstitutionApplied ? line : null,
            secondaryCharacter: helperSubstitutionApplied ? character : null,
            dbContextTypeName: null);

        Assert.NotNull(capturePlan);
        if (!captureSuccess)
        {
            // Explicitly returning the plan on failure allows reject-path tests to assert diagnostics.
            return capturePlan;
        }

        return capturePlan;
    }
}
