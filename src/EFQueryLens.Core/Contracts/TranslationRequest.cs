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
}
