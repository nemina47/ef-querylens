namespace EFQueryLens.Core;

/// <summary>
/// Parses raw EXPLAIN output from a specific database provider into
/// the provider-agnostic ExplainNode tree.
/// </summary>
public interface IExplainParser
{
    string ProviderName { get; }

    ExplainNode Parse(string rawExplainOutput);
}
