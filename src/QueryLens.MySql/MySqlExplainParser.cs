using QueryLens.Core;

namespace QueryLens.MySql;

/// <summary>
/// Parses MySQL EXPLAIN output into the normalized ExplainNode tree.
/// Handles two formats:
///   - EXPLAIN FORMAT=JSON  (all MySQL 8.x, estimates only)
///   - EXPLAIN ANALYZE      (MySQL 8.0.18+ / Aurora 3.x, includes actual row counts)
/// </summary>
public sealed class MySqlExplainParser : IExplainParser
{
    public string ProviderName => "Pomelo.EntityFrameworkCore.MySql";

    public ExplainNode Parse(string rawExplainOutput)
    {
        // Phase 2 implementation: parse JSON or ANALYZE text output into ExplainNode.
        throw new NotImplementedException(
            "MySqlExplainParser.Parse is not yet implemented. Coming in Phase 1.");
    }
}
