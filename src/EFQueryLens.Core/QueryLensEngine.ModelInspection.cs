using System.Reflection;
using System.Runtime.Loader;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Scripting;

namespace EFQueryLens.Core;

public sealed partial class QueryLensEngine
{
    // DbContext instantiation for model inspection
    private object CreateDbContextForInspection(Type dbContextType, ProjectAssemblyContext alcCtx)
    {
        // For model inspection we keep EF design-time factory as the primary path
        // (stable parity with EF tooling) and support QueryLens factory as fallback.
        // Search across both user ALC and default ALC.
        var allAssemblies = alcCtx.LoadedAssemblies.Concat(AssemblyLoadContext.Default.Assemblies);

        var fromDesignTimeFactory = DesignTimeDbContextFactory.TryCreate(
            dbContextType,
            allAssemblies,
            alcCtx.AssemblyPath);
        if (fromDesignTimeFactory is not null)
        {
            return fromDesignTimeFactory;
        }

        var fromQueryLensFactory = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            dbContextType,
            allAssemblies,
            alcCtx.AssemblyPath);
        if (fromQueryLensFactory is not null)
        {
            return fromQueryLensFactory;
        }

        throw new InvalidOperationException(
            $"Could not create an instance of '{dbContextType.FullName}' for model inspection. " +
            "Implement IDesignTimeDbContextFactory<T> or IQueryLensDbContextFactory<T> in the executable project (API / Worker / Console), not in a class library. " +
            $"Selected executable assembly: '{Path.GetFileName(alcCtx.AssemblyPath)}'.");
    }

    // Model snapshot building
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

        // IModel.GetEntityTypes() - navigate via interface to survive EF minor versions.
        var getEntityTypes = modelType.GetMethod("GetEntityTypes")
            ?? modelType.GetInterfaces()
                .SelectMany(i => i.GetMethods())
                .FirstOrDefault(m => m.Name == "GetEntityTypes");

        if (getEntityTypes is null)
        {
            return new ModelSnapshot
            {
                DbContextType = dbContextType.FullName!,
                DbSetProperties = dbSetProperties,
            };
        }

        var entityTypes = (System.Collections.IEnumerable)getEntityTypes.Invoke(model, null)!;

        var entities = new List<EntitySnapshot>();
        foreach (var entityType in entityTypes)
        {
            entities.Add(MapEntity(entityType));
        }

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
                    {
                        cols.Add((string)(ip.GetType().GetProperty("Name")?.GetValue(ip) ?? "?"));
                    }
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

}
