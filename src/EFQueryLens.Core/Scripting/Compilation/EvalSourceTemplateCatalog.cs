namespace EFQueryLens.Core.Scripting.Compilation;

internal static class EvalSourceTemplateCatalog
{
    private static readonly Lazy<string> SCapturedTypes = new(() => LoadTemplate("CapturedTypes.cs.tmpl"));
    private static readonly Lazy<string> SOfflineDbConnection = new(() => LoadTemplate("OfflineDbConnection.cs.tmpl"));
    private static readonly Lazy<string> SFakeDbDataReader = new(() => LoadTemplate("FakeDbDataReader.cs.tmpl"));
    private static readonly Lazy<string> SSqlCaptureScope = new(() => LoadTemplate("SqlCaptureScope.cs.tmpl"));
    private static readonly Lazy<string> SOfflineCapture = new(() => LoadTemplate("OfflineCapture.cs.tmpl"));
    private static readonly Lazy<string> SRunner = new(() => LoadTemplate("Runner.cs.tmpl"));
    private static readonly Lazy<string> SRunnerAsync = new(() => LoadTemplate("RunnerAsync.cs.tmpl"));

    internal static string CapturedTypes => SCapturedTypes.Value;
    internal static string OfflineDbConnection => SOfflineDbConnection.Value;
    internal static string FakeDbDataReader => SFakeDbDataReader.Value;
    internal static string SqlCaptureScope => SSqlCaptureScope.Value;
    internal static string OfflineCapture => SOfflineCapture.Value;
    internal static string Runner => SRunner.Value;
    internal static string RunnerAsync => SRunnerAsync.Value;

    internal static string Render(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var rendered = template;
        foreach (var token in tokens)
        {
            rendered = rendered.Replace(token.Key, token.Value, StringComparison.Ordinal);
        }

        return rendered;
    }

    private static string LoadTemplate(string fileName)
    {
        var assembly = typeof(Evaluation.QueryEvaluator).Assembly;
        var suffix = $".Scripting.Compilation.Templates.{fileName}";

        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                $"Could not locate embedded template '{fileName}'. Ensure it is included as EmbeddedResource.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open embedded template stream '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
