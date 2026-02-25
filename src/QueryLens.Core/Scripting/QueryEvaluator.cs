using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using QueryLens.Core.AssemblyContext;

namespace QueryLens.Core.Scripting;

/// <summary>
/// Evaluates a LINQ expression string against an offline <c>DbContext</c>
/// instance loaded via <see cref="ProjectAssemblyContext"/> and returns the
/// generated SQL as a <see cref="QueryTranslationResult"/>.
///
/// No real database connection is ever opened.
/// </summary>
public sealed class QueryEvaluator
{
    private readonly ScriptStateCache _cache = new();

    // EF Core ToQueryString extension method — cached after first lookup.
    private MethodInfo? _toQueryStringMethod;

    // ─── Static lazy-load resolver ────────────────────────────────────────────

    // Directories registered by GetOrLoadTypeInDefaultAlc for each user project.
    // When the default ALC can't resolve an assembly through its normal probe paths
    // (e.g. a lazy transitive dep like Audit.NET that wasn't loaded at mirror-
    // snapshot time), the Resolving handler below probes these directories.
    private static readonly HashSet<string> s_probeDirs =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_probeDirsLock = new();

    /// <summary>
    /// Registers a one-time <see cref="AssemblyLoadContext.Default"/> Resolving
    /// handler that probes the registered user bin directories.  This handles
    /// lazy transitive dependencies (e.g. Audit.NET loaded by AuditDbContext)
    /// that are not yet in <c>userAlc.Assemblies</c> when the mirroring snapshot
    /// runs in <see cref="GetOrLoadTypeInDefaultAlc"/>.
    /// </summary>
    static QueryEvaluator()
    {
        AssemblyLoadContext.Default.Resolving += (alc, name) =>
        {
            if (name.Name is null) return null;

            string[] dirs;
            lock (s_probeDirsLock)
                dirs = [..s_probeDirs];

            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, $"{name.Name}.dll");
                if (File.Exists(candidate))
                {
                    try { return alc.LoadFromAssemblyPath(candidate); }
                    catch { /* wrong version or platform — keep trying other dirs */ }
                }
            }

