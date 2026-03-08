using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using QueryLens.Core;
using QueryLens.Lsp.Parsing;

namespace QueryLens.Lsp.Handlers;

internal sealed class CodeLensHandler : CodeLensHandlerBase
{
    private const int DefaultMaxCodeLensPerDocument = 50;
    private const int DefaultDebounceMilliseconds = 250;
    private const bool DefaultUseModelFilter = false;

    private readonly IQueryLensEngine _engine;
    private readonly DocumentManager _documentManager;
    private readonly int _maxCodeLensPerDocument;
    private readonly int _debounceMilliseconds;
    private readonly bool _useModelFilter;
    private readonly bool _debugEnabled;
    private readonly ConcurrentDictionary<string, CachedDbSetNames> _dbSetCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _assemblyPathCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedCodeLensResult> _codeLensCache =
        new(StringComparer.OrdinalIgnoreCase);

    public CodeLensHandler(IQueryLensEngine engine, DocumentManager documentManager)
    {
        _engine = engine;
        _documentManager = documentManager;
        _maxCodeLensPerDocument = ReadIntEnvironmentVariable(
            "QUERYLENS_MAX_CODELENS_PER_DOCUMENT",
            DefaultMaxCodeLensPerDocument,
            min: 1,
            max: 500);
        _debounceMilliseconds = ReadIntEnvironmentVariable(
            "QUERYLENS_CODELENS_DEBOUNCE_MS",
            DefaultDebounceMilliseconds,
            min: 0,
            max: 5000);
        _useModelFilter = ReadBoolEnvironmentVariable(
            "QUERYLENS_CODELENS_USE_MODEL_FILTER",
            DefaultUseModelFilter);
        _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);

