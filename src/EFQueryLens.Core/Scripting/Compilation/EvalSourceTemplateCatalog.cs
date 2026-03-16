using System.Reflection;

namespace EFQueryLens.Core.Scripting.Compilation;

internal static class EvalSourceTemplateCatalog
{
    private static readonly Lazy<string> s_capturedTypes = new(() => LoadTemplate("CapturedTypes.cs.tmpl"));
    private static readonly Lazy<string> s_offlineDbConnection = new(() => LoadTemplate("OfflineDbConnection.cs.tmpl"));
    private static readonly Lazy<string> s_fakeDbDataReader = new(() => LoadTemplate("FakeDbDataReader.cs.tmpl"));
    private static readonly Lazy<string> s_sqlCaptureScope = new(() => LoadTemplate("SqlCaptureScope.cs.tmpl"));
    private static readonly Lazy<string> s_offlineCapture = new(() => LoadTemplate("OfflineCapture.cs.tmpl"));
    private static readonly Lazy<string> s_runner = new(() => LoadTemplate("Runner.cs.tmpl"));

    internal static string CapturedTypes => s_capturedTypes.Value;
    internal static string OfflineDbConnection => s_offlineDbConnection.Value;
    internal static string FakeDbDataReader => s_fakeDbDataReader.Value;
    internal static string SqlCaptureScope => s_sqlCaptureScope.Value;
    internal static string OfflineCapture => s_offlineCapture.Value;
    internal static string Runner => s_runner.Value;

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
        var assembly = typeof(QueryEvaluator).Assembly;
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
