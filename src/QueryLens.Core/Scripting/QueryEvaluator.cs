using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using QueryLens.Core.AssemblyContext;

namespace QueryLens.Core.Scripting;

/// <summary>
/// Evaluates a LINQ expression string against an offline <c>DbContext</c>
/// instance loaded via <see cref="ProjectAssemblyContext"/> and returns the
/// generated SQL commands as a <see cref="QueryTranslationResult"/>.
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
                dirs = [.. s_probeDirs];

            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, $"{name.Name}.dll");
                if (File.Exists(candidate))
                {
                    try
                    {
                        return alc.LoadFromAssemblyPath(candidate);
                    }
                    catch
                    {
                        /* wrong version or platform — keep trying other dirs */
                    }
                }
            }

            return null; // let the default ALC continue its normal fallback chain
        };
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Translates a LINQ expression to SQL, preferring execution-based capture
    /// and falling back to <c>ToQueryString()</c> when capture is unavailable.
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
            Type dbContextType;
            try
            {
                dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName, request.Expression);
            }
            catch (InvalidOperationException ex) when (IsNoDbContextFoundError(ex))
            {
                // Some solutions place the DbContext in a sibling class library while the
                // hovered file resolves to a host executable assembly. If that sibling wasn't
                // loaded yet in a reused cached context, best-effort load local sibling DLLs
                // and retry discovery once before returning the original error.
                TryLoadSiblingAssembliesForDbContextDiscovery(alcCtx);
                dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName, request.Expression);
            }

            // 2. Load the same type in the DEFAULT ALC — Roslyn CSharpScript generates
            //    assemblies that execute in the default ALC.  The DbContext instance must
            //    also originate from the default ALC so that the typed preamble cast
            //    (e.g. "(AppDbContext)(object)db") resolves to exactly the same runtime
            //    type as the MetadataReference used during compilation.
            var defaultAlcContextType = GetOrLoadTypeInDefaultAlc(dbContextType);

            if (IsUnsupportedTopLevelMethodInvocation(request.Expression, request.ContextVariableName))
            {
                return Failure(
                    "Top-level method invocations (e.g. service.GetXxx(...)) are not supported for SQL preview. " +
                    "Hover a direct IQueryable chain (for example: dbContext.Entities.Where(...)) or hover inside the method where the query is built.",
                    sw.Elapsed,
                    dbContextType,
                    bootstrap);
            }

            // 3. Build an offline DbContext instance using the default-ALC type.
            //    Try IDesignTimeDbContextFactory<T> first (mirrors EF Core tooling priority).
            var (dbInstance, creationStrategy) =
                CreateDbContextInstance(defaultAlcContextType, bootstrap, alcCtx.LoadedAssemblies);

            var canCaptureWithExecution = EnsureExecutionCaptureSafety(
                dbInstance,
                creationStrategy,
                out var captureSkipReason);

            // 4. Get or build a warm ScriptState for this assembly + context type.
            var scriptState = await GetOrBuildScriptStateAsync(
                alcCtx, defaultAlcContextType, dbInstance, request, ct);

            // 5. Evaluate the user's LINQ expression.
            //    No EnterContextualReflection needed — both the DbContext instance
            //    and the MetadataReferences resolve from the default ALC.
            //
            //    When the expression references local variables from the user's method
            //    scope (e.g. applicationId, userId), Roslyn reports CS0103. We detect
            //    these, auto-declare default-valued stubs, and retry so the SQL shape
            //    is still generated correctly (values become @p0, @p1, etc.).
            ScriptState<object> resultState;
            var currentState = scriptState;
            var maxRetries = 5; // safety valve

            while (true)
            {
                try
                {
                    resultState = await currentState.ContinueWithAsync<object>(
                        request.Expression, cancellationToken: ct);
                    break;
                }
                catch (CompilationErrorException cex) when (maxRetries > 0)
                {
                    var missingNames = cex.Diagnostics
                        .Where(d => d.Id == "CS0103")
                        .Select(d =>
                        {
                            // Diagnostic message: "The name 'xyz' does not exist in the current context"
                            var msg = d.GetMessage();
                            var start = msg.IndexOf('\'');
                            var end = msg.IndexOf('\'', start + 1);
                            return start >= 0 && end > start
                                ? msg[(start + 1)..end]
                                : null;
                        })
                        .Where(n => n is not null)
                        .Distinct()
                        .ToList();

                    var declarableNames = missingNames
                        .Where(n =>
                            !string.IsNullOrWhiteSpace(n)
                            && !LooksLikeTypeOrNamespacePrefix(n!, request.Expression, request.UsingAliases))
                        .ToList();

                    if (declarableNames.Count == 0)
                    {
                        // Non-CS0103 compilation errors — fall through to the outer catch
                        return Failure(
                            $"Compilation error: {string.Join("; ", cex.Diagnostics.Select(d => d.GetMessage()))}",
                            sw.Elapsed, null, bootstrap);
                    }

                    // Declare the missing variables with type-inferred default values.
                    // We search the expression for comparison patterns like
                    //   w.PropertyName == variableName
                    // and look up PropertyName's CLR type from the DbContext's entity types.
                    var rootIdentifier = TryExtractRootIdentifier(request.Expression);
                    var declarations = string.Join("\n",
                        declarableNames.Select(n =>
                        {
                            if (!string.IsNullOrWhiteSpace(rootIdentifier) &&
                                string.Equals(n, rootIdentifier, StringComparison.Ordinal) &&
                                !string.Equals(n, request.ContextVariableName, StringComparison.Ordinal))
                            {
                                // If the parser picked a different context variable name,
                                // alias the expression root back to the known typed variable.
                                return $"var {n} = {request.ContextVariableName};";
                            }

                            var inferredType = InferVariableType(n!, request.Expression, dbContextType);
                            if (inferredType is not null)
                            {
                                var typeName = ToCSharpTypeName(inferredType);
                                return $"{typeName} {n} = default({typeName});";
                            }

                            var containsElementType = InferContainsElementType(n!, request.Expression, dbContextType);
                            if (containsElementType is not null)
                            {
                                var elementTypeName = ToCSharpTypeName(containsElementType);
                                // Keep placeholder collections non-empty so generated SQL shape
                                // doesn't collapse into "WHERE FALSE" for unknown Contains inputs.
                                return $"System.Collections.Generic.List<{elementTypeName}> {n} = new() {{ default({elementTypeName})! }};";
                            }

                            var selectEntityType = InferSelectEntityType(n!, request.Expression, dbContextType);
                            if (selectEntityType is not null)
                            {
                                var entityTypeName = ToCSharpTypeName(selectEntityType);
                                return $"System.Linq.Expressions.Expression<System.Func<{entityTypeName}, object>> {n} = _ => default!;";
                            }

                            if (LooksLikeCancellationTokenArgument(n!, request.Expression))
                            {
                                return $"System.Threading.CancellationToken {n} = default;";
                            }

                            return $"object {n} = default;";
                        }));
                    currentState = await currentState.ContinueWithAsync<object>(
                        declarations, cancellationToken: ct);
                    maxRetries--;
                }
            }

            var result = resultState.ReturnValue;

            // 6. Verify the result is an IQueryable and extract SQL.
            if (!IsQueryable(result))
            {
                return Failure(
                    $"Expression did not return an IQueryable. Got: '{result?.GetType().Name ?? "null"}'. " +
                    "Make sure your expression targets a DbSet property (e.g. db.Orders.Where(...)).",
                    sw.Elapsed, dbContextType, bootstrap);
            }

            var warnings = new List<QueryWarning>();
            IReadOnlyList<QuerySqlCommand> commands;
            string sql;
            IReadOnlyList<QueryParameter> parameters;

            if (canCaptureWithExecution)
            {
                var capturedCommands = TryCaptureSqlCommands(result, out var captureError);

                if (capturedCommands.Count > 0)
                {
                    commands = capturedCommands
                        .Select(c => new QuerySqlCommand { Sql = c.Sql, Parameters = c.Parameters })
                        .ToList();

                    sql = commands[0].Sql;
                    parameters = commands[0].Parameters;
                }
                else
                {
                    sql = CallToQueryString(result, dbContextType);
                    parameters = ParseParameters(sql);
                    commands = [new QuerySqlCommand { Sql = sql, Parameters = parameters }];

                    warnings.Add(new QueryWarning
                    {
                        Severity = WarningSeverity.Warning,
                        Code = "QL_CAPTURE_FALLBACK",
                        Message = "Exact SQL command capture was unavailable; fell back to ToQueryString().",
                        Suggestion = captureError,
                    });
                }
            }
            else
            {
                sql = CallToQueryString(result, dbContextType);
                parameters = ParseParameters(sql);
                commands = [new QuerySqlCommand { Sql = sql, Parameters = parameters }];

                warnings.Add(new QueryWarning
                {
                    Severity = WarningSeverity.Info,
                    Code = "QL_CAPTURE_SKIPPED",
                    Message = "Exact SQL command capture was skipped for this context configuration.",
                    Suggestion = captureSkipReason,
                });
            }

            sw.Stop();

            return new QueryTranslationResult
            {
                Success = true,
                Sql = sql,
                Commands = commands,
                Parameters = parameters,
                Warnings = warnings,
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
        TranslationRequest request,
        CancellationToken ct)
    {
        // EagerLoadBinDirAssemblies has been moved to ProjectAssemblyContext constructor
        // so it happens before DbContext discovery.

        var assemblySetHash = ScriptStateCache.ComputeAssemblySetHash(
            alcCtx.LoadedAssemblies.ToArray());
        var scriptContextHash = ComputeScriptContextHash(request);

        var cached = _cache.TryGet(
            alcCtx.AssemblyPath,
            dbContextType.FullName!,
            alcCtx.AssemblyTimestamp,
            assemblySetHash,
            scriptContextHash);

        if (cached is not null)
            return cached;

        var freshState = await BuildInitialScriptStateAsync(
            alcCtx, dbContextType, dbInstance, request, ct);

        _cache.Store(
            alcCtx.AssemblyPath,
            dbContextType.FullName!,
            alcCtx.AssemblyTimestamp,
            assemblySetHash,
            scriptContextHash,
            freshState);

        return freshState;
    }

    private static async Task<ScriptState<object>> BuildInitialScriptStateAsync(
        ProjectAssemblyContext alcCtx,
        Type dbContextType,
        object dbInstance,
        TranslationRequest request,
        CancellationToken ct)
    {
        // EagerLoadBinDirAssemblies has already been called by GetOrBuildScriptStateAsync
        // before this point, so LoadedAssemblies is fully populated here.
        var refs = CollectMetadataReferences(alcCtx).ToList();

        // Ensure Queryable and EF Core extension methods are always referenced.
        // In the LSP host, these assemblies might not be eager-loaded into the ALC 
        // if the user's ALC hasn't executed EF Core code yet, which causes Roslyn
        // to bind .Where() to IEnumerable instead of IQueryable.
        refs.Add(MetadataReference.CreateFromFile(typeof(Queryable).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(
            typeof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions).Assembly.Location));

        var imports = new HashSet<string>(StringComparer.Ordinal)
        {
            "System",
            "System.Linq",
            "System.Collections.Generic",
            "Microsoft.EntityFrameworkCore",
        };

        foreach (var import in request.AdditionalImports)
        {
            if (IsValidUsingName(import))
            {
                imports.Add(import);
            }
        }

        var scriptOptions = ScriptOptions.Default
            .AddReferences(refs)
            .AddImports(imports.ToArray());

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

        var preamble = BuildScriptPreamble(dbContextType, request.ContextVariableName, request);

        var script = CSharpScript.Create<object>(
            preamble,
            scriptOptions,
            globalsType: typeof(QueryScriptGlobals));

        var initial = await script.RunAsync(globals, cancellationToken: ct);

        return initial;
    }

    private static string ComputeScriptContextHash(TranslationRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("ctx:").Append(request.ContextVariableName).Append('|');

        foreach (var import in request.AdditionalImports
                     .Where(i => !string.IsNullOrWhiteSpace(i))
                     .Order(StringComparer.Ordinal))
        {
            sb.Append("i:").Append(import).Append('|');
        }

        foreach (var alias in request.UsingAliases.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            sb.Append("a:").Append(alias.Key).Append('=').Append(alias.Value).Append('|');
        }

        foreach (var staticType in request.UsingStaticTypes
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Order(StringComparer.Ordinal))
        {
            sb.Append("s:").Append(staticType).Append('|');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..8];
    }

    private static string BuildScriptPreamble(
        Type dbContextType,
        string contextVariableName,
        TranslationRequest request)
    {
        var sb = new StringBuilder();

        foreach (var alias in request.UsingAliases
                     .Where(kvp => IsValidAliasName(kvp.Key) && IsValidUsingName(kvp.Value))
                     .OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            sb.Append("using ")
                .Append(alias.Key)
                .Append(" = ")
                .Append(alias.Value)
                .AppendLine(";");
        }

        foreach (var staticType in request.UsingStaticTypes
                     .Where(IsValidUsingName)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            sb.Append("using static ")
                .Append(staticType)
                .AppendLine(";");
        }

        sb.Append("var ")
            .Append(contextVariableName)
            .Append(" = (")
            .Append(dbContextType.FullName)
            .AppendLine(")(object)__ql_raw_ctx__;");

        return sb.ToString();
    }

    private static bool IsValidAliasName(string aliasName) =>
        !string.IsNullOrWhiteSpace(aliasName) && SyntaxFacts.IsValidIdentifier(aliasName);

    private static bool IsValidUsingName(string typeOrNamespaceName)
    {
        if (string.IsNullOrWhiteSpace(typeOrNamespaceName))
        {
            return false;
        }

        var parsed = SyntaxFactory.ParseName(typeOrNamespaceName);
        return !parsed.ContainsDiagnostics;
    }

    private static bool LooksLikeTypeOrNamespacePrefix(
        string identifier,
        string expression,
        IReadOnlyDictionary<string, string> usingAliases)
    {
        if (usingAliases.ContainsKey(identifier))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(identifier) || !char.IsUpper(identifier[0]))
        {
            return false;
        }

        var qualifiedAccessPattern = $@"(?<!\w){Regex.Escape(identifier)}\s*\.\s*[A-Z_]";
        return Regex.IsMatch(expression, qualifiedAccessPattern);
    }

    /// <summary>
    /// Attempts to infer the CLR type for a missing variable by searching the LINQ
    /// expression for comparison patterns like <c>w.PropertyName == variableName</c>
    /// and looking up <c>PropertyName</c> on the DbContext's entity types (DbSet properties).
    /// </summary>
    private static Type? InferVariableType(string variableName, string expression, Type dbContextType)
    {
        // Search for patterns: .PropertyName == variableName  OR  variableName == .PropertyName
        var pattern = $@"\.(\w+)\s*(?:==|!=|>|<|>=|<=)\s*{Regex.Escape(variableName)}(?!\w)" +
                      "|" +
                      $@"(?<!\w){Regex.Escape(variableName)}\s*(?:==|!=|>|<|>=|<=)\s*\w+\.(\w+)";

        var match = Regex.Match(expression, pattern);
        if (!match.Success)
            return null;

        var propertyName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

        return FindEntityPropertyType(dbContextType, propertyName);
    }

    /// <summary>
    /// Attempts to infer a missing variable used as a collection receiver in a
    /// Contains call, e.g. <c>accountIds.Contains(account.AccountId)</c>.
    /// </summary>
    private static Type? InferContainsElementType(string variableName, string expression, Type dbContextType)
    {
        var pattern = $@"(?<!\w){Regex.Escape(variableName)}\s*\.\s*Contains\s*\(\s*\w+\s*\.\s*(\w+)";
        var match = Regex.Match(expression, pattern);
        if (!match.Success)
            return null;

        var propertyName = match.Groups[1].Value;
        return FindEntityPropertyType(dbContextType, propertyName);
    }

    private static Type? InferSelectEntityType(string variableName, string expression, Type dbContextType)
    {
        var selectPattern = $@"\.\s*Select\s*\(\s*{Regex.Escape(variableName)}\s*\)";
        if (!Regex.IsMatch(expression, selectPattern))
        {
            return null;
        }

        var rootPattern = @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)";
        var rootMatch = Regex.Match(expression, rootPattern);
        if (!rootMatch.Success)
        {
            return null;
        }

        var rootMemberName = rootMatch.Groups[1].Value;
        var rootMember = dbContextType.GetProperty(rootMemberName);
        if (rootMember?.PropertyType.IsGenericType != true)
        {
            return null;
        }

        return rootMember.PropertyType.GetGenericArguments().FirstOrDefault();
    }

    private static bool LooksLikeCancellationTokenArgument(string variableName, string expression)
    {
        var methodArgPattern = $@"\w+Async\s*\([^\)]*\b{Regex.Escape(variableName)}\b[^\)]*\)";
        if (Regex.IsMatch(expression, methodArgPattern))
        {
            return true;
        }

        return string.Equals(variableName, "ct", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variableName, "cancellationToken", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToCSharpTypeName(Type type)
    {
        if (type == typeof(void)) return "void";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(sbyte)) return "sbyte";
        if (type == typeof(char)) return "char";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(int)) return "int";
        if (type == typeof(uint)) return "uint";
        if (type == typeof(long)) return "long";
        if (type == typeof(ulong)) return "ulong";
        if (type == typeof(object)) return "object";
        if (type == typeof(short)) return "short";
        if (type == typeof(ushort)) return "ushort";
        if (type == typeof(string)) return "string";

        if (type.IsArray)
        {
            return $"{ToCSharpTypeName(type.GetElementType()!)}[]";
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            return $"{ToCSharpTypeName(type.GetGenericArguments()[0])}?";
        }

        if (type.IsGenericType)
        {
            var genericDefName = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = genericDefName.IndexOf('`');
            if (tick >= 0)
            {
                genericDefName = genericDefName[..tick];
            }

            genericDefName = genericDefName.Replace('+', '.');
            var args = string.Join(", ", type.GetGenericArguments().Select(ToCSharpTypeName));
            return $"{genericDefName}<{args}>";
        }

        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static Type? FindEntityPropertyType(Type dbContextType, string propertyName)
    {
        // Search the DbContext's entity types for a property with this name.
        foreach (var dbSetProp in dbContextType.GetProperties())
        {
            var propType = dbSetProp.PropertyType;
            if (!propType.IsGenericType) continue;

            var entityType = propType.GetGenericArguments().FirstOrDefault();
            if (entityType is null) continue;

            var entityProp = entityType.GetProperty(propertyName);
            if (entityProp is not null)
                return entityProp.PropertyType;
        }

        return null;
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
                catch
                {
                    /* best-effort — some assemblies legitimately can't load in the default ALC */
                }
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

        // Force a no-op connection implementation so enumeration never opens
        // a real network connection while we capture commands offline.
        builderInstance.ReplaceService<IRelationalConnection, NoOpRelationalConnection>();

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

    private static bool IsNoDbContextFoundError(InvalidOperationException ex) =>
        ex.Message.Contains("No DbContext subclass found", StringComparison.OrdinalIgnoreCase);

    private static void TryLoadSiblingAssembliesForDbContextDiscovery(ProjectAssemblyContext alcCtx)
    {
        var binDir = Path.GetDirectoryName(alcCtx.AssemblyPath);
        if (string.IsNullOrWhiteSpace(binDir) || !Directory.Exists(binDir))
        {
            return;
        }

        var alreadyLoaded = alcCtx.LoadedAssemblies
            .Select(a => a.Location)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(binDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(dll, alcCtx.AssemblyPath, StringComparison.OrdinalIgnoreCase) ||
                alreadyLoaded.Contains(dll))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(dll);
            if (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Pomelo.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                alcCtx.LoadAdditionalAssembly(dll);
            }
            catch
            {
                // Best-effort recovery only.
            }
        }
    }

    private static string? TryExtractRootIdentifier(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var match = Regex.Match(expression, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\.|$)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsUnsupportedTopLevelMethodInvocation(string expression, string contextVariableName)
    {
        var match = Regex.Match(expression,
            @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(");
        if (!match.Success)
        {
            return false;
        }

        var root = match.Groups[1].Value;
        var method = match.Groups[2].Value;

        // Allow dbContext.Set<TEntity>() as a valid query root shape.
        if (string.Equals(root, contextVariableName, StringComparison.Ordinal) &&
            string.Equals(method, "Set", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool EnsureExecutionCaptureSafety(
        object dbInstance,
        string creationStrategy,
        out string? skipReason)
    {
        skipReason = null;

        // Bootstrap already replaces IRelationalConnection with NoOpRelationalConnection.
        if (string.Equals(creationStrategy, "bootstrap", StringComparison.Ordinal))
        {
            return true;
        }

        if (dbInstance is not DbContext dbContext)
        {
            skipReason = "DbContext instance could not be cast to Microsoft.EntityFrameworkCore.DbContext.";
            return false;
        }

        try
        {
            dbContext.Database.SetDbConnection(
                NoOpRelationalConnection.CreateOfflineDbConnection(),
                contextOwnsConnection: true);
            return true;
        }
        catch (Exception ex)
        {
            skipReason =
                $"Could not install offline connection for execution capture ({creationStrategy}): {ex.Message}";
            return false;
        }
    }

    private static IReadOnlyList<CapturedSqlCommand> TryCaptureSqlCommands(
        object queryable,
        out string? captureError)
    {
        captureError = null;

        if (queryable is not System.Collections.IEnumerable enumerable)
        {
            captureError = "Expression result does not implement IEnumerable.";
            return [];
        }

        try
        {
            using var capture = SqlCaptureScope.Begin();
            var enumerator = enumerable.GetEnumerator();

            try
            {
                var guard = 0;
                while (guard++ < 32 && enumerator.MoveNext())
                {
                }
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }

            return [.. capture.Commands];
        }
        catch (Exception ex)
        {
            captureError = ex.Message;
            return [];
        }
    }

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
            DbContextType = dbContextType?.FullName ?? "unknown",
            ProviderName = bootstrap?.ProviderName ?? "unknown",
            EfCoreVersion = GetEfCoreVersion(dbContextType),
            TranslationTime = elapsed,
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
