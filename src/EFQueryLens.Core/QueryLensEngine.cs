using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Scripting;

namespace EFQueryLens.Core;

/// <summary>
/// Default implementation of <see cref="IQueryLensEngine"/>.
///
/// Orchestrates the ALC cache, the Roslyn scripting evaluator, and EF Core
/// model inspection without ever opening a real database connection.
/// </summary>
public sealed class QueryLensEngine : IQueryLensEngine
{
    private readonly QueryEvaluator _evaluator = new();
    private readonly ConcurrentDictionary<string, ProjectAssemblyContext> _alcCache = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly bool _debugEnabled;
    private bool _disposed;

    public QueryLensEngine()
    {
        _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
    }

    // ── IQueryLensEngine ──────────────────────────────────────────────────────

    public async Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var fullPath = Path.GetFullPath(request.AssemblyPath);
        var alcCtx = GetOrRefreshContext(fullPath);
        var result = await _evaluator.EvaluateAsync(alcCtx, request, ct);

        if (!NeedsDbContextDiscoveryRetry(result))
            return result;

        // Self-heal long-running hosts (LSP): evict the cached context and retry once.
        // This recovers from contexts created before dependency outputs existed or from
        // transient assembly-load failures during initial discovery.
        if (_alcCache.TryRemove(fullPath, out var stale))
            stale.Dispose();

