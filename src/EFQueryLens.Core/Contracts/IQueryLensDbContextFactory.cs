namespace EFQueryLens.Core.Contracts;

/// <summary>
/// Implement this interface in your project to give QueryLens full control over
/// how the offline <see cref="Microsoft.EntityFrameworkCore.DbContext"/> is
/// constructed for SQL preview.
///
/// <para>
/// <b>When to use:</b> Add this when your project configures provider-specific
/// options that SQL preview depends on — for example:
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
/// <b>Placement rule:</b> implement this factory in the executable startup project
/// that QueryLens targets (API / WorkerService / Console).
/// Do not place it in a class library.
/// QueryLens resolves dependencies from the selected executable assembly output,
/// and only factories declared in that assembly are used.
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
