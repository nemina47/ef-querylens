namespace EFQueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private static IReadOnlyList<QueryParameter> ParseParameters(string sql)
    {
        var parameters = new List<QueryParameter>();
        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("-- @", StringComparison.Ordinal))
                continue;

            var content = trimmed[3..].Trim();
            var nameEnd = content.IndexOfAny(['=', ' ']);
            if (nameEnd < 0)
                continue;

            parameters.Add(new QueryParameter
            {
                Name = content[..nameEnd].Trim(),
                ClrType = ExtractDbType(content),
                InferredValue = ExtractInferredValue(content),
            });
        }

        return parameters;
    }

    private static string ExtractDbType(string a)
    {
        const string marker = "DbType = ";
        var i = a.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return "object";

        var s = i + marker.Length;
        var e = a.IndexOf(')', s);
        return e > s ? a[s..e].Trim() : "object";
    }

    private static string? ExtractInferredValue(string a)
    {
        var i = a.IndexOf("='", StringComparison.Ordinal);
        if (i < 0)
            return null;

        var s = i + 2;
        var e = a.IndexOf('\'', s);
        return e > s ? a[s..e] : null;
    }
}
