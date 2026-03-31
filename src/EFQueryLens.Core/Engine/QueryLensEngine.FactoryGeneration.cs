using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.DesignTime;
using EFQueryLens.Core.Scripting.Evaluation;

namespace EFQueryLens.Core.Engine;

public sealed partial class QueryLensEngine
{
    public Task<FactoryGenerationResult> GenerateFactoryAsync(
        FactoryGenerationRequest request,
        CancellationToken ct = default)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(request);

            var assemblyPath = Path.GetFullPath(request.AssemblyPath);
            var alcCtx = GetOrRefreshContext(assemblyPath);

            Type dbContextType;
            try
            {
                dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName);
            }
            catch (InvalidOperationException ex) when (QueryEvaluator.IsNoDbContextFoundError(ex))
            {
                QueryEvaluator.TryLoadSiblingAssemblies(alcCtx);
                dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName);
            }

            var fullName = dbContextType.FullName
                ?? dbContextType.Name;

            var content = FactoryCodeGenerator.Generate(fullName, alcCtx.LoadedAssemblies);
            var suggestedFileName = FactoryCodeGenerator.SuggestFileName(fullName);

            return Task.FromResult(new FactoryGenerationResult
            {
                Content = content,
                SuggestedFileName = suggestedFileName,
                DbContextTypeFullName = fullName,
            });
        }
        catch (Exception ex)
        {
            return Task.FromException<FactoryGenerationResult>(ex);
        }
    }
}
