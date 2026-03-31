namespace EFQueryLens.Core.Contracts;

public record TranslationRequest
{
    /// <summary>
    /// LINQ expression as C# source text.
    /// Example: "db.Orders.Where(o => o.UserId == 5).Include(o => o.Items)"
    /// </summary>
    public required string Expression { get; init; }

    public required string AssemblyPath { get; init; }

    /// <summary>null = auto-discover the first DbContext found in the assembly.</summary>
    public string? DbContextTypeName { get; init; }

    /// <summary>Name of the DbContext variable in the script globals. Defaults to "db".</summary>
    public string ContextVariableName { get; init; } = "db";

    /// <summary>
    /// Extra namespace imports used when compiling the expression in Roslyn script.
    /// This is mainly populated by the LSP host from source <c>using</c> directives.
    /// </summary>
    public IReadOnlyList<string> AdditionalImports { get; init; } = [];

    /// <summary>
    /// Alias imports (for example <c>using Enums = My.Namespace.Enums;</c>)
    /// carried from source files into the script preamble.
    /// </summary>
    public IReadOnlyDictionary<string, string> UsingAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Static imports (for example <c>using static System.Math;</c>) carried
    /// from source files into the script preamble.
    /// </summary>
    public IReadOnlyList<string> UsingStaticTypes { get; init; } = [];

    /// <summary>
    /// CLR type name strings for local variables visible at the cursor position,
    /// extracted by the LSP from the source file's syntax tree.
    /// The engine uses these as authoritative types when synthesising stub declarations,
    /// falling back to heuristics only for variables absent from this map.
    /// </summary>
    public IReadOnlyDictionary<string, string> LocalVariableTypes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// When true, generates and invokes an async runner method (<c>RunAsync</c>)
    /// without sync fallback. Default is false for compatibility.
    /// </summary>
    public bool UseAsyncRunner { get; init; } = false;

    /// <summary>
    /// Snapshot of how the LSP resolved the target DbContext at hover time.
    /// Carries both local declared-type hints and factory-derived concrete candidates
    /// so the daemon can validate and disambiguate against the loaded runtime model.
    /// </summary>
    public DbContextResolutionSnapshot? DbContextResolution { get; init; }

    /// <summary>
    /// Complete using context snapshot from LSP extraction: all using directives,
    /// aliases, and static usings from the source file at hover time.
    /// Serialized to avoid re-parsing the file in daemon; enables deterministic
    /// caching across multiple hover requests on the same expression.
    /// </summary>
    public UsingContextSnapshot? UsingContextSnapshot { get; init; }

    /// <summary>
    /// Metadata about the parsed expression: its syntactic type (Invocation, Query, MemberAccess),
    /// the file location where it was extracted, and confidence that it's a valid LINQ query.
    /// Used by daemon for validation and debugging; enables cross-version compatibility checks.
    /// </summary>
    public ParsedExpressionMetadata? ExpressionMetadata { get; init; }
}

/// <summary>
/// Snapshot of DbContext resolution signals observed by the LSP.
/// </summary>
public record DbContextResolutionSnapshot
{
    /// <summary>
    /// Type name declared in source for the hovered context variable.
    /// May be a concrete DbContext, an interface, or a simple name.
    /// </summary>
    public string? DeclaredTypeName { get; init; }

    /// <summary>
    /// Single concrete type derived from QueryLens factory discovery when unambiguous.
    /// </summary>
    public string? FactoryTypeName { get; init; }

    /// <summary>
    /// All concrete DbContext types declared by QueryLens factories in the selected host project.
    /// Populated when more than one factory exists so Core can disambiguate with expression shape.
    /// </summary>
    public IReadOnlyList<string> FactoryCandidateTypeNames { get; init; } = [];

    /// <summary>
    /// Human-readable description of which hint sources contributed to this snapshot.
    /// </summary>
    public string? ResolutionSource { get; init; }

    /// <summary>
    /// LSP confidence that the selected DbContext hint is correct (0.0 to 1.0).
    /// Lower values indicate conflicting or ambiguous hints that should be revalidated in Core.
    /// </summary>
    public double Confidence { get; init; } = 1.0;
}

/// <summary>
/// Complete snapshot of using context extracted by LSP from source file.
/// Enables daemon to cache and skip re-parsing the same file on repeated requests.
/// </summary>
public record UsingContextSnapshot
{
    /// <summary>All regular namespace imports (using X.Y.Z;).</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>Namespace aliases (using Alias = X.Y.Z;).</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Static type imports (using static X.Y.Z;).</summary>
    public IReadOnlyList<string> StaticTypes { get; init; } = [];
}

/// <summary>
/// Metadata about the parsed LINQ expression extracted by LSP.
/// Used for validation, caching, and cross-version compatibility.
/// </summary>
public record ParsedExpressionMetadata
{
    /// <summary>
    /// Syntactic expression type: "Invocation" (.ToList()), "Query" (from...select),
    /// or "MemberAccess" (db.Orders). Helps daemon validate expression shape.
    /// </summary>
    public string? ExpressionType { get; init; }

    /// <summary>
    /// Zero-based line number in source file where expression starts. Enables
    /// daemon to validate that expression still exists at same location if file changed.
    /// </summary>
    public int SourceLine { get; init; }

    /// <summary>
    /// Zero-based character offset on SourceLine where expression starts.
    /// </summary>
    public int SourceCharacter { get; init; }

    /// <summary>
    /// LSP's confidence that this is a valid LINQ query chain (0.0 to 1.0).
    /// High confidence = recognized pattern (method chain); low = heuristic fallback.
    /// Helpful for daemon diagnostics.
    /// </summary>
    public double Confidence { get; init; } = 1.0;
}
