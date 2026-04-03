using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public sealed class DbContextResolutionSnapshotTests
{
    [Fact]
    public void BuildDbContextResolutionSnapshot_DeclaredAndUniqueFactoryMatch_IsHighConfidence()
    {
        var source = """
            private readonly SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext db;
            _ = db.Orders;
            """;

        var snapshot = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
            source,
            "db",
            ["SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext"]);

        Assert.NotNull(snapshot);
        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext", snapshot!.DeclaredTypeName);
        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext", snapshot.FactoryTypeName);
        Assert.Equal("declared+factory", snapshot.ResolutionSource);
        Assert.Equal(1.0, snapshot.Confidence);
        Assert.Equal(
            "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
            LspSyntaxHelper.GetPreferredDbContextTypeName(snapshot));
    }

    [Fact]
    public void BuildDbContextResolutionSnapshot_MultipleFactoryCandidates_PreservesCandidatesWithoutPreferredType()
    {
        var source = """
            var query = db.CustomerDirectory;
            """;

        var snapshot = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
            source,
            "db",
            [
                "SampleMySqlApp.Infrastructure.Persistence.MySqlReportingDbContext",
                "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
            ]);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.DeclaredTypeName);
        Assert.Null(snapshot.FactoryTypeName);
        Assert.Equal("factory-candidates", snapshot.ResolutionSource);
        Assert.Equal(0.5, snapshot.Confidence);
        Assert.Equal(2, snapshot.FactoryCandidateTypeNames.Count);
        Assert.Null(LspSyntaxHelper.GetPreferredDbContextTypeName(snapshot));
        Assert.Equal(
            "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext;SampleMySqlApp.Infrastructure.Persistence.MySqlReportingDbContext",
            LspSyntaxHelper.GetDbContextResolutionCacheToken(snapshot));
    }

    [Fact]
    public void BuildDbContextResolutionSnapshot_DeclaredAndFactoryMismatch_IsLowConfidence()
    {
        var source = """
            private readonly SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext db;
            _ = db.Orders;
            """;

        var snapshot = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
            source,
            "db",
            ["SampleSqlServerApp.Infrastructure.Persistence.SqlServerReportingDbContext"]);

        Assert.NotNull(snapshot);
        Assert.Equal("declared+factory-mismatch", snapshot!.ResolutionSource);
        Assert.Equal(0.4, snapshot.Confidence);
        Assert.Equal(
            "SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext",
            LspSyntaxHelper.GetPreferredDbContextTypeName(snapshot));
    }

    [Fact]
    public void BuildDbContextResolutionSnapshot_DeclaredInterfaceAndUniqueFactoryMismatch_PrefersFactoryConcreteType()
    {
        var source = """
            private readonly SampleMySqlApp.Application.Abstractions.IMySqlAppDbContext db;
            _ = db.Customers;
            """;

        var snapshot = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
            source,
            "db",
            ["SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext"]);

        Assert.NotNull(snapshot);
        Assert.Equal("declared+factory-mismatch", snapshot!.ResolutionSource);
        Assert.Equal(
            "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
            LspSyntaxHelper.GetPreferredDbContextTypeName(snapshot));
        Assert.Equal(
            "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
            LspSyntaxHelper.GetDbContextResolutionCacheToken(snapshot));
    }

    [Fact]
    public void BuildDbContextResolutionSnapshot_DeclaredInterfaceAndMultipleFactoryCandidates_DoesNotPreferInterfaceName()
    {
        var source = """
            private readonly SampleMySqlApp.Application.Abstractions.IMySqlAppDbContext db;
            _ = db.Customers;
            """;

        var snapshot = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
            source,
            "db",
            [
                "SampleMySqlApp.Infrastructure.Persistence.MySqlReportingDbContext",
                "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
            ]);

        Assert.NotNull(snapshot);
        Assert.Equal("declared+factory-candidates", snapshot!.ResolutionSource);
        Assert.Null(LspSyntaxHelper.GetPreferredDbContextTypeName(snapshot));
        Assert.Equal(
            "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext;SampleMySqlApp.Infrastructure.Persistence.MySqlReportingDbContext",
            LspSyntaxHelper.GetDbContextResolutionCacheToken(snapshot));
    }

    [Fact]
    public void BuildDbContextResolutionSnapshot_UnwrapsDbContextFactoryDeclaredType()
    {
        var source = """
            private readonly IDbContextFactory<SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext> _contextFactory;
            var query = _contextFactory.CreateDbContext().Orders;
            """;

        var snapshot = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
            source,
            "_contextFactory",
            [
                "SampleMySqlApp.Infrastructure.Persistence.MySqlReportingDbContext",
                "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
            ]);

        Assert.NotNull(snapshot);
        Assert.Equal(
            "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
            snapshot!.DeclaredTypeName);
        Assert.Equal("declared+factory-candidates", snapshot.ResolutionSource);
        Assert.Equal(
            "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
            LspSyntaxHelper.GetPreferredDbContextTypeName(snapshot));
    }
}