        LogDebug($"initialized max={_maxCodeLensPerDocument} debounceMs={_debounceMilliseconds} useModelFilter={_useModelFilter}");
    }

    public override async Task<CodeLensContainer?> Handle(CodeLensParams request, CancellationToken cancellationToken)
    {
        try
        {
            LogDebug($"handle-start uri={request.TextDocument.Uri}");

            var sourceText = await GetSourceTextAsync(request.TextDocument.Uri, cancellationToken);
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                LogDebug("handle-exit reason=empty-source");
                return new CodeLensContainer();
            }

            var filePath = request.TextDocument.Uri.GetFileSystemPath();
            var sourceHash = StringComparer.Ordinal.GetHashCode(sourceText);
            var nowTicks = DateTime.UtcNow.Ticks;
            if (_debounceMilliseconds > 0
                && _codeLensCache.TryGetValue(filePath, out var cachedLensResult)
                && cachedLensResult.SourceHash == sourceHash
                && cachedLensResult.CreatedAtTicks + TimeSpan.FromMilliseconds(_debounceMilliseconds).Ticks > nowTicks)
            {
                LogDebug("handle-exit reason=cache-hit");
                return cachedLensResult.Lenses;
            }

            var chainInfos = LspSyntaxHelper.FindAllLinqChains(sourceText);
            LogDebug($"chains-found count={chainInfos.Count}");
            if (chainInfos.Count == 0)
            {
                LogDebug("handle-exit reason=no-chains");
                return new CodeLensContainer();
            }

            var targetAssembly = ResolveTargetAssembly(filePath);
            LogDebug($"assembly-resolved path={(targetAssembly ?? "<null>")}");
            if (string.IsNullOrWhiteSpace(targetAssembly)
                || targetAssembly.StartsWith("DEBUG_FAIL", StringComparison.Ordinal)
                || !File.Exists(targetAssembly))
            {
                LogDebug("handle-exit reason=assembly-not-found");
                return new CodeLensContainer();
            }

            IReadOnlySet<string>? dbSetNames = null;
            if (_useModelFilter)
            {
                dbSetNames = await GetDbSetNamesAsync(targetAssembly, cancellationToken);
                LogDebug($"dbset-filter status={(dbSetNames is null ? "unavailable" : dbSetNames.Count.ToString())}");
                if (dbSetNames is { Count: 0 })
                {
                    LogDebug("handle-exit reason=empty-dbsets");
                    return new CodeLensContainer();
                }
            }
            else
            {
                LogDebug("dbset-filter status=disabled");
            }

            var lenses = new List<CodeLens>();
            var matchedQueries = 0;

            foreach (var chain in chainInfos)
            {
                if (dbSetNames is not null && !dbSetNames.Contains(chain.DbSetMemberName))
                {
                    continue;
                }

                matchedQueries++;
                if (matchedQueries > _maxCodeLensPerDocument)
                {
                    break;
                }

                var arguments = new JArray
                {
                    request.TextDocument.Uri.ToString(),
                    chain.Line,
                    chain.Character,
                };

                var range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(chain.Line, 0),
                    new Position(chain.Line, 0));

                lenses.Add(new CodeLens
                {
                    Range = range,
                    Command = new Command
                    {
                        Name = "querylens.showSql",
                        Title = $"QueryLens · {chain.DbSetMemberName}",
                        Arguments = arguments,
                    }
                });

                lenses.Add(new CodeLens
                {
                    Range = range,
                    Command = new Command
                    {
                        Name = "querylens.copySql",
                        Title = "Copy SQL",
                        Arguments = arguments.DeepClone() as JArray ?? arguments,
                    }
                });
            }

            var result = new CodeLensContainer(lenses);
            _codeLensCache[filePath] = new CachedCodeLensResult(sourceHash, nowTicks, result);
            LogDebug($"handle-success lenses={lenses.Count}");
            return result;
        }
        catch (Exception ex)
        {
            LogDebug($"handle-exception type={ex.GetType().Name} message={ex.Message}");
            return new CodeLensContainer();
        }
    }

    public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    protected override CodeLensRegistrationOptions CreateRegistrationOptions(
        CodeLensCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new CodeLensRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("csharp"),
            ResolveProvider = false,
        };
    }

    private async Task<IReadOnlySet<string>?> GetDbSetNamesAsync(string assemblyPath, CancellationToken cancellationToken)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(assemblyPath);
            var assemblyTimestamp = File.GetLastWriteTimeUtc(normalizedPath).Ticks;

            if (_dbSetCache.TryGetValue(normalizedPath, out var cached)
                && cached.AssemblyTimestamp == assemblyTimestamp)
            {
                return cached.DbSetNames;
            }

            var snapshot = await _engine.InspectModelAsync(new ModelInspectionRequest
            {
                AssemblyPath = normalizedPath,
            }, cancellationToken);

            var names = snapshot.DbSetProperties
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal);

            _dbSetCache[normalizedPath] = new CachedDbSetNames(assemblyTimestamp, names);
            LogDebug($"dbset-cache-update path={normalizedPath} count={names.Count}");
            return names;
        }
        catch (Exception ex)
        {
            // Do not block CodeLens entirely when model inspection fails for a project.
            // In that case we fall back to syntax-only candidate chains.
            LogDebug($"dbset-inspection-failed type={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    private async Task<string?> GetSourceTextAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        var sourceText = _documentManager.GetDocumentText(uri);
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        var filePath = uri.GetFileSystemPath();
        if (!File.Exists(filePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(filePath, cancellationToken);
    }

    private string? ResolveTargetAssembly(string filePath)
    {
        if (_assemblyPathCache.TryGetValue(filePath, out var cachedAssemblyPath)
            && File.Exists(cachedAssemblyPath))
        {
            return cachedAssemblyPath;
        }

        var resolved = AssemblyResolver.TryGetTargetAssembly(filePath);
        if (!string.IsNullOrWhiteSpace(resolved)
            && !resolved.StartsWith("DEBUG_FAIL", StringComparison.Ordinal)
            && File.Exists(resolved))
        {
            _assemblyPathCache[filePath] = resolved;
        }

        return resolved;
    }

    private static int ReadIntEnvironmentVariable(string variableName, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (!int.TryParse(raw, out var value))
        {
            return fallback;
        }

        if (value < min) return min;
        if (value > max) return max;
        return value;
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

        Console.Error.WriteLine($"[QL-CodeLens] {message}");
    }

    private sealed record CachedDbSetNames(long AssemblyTimestamp, IReadOnlySet<string> DbSetNames);
    private sealed record CachedCodeLensResult(int SourceHash, long CreatedAtTicks, CodeLensContainer Lenses);
}