        var freshCtx = GetOrRefreshContext(fullPath);
        return await _evaluator.EvaluateAsync(freshCtx, request, ct);
    }

    public Task<ExplainResult> ExplainAsync(
        ExplainRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException(
            "ExplainAsync requires a live database connection and is implemented in Phase 2.");

    public async Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        LogDebug($"inspect-model-start assembly={request.AssemblyPath}");

        try
        {
            var alcCtx = GetOrRefreshContext(request.AssemblyPath);
            var dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName);
            var dbInstance = CreateDbContextForInspection(dbContextType, alcCtx);

            var snapshot = BuildModelSnapshot(dbInstance, dbContextType);
            LogDebug($"inspect-model-success context={snapshot.DbContextType} dbSets={snapshot.DbSetProperties.Count} entities={snapshot.Entities.Count}");
            return snapshot;
        }
        catch (Exception ex)
        {
            LogDebug($"inspect-model-failure type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var ctx in _alcCache.Values)
            ctx.Dispose();
        _alcCache.Clear();

    }

    // ── ALC cache ─────────────────────────────────────────────────────────────

    private ProjectAssemblyContext GetOrRefreshContext(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);

        if (_alcCache.TryGetValue(fullPath, out var existing))
        {
            if (!ProjectAssemblyContextFactory.IsStale(existing))
                return existing;

            // Assembly was rebuilt — evict the stale context and reload.
            _alcCache.TryRemove(fullPath, out _);
            existing.Dispose();
        }

        var fresh = ProjectAssemblyContextFactory.Create(fullPath);
        _alcCache[fullPath] = fresh;
        return fresh;
    }

    // ── DbContext instantiation for model inspection ───────────────────────────

    private object CreateDbContextForInspection(Type dbContextType, ProjectAssemblyContext alcCtx)
    {
        // For model inspection we keep EF design-time factory as the primary path
        // (stable parity with EF tooling) and support QueryLens factory as fallback.
        // Search across both user ALC and default ALC.
        var allAssemblies = alcCtx.LoadedAssemblies.Concat(AssemblyLoadContext.Default.Assemblies);

        var fromDesignTimeFactory = DesignTimeDbContextFactory.TryCreate(
            dbContextType,
            allAssemblies);
        if (fromDesignTimeFactory is not null)
            return fromDesignTimeFactory;

        var fromQueryLensFactory = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            dbContextType,
            allAssemblies);
        if (fromQueryLensFactory is not null)
            return fromQueryLensFactory;

        throw new InvalidOperationException(
            $"Could not create an instance of '{dbContextType.FullName}' for model inspection. " +
            "Implement IDesignTimeDbContextFactory<T> or IQueryLensDbContextFactory<T> in the project.");
    }

    // ── Model snapshot building ───────────────────────────────────────────────

    private static ModelSnapshot BuildModelSnapshot(object dbInstance, Type dbContextType)
    {
        var dbSetProperties = dbContextType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(IsDbSetProperty)
            .Select(p => p.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var modelProp = dbContextType.GetProperty("Model",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "Could not find Model property on DbContext.");

        var model = modelProp.GetValue(dbInstance)!;
        var modelType = model.GetType();

        // IModel.GetEntityTypes() — navigate via interface to survive EF minor versions.
        var getEntityTypes = modelType.GetMethod("GetEntityTypes")
            ?? modelType.GetInterfaces()
                .SelectMany(i => i.GetMethods())
                .FirstOrDefault(m => m.Name == "GetEntityTypes");

        if (getEntityTypes is null)
            return new ModelSnapshot
            {
                DbContextType = dbContextType.FullName!,
                DbSetProperties = dbSetProperties,
            };

        var entityTypes = (System.Collections.IEnumerable)getEntityTypes.Invoke(model, null)!;

        var entities = new List<EntitySnapshot>();
        foreach (var entityType in entityTypes)
            entities.Add(MapEntity(entityType));

        return new ModelSnapshot
        {
            DbContextType = dbContextType.FullName!,
            DbSetProperties = dbSetProperties,
            Entities = entities,
        };
    }

    private static bool IsDbSetProperty(PropertyInfo property)
    {
        var propertyType = property.PropertyType;
        if (!propertyType.IsGenericType)
        {
            return false;
        }

        var genericTypeDefinition = propertyType.GetGenericTypeDefinition();
        return string.Equals(genericTypeDefinition.FullName,
            "Microsoft.EntityFrameworkCore.DbSet`1",
            StringComparison.Ordinal);
    }

    private static bool ReadBoolEnvironmentVariable(string variableName, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-Engine] {message}");
    }

    private static EntitySnapshot MapEntity(object entityType)
    {
        var et = entityType.GetType();
        var clrType = (Type)(et.GetProperty("ClrType")?.GetValue(entityType)
            ?? throw new InvalidOperationException("IEntityType.ClrType not found."));

        var tableName = GetTableName(entityType) ?? clrType.Name;

        // Properties
        var getProps = FindMethod(et, "GetProperties");
        var properties = new List<PropertySnapshot>();
        if (getProps is not null)
        {
            var props = (System.Collections.IEnumerable)getProps.Invoke(entityType, null)!;
            foreach (var p in props)
            {
                var pt = p.GetType();
                var name = (string)(pt.GetProperty("Name")?.GetValue(p) ?? "?");
                var propClrType = (Type)(pt.GetProperty("ClrType")?.GetValue(p) ?? typeof(object));
                var colName = GetColumnName(p) ?? name;
                var isPrimary = IsKey(p, et, entityType);
                var isNullable = (bool)(pt.GetProperty("IsNullable")?.GetValue(p) ?? false);

                properties.Add(new PropertySnapshot
                {
                    Name = name,
                    ClrType = propClrType.Name,
                    ColumnName = colName,
                    IsKey = isPrimary,
                    IsNullable = isNullable,
                });
            }
        }

        // Navigations
        var getNavs = FindMethod(et, "GetNavigations");
        var navigations = new List<NavigationSnapshot>();
        if (getNavs is not null)
        {
            var navs = (System.Collections.IEnumerable)getNavs.Invoke(entityType, null)!;
            foreach (var n in navs)
            {
                var nt = n.GetType();
                var navName = (string)(nt.GetProperty("Name")?.GetValue(n) ?? "?");
                var isCollection = (bool)(nt.GetProperty("IsCollection")?.GetValue(n) ?? false);
                var targetEt = nt.GetProperty("TargetEntityType")?.GetValue(n);
                var targetClr = targetEt is not null
                    ? (Type)(targetEt.GetType().GetProperty("ClrType")?.GetValue(targetEt)
                             ?? typeof(object))
                    : typeof(object);

                navigations.Add(new NavigationSnapshot
                {
                    Name = navName,
                    TargetEntity = targetClr.Name,
                    IsCollection = isCollection,
                });
            }
        }

        // Indexes
        var getIndexes = FindMethod(et, "GetIndexes");
        var indexes = new List<IndexSnapshot>();
        if (getIndexes is not null)
        {
            var idxs = (System.Collections.IEnumerable)getIndexes.Invoke(entityType, null)!;
            foreach (var idx in idxs)
            {
                var it = idx.GetType();
                var isUnique = (bool)(it.GetProperty("IsUnique")?.GetValue(idx) ?? false);
                var nameVal = it.GetProperty("Name")?.GetValue(idx) as string;
                var propsList = it.GetProperty("Properties")?.GetValue(idx);
                var cols = new List<string>();

                if (propsList is System.Collections.IEnumerable pEnum)
                {
                    foreach (var ip in pEnum)
                        cols.Add((string)(ip.GetType().GetProperty("Name")?.GetValue(ip) ?? "?"));
                }

                indexes.Add(new IndexSnapshot
                {
                    Columns = cols,
                    IsUnique = isUnique,
                    Name = nameVal,
                });
            }
        }

        return new EntitySnapshot
        {
            ClrType = clrType.FullName ?? clrType.Name,
            TableName = tableName,
            Properties = properties,
            Navigations = navigations,
            Indexes = indexes,
        };
    }

    // ── Reflection helpers ─────────────────────────────────────────────────────

    /// <summary>Finds a named method on a type or any of its interfaces.</summary>
    private static MethodInfo? FindMethod(Type type, string methodName) =>
        type.GetMethod(methodName)
        ?? type.GetInterfaces()
            .SelectMany(i => i.GetMethods())
            .FirstOrDefault(m => m.Name == methodName);

    /// <summary>
    /// Returns the relational table name via
    /// <c>RelationalEntityTypeExtensions.GetTableName(IEntityType)</c>.
    /// Wrapped in try/catch — extension method signatures vary between EF Core minors.
    /// </summary>
    private static string? GetTableName(object entityType)
    {
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(entityType.GetType().Assembly);
            var relAsm = (alc?.Assemblies ?? Enumerable.Empty<Assembly>())
                .Concat(AssemblyLoadContext.Default.Assemblies)
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore.Relational");

            var extType = relAsm?.GetType(
                "Microsoft.EntityFrameworkCore.RelationalEntityTypeExtensions");
            var method = extType?.GetMethod("GetTableName",
                BindingFlags.Public | BindingFlags.Static);

            return method?.Invoke(null, [entityType]) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the relational column name via
    /// <c>RelationalPropertyExtensions.GetColumnName(IReadOnlyProperty)</c>.
    /// </summary>
    private static string? GetColumnName(object property)
    {
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(property.GetType().Assembly);
            var relAsm = (alc?.Assemblies ?? Enumerable.Empty<Assembly>())
                .Concat(AssemblyLoadContext.Default.Assemblies)
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore.Relational");

            var extType = relAsm?.GetType(
                "Microsoft.EntityFrameworkCore.RelationalPropertyExtensions");

            // GetColumnName has overloads; take the one with a single parameter.
            var method = extType?
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetColumnName" &&
                    m.GetParameters().Length == 1);

            return method?.Invoke(null, [property]) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns true if the property is part of any primary key on the entity type.
    /// </summary>
    private static bool IsKey(object property, Type entityTypeType, object entityType)
    {
        try
        {
            var getKeys = FindMethod(entityTypeType, "GetKeys");
            if (getKeys is null) return false;

            var keys = (System.Collections.IEnumerable)getKeys.Invoke(entityType, null)!;
            var propName = (string)(property.GetType().GetProperty("Name")?.GetValue(property) ?? "");

            foreach (var key in keys)
            {
                var keyProps = key.GetType().GetProperty("Properties")?.GetValue(key);
                if (keyProps is System.Collections.IEnumerable kpEnum)
                {
                    foreach (var kp in kpEnum)
                    {
                        var kpName = (string)(kp.GetType().GetProperty("Name")?.GetValue(kp) ?? "");
                        if (kpName == propName) return true;
                    }
                }
            }
            return false;
        }
        catch { return false; }
    }

    private static bool NeedsDbContextDiscoveryRetry(QueryTranslationResult result) =>
        !result.Success &&
        !string.IsNullOrWhiteSpace(result.ErrorMessage) &&
        result.ErrorMessage.Contains("No DbContext subclass found", StringComparison.OrdinalIgnoreCase);
}