            return null; // let the default ALC continue its normal fallback chain
        };
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Translates a LINQ expression to SQL via <c>ToQueryString()</c>.
    /// </summary>
    public async Task<QueryTranslationResult> EvaluateAsync(
        ProjectAssemblyContext alcCtx,
        IProviderBootstrap bootstrap,
        TranslationRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. Resolve the DbContext type from the user's ALC.
            var dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName);

            // 2. Load the same type in the DEFAULT ALC — Roslyn CSharpScript generates
            //    assemblies that execute in the default ALC.  The DbContext instance must
            //    also originate from the default ALC so that the typed preamble cast
            //    (e.g. "(AppDbContext)(object)db") resolves to exactly the same runtime
            //    type as the MetadataReference used during compilation.
            var defaultAlcContextType = GetOrLoadTypeInDefaultAlc(dbContextType);

            // 3. Build an offline DbContext instance using the default-ALC type.
            //    Try IDesignTimeDbContextFactory<T> first (mirrors EF Core tooling priority).
            var (dbInstance, creationStrategy) = CreateDbContextInstance(defaultAlcContextType, bootstrap, alcCtx.LoadedAssemblies);

            // 4. Get or build a warm ScriptState for this assembly + context type.
            var scriptState = await GetOrBuildScriptStateAsync(
                alcCtx, defaultAlcContextType, dbInstance, request.ContextVariableName, ct);

            // 5. Evaluate the user's LINQ expression.
            //    No EnterContextualReflection needed — both the DbContext instance
            //    and the MetadataReferences resolve from the default ALC.
            var resultState = await scriptState.ContinueWithAsync<object>(
                request.Expression, cancellationToken: ct);

            var result = resultState.ReturnValue;

            // 6. Verify the result is an IQueryable and extract SQL.
            if (!IsQueryable(result))
            {
                return Failure(
                    $"Expression did not return an IQueryable. Got: '{result?.GetType().Name ?? "null"}'. " +
                    "Make sure your expression targets a DbSet property (e.g. db.Orders.Where(...)).",
                    sw.Elapsed, dbContextType, bootstrap);
            }

            var sql = CallToQueryString(result, dbContextType);

            sw.Stop();

            return new QueryTranslationResult
            {
                Success = true,
                Sql = sql,
                Parameters = ParseParameters(sql),
                Metadata = BuildMetadata(dbContextType, bootstrap, sw.Elapsed, creationStrategy),
            };
        }
        catch (CompilationErrorException cex)
        {
            return Failure(
                $"Compilation error: {string.Join("; ", cex.Diagnostics.Select(d => d.GetMessage()))}",
                sw.Elapsed, null, bootstrap);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reflection-based invocations (ctor.Invoke, method.Invoke) wrap the
            // real exception in TargetInvocationException. Unwrap one level so the
            // error message returned to the caller describes the actual failure.
            var msg = ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                ? inner.ToString()
                : ex.Message;
            return Failure(msg, sw.Elapsed, null, bootstrap);
        }
    }

    // ─── ScriptState building ─────────────────────────────────────────────────

    private async Task<ScriptState<object>> GetOrBuildScriptStateAsync(
        ProjectAssemblyContext alcCtx,
        Type dbContextType,
        object dbInstance,
        string contextVariableName,
        CancellationToken ct)
    {
        var cached = _cache.TryGet(
            alcCtx.AssemblyPath,
            dbContextType.FullName!,
            alcCtx.AssemblyTimestamp);

        if (cached is not null)
            return cached;

        var freshState = await BuildInitialScriptStateAsync(
            alcCtx, dbContextType, dbInstance, contextVariableName, ct);

        _cache.Store(
            alcCtx.AssemblyPath,
            dbContextType.FullName!,
            alcCtx.AssemblyTimestamp,
            freshState);

        return freshState;
    }

    private static async Task<ScriptState<object>> BuildInitialScriptStateAsync(
        ProjectAssemblyContext alcCtx,
        Type dbContextType,
        object dbInstance,
        string contextVariableName,
        CancellationToken ct)
    {
        var refs = CollectMetadataReferences(alcCtx);

        var scriptOptions = ScriptOptions.Default
            .AddReferences(refs)
            .AddImports(
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "Microsoft.EntityFrameworkCore");

        // Create a strongly-typed local alias for the raw global so that user expressions
        // like "db.Orders.Where(o => o.UserId == 5)" compile cleanly against the concrete
        // DbContext type rather than the dynamic global.
        //
        // The global is named __ql_raw_ctx__ (deliberately unusual) so there is NO naming
        // conflict with the user's requested variable (e.g. "db").  A single preamble
        // submission is therefore sufficient — no CS0815 (the local and the global carry
        // different names, so there is no self-referential type inference), and subsequent
        // ContinueWithAsync calls see only the typed local, not a shadowed dynamic global.
        //
        // Both the DbContext instance and the type resolved from MetadataReferences come
        // from the default ALC (see GetOrLoadTypeInDefaultAlc), so the cast always succeeds.
        var globals = new QueryScriptGlobals { __ql_raw_ctx__ = dbInstance };

        var script = CSharpScript.Create<object>(
            $"var {contextVariableName} = ({dbContextType.FullName})(object)__ql_raw_ctx__;",
            scriptOptions,
            globalsType: typeof(QueryScriptGlobals));

        var initial = await script.RunAsync(globals, cancellationToken: ct);

        return initial;
    }

    // ─── Default-ALC type resolution ─────────────────────────────────────────

    /// <summary>
    /// Returns the type with the same full name from <see cref="AssemblyLoadContext.Default"/>,
    /// loading the assembly from its file path if it is not already present there.
    /// </summary>
    /// <remarks>
    /// Roslyn <see cref="CSharpScript"/> executes in the default ALC.  Loading the user's
    /// assembly into the default ALC ensures that runtime type resolution in the preamble
    /// cast (e.g. <c>(AppDbContext)(object)db</c>) matches the <see cref="MetadataReference"/>
    /// used during compilation — preventing <see cref="InvalidCastException"/> at runtime.
    /// </remarks>
    private static Type GetOrLoadTypeInDefaultAlc(Type userAlcType)
    {
        var path = userAlcType.Assembly.Location;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException(
                $"Assembly '{userAlcType.Assembly.GetName().Name}' has no physical file location " +
                "and cannot be loaded into the default ALC.");

        // Mirror the user-ALC assemblies into the default ALC before calling GetType().
        //
        // Roslyn CSharpScript executes in the default ALC, so both the DbContext
        // type and its dependency chain (base classes, interfaces, entity types) must
        // be resolvable there.  For projects where the DbContext extends a third-party
        // base class (e.g. AuditDbContext from Audit.EntityFramework), that base class
        // assembly is loaded into the *user* ALC — not the default ALC.  Without this
        // mirroring step, Assembly.GetType() returns null because it can't resolve the
        // base-class assembly, even though the DbContext type itself is present.
        //
        // We build a snapshot of current default-ALC locations first (fast HashSet
        // lookup), then add any user-ALC assemblies that aren't already there.
        // EF Core / Pomelo are already shared with the default ALC by
        // IsolatedLoadContext.Load — they'll be in the snapshot and skipped.
        var userAlc = AssemblyLoadContext.GetLoadContext(userAlcType.Assembly);
        if (userAlc is not null && userAlc != AssemblyLoadContext.Default)
        {
            // Register the user's bin dir so the static Resolving handler above can
            // lazily load transitive deps (e.g. Audit.NET) that are not yet in the
            // default ALC snapshot — they only get loaded when EF Core's type-walker
            // encounters base classes like AuditDbContext during DbContext construction.
            lock (s_probeDirsLock)
            {
                var binDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(binDir))
                    s_probeDirs.Add(binDir);
            }

            var defaultLocations = AssemblyLoadContext.Default.Assemblies
                .Select(a => a.Location)
                .Where(l => !string.IsNullOrEmpty(l))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in userAlc.Assemblies)
            {
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc) || defaultLocations.Contains(loc)) continue;
                try
                {
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(loc);
                    defaultLocations.Add(loc);
                }
                catch { /* best-effort — some assemblies legitimately can't load in the default ALC */ }
            }
        }

        // Now load (or find already-loaded) the primary assembly in the default ALC.
        var defaultAssembly =
            AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => string.Equals(a.Location, path, StringComparison.OrdinalIgnoreCase))
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

        return defaultAssembly.GetType(userAlcType.FullName!)
               ?? throw new InvalidOperationException(
                   $"Type '{userAlcType.FullName}' was not found after loading '{path}' into the default ALC.");
    }

    // ─── MetadataReference collection ────────────────────────────────────────

    /// <summary>
    /// Collects <see cref="MetadataReference"/> objects from the user's ALC
    /// assemblies plus the default context (for framework types). Only
    /// assemblies with a physical file location are included.
    /// </summary>
    private static IEnumerable<MetadataReference> CollectMetadataReferences(
        ProjectAssemblyContext alcCtx)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();

        // User ALC assemblies (entities, EF Core, provider, etc.)
        var userAssemblies = alcCtx.LoadedAssemblies;

        // Default context (System.*, Microsoft.Extensions.*, etc.)
        var defaultAssemblies = AssemblyLoadContext.Default.Assemblies;

        foreach (var asm in userAssemblies.Concat(defaultAssemblies))
        {
            try
            {
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc) || !seen.Add(loc)) continue;
                refs.Add(MetadataReference.CreateFromFile(loc));
            }
            catch
            {
                // Dynamic / in-memory assemblies have no Location — skip them.
            }
        }

        return refs;
    }

    // ─── DbContext instantiation ──────────────────────────────────────────────

    private static (object Instance, string Strategy) CreateDbContextInstance(
        Type dbContextType,
        IProviderBootstrap bootstrap,
        IEnumerable<Assembly> userAlcAssemblies)
    {
        var allAssemblies = AssemblyLoadContext.Default.Assemblies.Concat(userAlcAssemblies);

        // Priority 0: IQueryLensDbContextFactory<T> — QueryLens-native, highest priority.
        // Users implement this to supply provider-specific options (UseProjectables,
        // MySqlSchemaBehavior, etc.) that the bootstrap path cannot infer.
        var fromQueryLensFactory = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            dbContextType, allAssemblies);
        if (fromQueryLensFactory is not null)
            return (fromQueryLensFactory, "querylens-factory");

        // Priority 1: IDesignTimeDbContextFactory<T> — mirrors EF Core tooling.
        // Search default ALC (which already contains the user's assembly after
        // GetOrLoadTypeInDefaultAlc), then user ALC assemblies as fallback.
        var fromFactory = DesignTimeDbContextFactory.TryCreate(
            dbContextType, allAssemblies);
        if (fromFactory is not null)
            return (fromFactory, "design-time-factory");

        // Priority 2: Bootstrap approach (provider-supplied fake connection string).
        // We must build DbContextOptions<AppDbContext> using the EF Core types
        // that live in the *user's* ALC — not the tool's. Otherwise the ctor
        // rejects the options due to a cross-ALC type constraint violation.
        //
        // Steps:
        //   1. Find Microsoft.EntityFrameworkCore in the user's ALC.
        //   2. Create DbContextOptionsBuilder<TContext> from the user-ALC type.
        //   3. Pass the builder to bootstrap.ConfigureOffline() — it calls
        //      builder.UseMySql(...) (or equivalent).
        //   4. Read builder.Options (a DbContextOptions<TContext> in the user ALC).
        //   5. Invoke the DbContext ctor with those options.

        // Phase 1: EF Core is shared with the default ALC (excluded from user ALC),
        // so we search the default ALC's assemblies as well.
        var efAssembly = userAlcAssemblies
                             .Concat(AssemblyLoadContext.Default.Assemblies)
                             .FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore")
                         ?? throw new InvalidOperationException(
                             "Microsoft.EntityFrameworkCore not found. " +
                             "Ensure the project references EF Core and has been built.");

        // DbContextOptionsBuilder<TContext>
        var builderOpenGeneric = efAssembly.GetType(
                                     "Microsoft.EntityFrameworkCore.DbContextOptionsBuilder`1")
                                 ?? throw new InvalidOperationException(
                                     "Could not locate DbContextOptionsBuilder`1 in user EF Core assembly.");

        var builderType = builderOpenGeneric.MakeGenericType(dbContextType);
        var builderInstance = Activator.CreateInstance(builderType)!
                                  as Microsoft.EntityFrameworkCore.DbContextOptionsBuilder
                              ?? throw new InvalidOperationException(
                                  "Could not cast DbContextOptionsBuilder<T> to DbContextOptionsBuilder.");

        // Let the bootstrap configure UseMySql / UseNpgsql / etc.
        bootstrap.ConfigureOffline(builderInstance);

        // Read .Options — this is DbContextOptions<TContext> in the user's ALC.
        // Use DeclaredOnly to avoid AmbiguousMatchException: DbContextOptionsBuilder<T>
        // hides DbContextOptionsBuilder.Options with a covariant return, so both
        // the base and derived type expose a property named "Options".
        var optionsProp = builderType.GetProperty("Options",
                              BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                          ?? throw new InvalidOperationException(
                              "Could not find Options property on DbContextOptionsBuilder<T>.");

        var options = optionsProp.GetValue(builderInstance)!;

        // Find the matching ctor on the DbContext type.
        const string genericOptionsFullName = "Microsoft.EntityFrameworkCore.DbContextOptions`1";
        const string baseOptionsFullName = "Microsoft.EntityFrameworkCore.DbContextOptions";

        ConstructorInfo? ctor = null;
        foreach (var candidate in dbContextType.GetConstructors())
        {
            var parms = candidate.GetParameters();
            if (parms.Length != 1) continue;

            var paramTypeName = parms[0].ParameterType.FullName ?? string.Empty;
            if (paramTypeName.StartsWith(genericOptionsFullName, StringComparison.Ordinal) ||
                paramTypeName.StartsWith(baseOptionsFullName, StringComparison.Ordinal))
            {
                ctor = candidate;
                break;
            }
        }

        if (ctor is null)
            throw new InvalidOperationException(
                $"'{dbContextType.FullName}' has no (DbContextOptions) constructor. " +
                "QueryLens requires a DbContextOptions or DbContextOptions<T> ctor.");

        return (ctor.Invoke([options]), "bootstrap");
    }

    // ─── IQueryable detection & ToQueryString via reflection ─────────────────

    private static bool IsQueryable(object? value) =>
        value?.GetType().GetInterfaces()
            .Any(i => i.FullName == "System.Linq.IQueryable") == true;

    /// <summary>
    /// Calls <c>EntityFrameworkQueryableExtensions.ToQueryString(IQueryable)</c>
    /// via reflection. The extension lives in the user's EF Core assembly (in
    /// the user's ALC), so we find it by name rather than using typeof().
    /// </summary>
    private string CallToQueryString(object queryable, Type dbContextType)
    {
        _toQueryStringMethod ??= FindToQueryStringMethod(dbContextType.Assembly);

        if (_toQueryStringMethod is null)
            throw new InvalidOperationException(
                "Could not locate EntityFrameworkQueryableExtensions.ToQueryString. " +
                "Verify that Microsoft.EntityFrameworkCore is in the user assembly's output directory.");

        return (string)_toQueryStringMethod.Invoke(null, [queryable])!;
    }

    private static MethodInfo? FindToQueryStringMethod(Assembly userAssembly)
    {
        // Walk the ALC that owns the user's DbContext assembly to find EF Core,
        // then also check the default ALC (Phase 1: EF Core is shared there).
        var alc = AssemblyLoadContext.GetLoadContext(userAssembly);
        var alcAssemblies = alc?.Assemblies ?? Enumerable.Empty<Assembly>();
        var assemblies = alcAssemblies.Concat(AssemblyLoadContext.Default.Assemblies);

        foreach (var asm in assemblies.Where(a => a.GetName().Name == "Microsoft.EntityFrameworkCore"))
        {
            var extType = asm.GetType(
                "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions");

            var method = extType?
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "ToQueryString" &&
                    m.GetParameters().Length == 1 &&
                    !m.IsGenericMethod);

            if (method is not null) return method;
        }

        return null;
    }

    // ─── Parameter parsing ────────────────────────────────────────────────────

    /// <summary>
    /// Parses parameter annotations from <c>ToQueryString()</c> output.
    /// EF Core emits comment lines like: <c>-- @p0='5' (DbType = Int32)</c>
    /// before the actual SQL.
    /// </summary>
    private static IReadOnlyList<QueryParameter> ParseParameters(string sql)
    {
        var parameters = new List<QueryParameter>();

        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("-- @", StringComparison.Ordinal)) continue;

            var content = trimmed[3..].Trim(); // strip "-- "
            var nameEnd = content.IndexOfAny(['=', ' ']);
            if (nameEnd < 0) continue;

            parameters.Add(new QueryParameter
            {
                Name = content[..nameEnd].Trim(),
                ClrType = ExtractDbType(content),
                InferredValue = ExtractInferredValue(content),
            });
        }

        return parameters;
    }

    private static string ExtractDbType(string annotation)
    {
        const string marker = "DbType = ";
        var idx = annotation.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return "object";
        var start = idx + marker.Length;
        var end = annotation.IndexOf(')', start);
        return end > start ? annotation[start..end].Trim() : "object";
    }

    private static string? ExtractInferredValue(string annotation)
    {
        var eqIdx = annotation.IndexOf("='", StringComparison.Ordinal);
        if (eqIdx < 0) return null;
        var start = eqIdx + 2;
        var end = annotation.IndexOf('\'', start);
        return end > start ? annotation[start..end] : null;
    }

    // ─── Result builders ──────────────────────────────────────────────────────

    private static QueryTranslationResult Failure(
        string message, TimeSpan elapsed, Type? dbContextType, IProviderBootstrap? bootstrap) =>
        new()
        {
            Success = false,
            ErrorMessage = message,
            Metadata = BuildMetadata(dbContextType, bootstrap, elapsed),
        };

    private static TranslationMetadata BuildMetadata(
        Type? dbContextType, IProviderBootstrap? bootstrap, TimeSpan elapsed,
        string creationStrategy = "bootstrap") =>
        new()
        {
            DbContextType    = dbContextType?.FullName ?? "unknown",
            ProviderName     = bootstrap?.ProviderName ?? "unknown",
            EfCoreVersion    = GetEfCoreVersion(dbContextType),
            TranslationTime  = elapsed,
            CreationStrategy = creationStrategy,
        };

    private static string GetEfCoreVersion(Type? dbContextType)
    {
        if (dbContextType is null) return "unknown";
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(dbContextType.Assembly);
            var alcAssemblies = alc?.Assemblies ?? Enumerable.Empty<Assembly>();
            // Phase 1: EF Core is shared with default ALC — search both.
            var ef = alcAssemblies
                .Concat(AssemblyLoadContext.Default.Assemblies)
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore");
            return ef?.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
