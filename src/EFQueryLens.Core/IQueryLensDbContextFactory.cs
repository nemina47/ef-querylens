namespace EFQueryLens.Core;

/// <summary>
/// Implement this interface in your project to give QueryLens full control over
/// how the offline <see cref="Microsoft.EntityFrameworkCore.DbContext"/> is
/// constructed for SQL preview.
///
/// <para>
/// <b>When to use:</b> Add this when your project configures provider-specific
/// options that the automatic bootstrap path cannot infer — for example:
/// <list type="bullet">
///   <item><description><c>UseProjectables()</c> — required for <c>[Projectable]</c> properties</description></item>
///   <item><description><c>MySqlSchemaBehavior.Translate</c> — required for correct table names</description></item>
///   <item><description><c>UseQuerySplittingBehavior</c> — affects JOIN vs. split query output</description></item>
///   <item><description>Custom conventions / interceptors that shape the EF Core model</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>How to add to your project:</b>
/// <code>
/// // YourProject.csproj
/// &lt;ProjectReference Include="..\..\path\to\EFQueryLens.Core.csproj" PrivateAssets="all" /&gt;
/// // or NuGet:
/// &lt;PackageReference Include="EFQueryLens.Core" Version="*" PrivateAssets="all" /&gt;
/// </code>
/// The <c>PrivateAssets="all"</c> keeps QueryLens out of your runtime deployment.
/// </para>
///
/// <para>
/// <b>Discovery:</b> QueryLens finds your factory via full-name reflection —
/// the same technique used for <c>IDesignTimeDbContextFactory&lt;T&gt;</c>.
/// It is prioritised above both <c>IDesignTimeDbContextFactory&lt;T&gt;</c>
/// and the automatic bootstrap fallback.
/// </para>
/// </summary>
/// <typeparam name="TContext">The DbContext type this factory creates.</typeparam>
public interface IQueryLensDbContextFactory<out TContext>
    where TContext : class
{
    /// <summary>
    /// Creates an offline instance of <typeparamref name="TContext"/> configured
    /// for SQL generation. No real database connection is ever opened or required.
    /// </summary>
    TContext CreateOfflineContext();
}
