using System.Diagnostics;
using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Lsp.Services;

internal sealed partial class HoverPreviewService
{
    private sealed record HoverCanonicalComputationResult(
        bool Success,
        string Message,
        QueryTranslationStatus Status,
        double AvgTranslationMs,
        double LastTranslationMs,
        string? SourceExpression,
        string? ExecutedExpression,
        int SourceLine,
        TranslationMetadata? Metadata,
        IReadOnlyList<QuerySqlCommand> Commands,
        IReadOnlyList<QueryWarning> Warnings);

    private static string? ResolveExecutedExpression(
        string? callSiteExpression,
        string expression,
        string? translationExecutedExpression)
    {
        if (!string.IsNullOrWhiteSpace(translationExecutedExpression))
        {
            return translationExecutedExpression;
        }

        if (string.IsNullOrWhiteSpace(callSiteExpression))
        {
            return null;
        }

        return string.Equals(callSiteExpression, expression, StringComparison.Ordinal)
            ? null
            : expression;
    }

    private static bool IsValidExtractionOrigin(ExtractionOriginSnapshot? origin)
    {
        if (origin is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(origin.FilePath))
        {
            return false;
        }

        return origin.Line >= 0
               && origin.Character >= 0
               && origin.EndLine >= 0
               && origin.EndCharacter >= 0;
    }

    private static string BuildStatusText(QueryTranslationStatus status) => status switch
    {
        QueryTranslationStatus.Starting => "EF QueryLens - warming up",
        QueryTranslationStatus.InQueue => "EF QueryLens - in queue",
        QueryTranslationStatus.DaemonUnavailable => "EF QueryLens - error",
        _ => "EF QueryLens - in queue",
    };

    private static bool ContainsToQueryStringCall(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        try
        {
            var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(expression);
            return parsed
                .DescendantNodesAndSelf()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                .Any(invocation => invocation.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member
                    && string.Equals(member.Name.Identifier.ValueText, "ToQueryString", StringComparison.Ordinal));
        }
        catch
        {
            return expression.Contains(".ToQueryString(", StringComparison.Ordinal);
        }
    }

    private async Task<HoverCanonicalComputationResult> BuildCanonicalAsync(
        string filePath,
        string sourceText,
        int line,
        int character,
        CancellationToken cancellationToken,
        Action<string> log)
    {
        static HoverCanonicalComputationResult Fail(
            string message,
            int sourceLine,
            QueryTranslationStatus status = QueryTranslationStatus.Ready) =>
            new(
                Success: false,
                Message: message,
                Status: status,
                AvgTranslationMs: 0,
                LastTranslationMs: 0,
                SourceExpression: null,
                ExecutedExpression: null,
                SourceLine: sourceLine,
                Metadata: null,
                Commands: [],
                Warnings: []);

        var sourceLine = line + 1;

        var targetAssembly = AssemblyResolver.TryGetTargetAssembly(filePath);
        var sourceIndex = ProjectSourceHelper.GetProjectIndex(filePath);
        var extraction = LspSyntaxHelper.TryExtractLinqExpressionDetailed(
            sourceText,
            filePath,
            line,
            character,
            sourceIndex,
            targetAssembly);
        var expression = extraction?.Expression;
        var contextVariableName = extraction?.ContextVariableName;
        var callSiteExpression = extraction?.CallSiteExpression;
        var expressionPreview = string.IsNullOrWhiteSpace(expression)
            ? string.Empty
            : expression.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
        if (expressionPreview.Length > 240)
        {
            expressionPreview = expressionPreview[..240] + "...";
        }

        log($"extract-linq line={line} char={character} found={!string.IsNullOrWhiteSpace(expression)} ctx={contextVariableName} exprLen={expression?.Length ?? 0} preview={expressionPreview} indexedFiles={sourceIndex.FileCount}");
        var origin = extraction?.Origin;
        var originValid = IsValidExtractionOrigin(origin);
        if (origin is not null)
        {
            log($"extract-origin path={origin.FilePath} line={origin.Line} char={origin.Character} scope={origin.Scope} originValid={originValid}");
        }

        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(contextVariableName))
        {
            return Fail("Could not extract a LINQ query expression at the current caret location.", sourceLine);
        }

        // Statements that project SQL strings via ToQueryString() are not a single query root.
        // Return a deterministic guidance message instead of flowing into capture/runtime logic.
        if (ContainsToQueryStringCall(expression))
        {
            log($"preview-blocked line={line} char={character} reason=toquerystring-wrapper");
            return Fail("This statement renders SQL text via ToQueryString(). Hover the underlying LINQ query expression to preview SQL.", sourceLine);
        }

        if (string.IsNullOrWhiteSpace(targetAssembly)
            || targetAssembly.StartsWith("DEBUG_FAIL", StringComparison.Ordinal)
            || !File.Exists(targetAssembly))
        {
            return Fail("Could not locate compiled target assembly for this file. Build the project and try again.", sourceLine);
        }

        var warmupState = _warmupHandler?.EnsureWarmupStartedForPreview(
            filePath,
            sourceText,
            line,
            character);
        if (warmupState is { ShouldDeferPreview: true })
        {
            log($"preview-starting line={line} char={character} reason={warmupState.Reason} assembly={warmupState.AssemblyPath}");
            return new HoverCanonicalComputationResult(
                Success: true,
                Message: warmupState.Message,
                Status: QueryTranslationStatus.Starting,
                AvgTranslationMs: 0,
                LastTranslationMs: 0,
                SourceExpression: callSiteExpression ?? expression,
                ExecutedExpression: null,
                SourceLine: sourceLine,
                Metadata: null,
                Commands: [],
                Warnings: []);
        }

        var isHelperExtraction = string.Equals(origin?.Scope, "helper-method", StringComparison.Ordinal);
        if (isHelperExtraction)
        {
            log($"semantic-gate-bypass line={line} char={character} reason=helper-method");
        }
        else if (!LspSyntaxHelper.PassesSemanticLinqGate(sourceText, line, character, targetAssembly, out var gateReason))
        {
            if (LspSyntaxHelper.PassesExtractedExpressionLinqShapeGate(expression, contextVariableName))
            {
                log($"semantic-gate-fallback line={line} char={character} reason={gateReason ?? "unknown"}");
            }
            else
            {
                log($"semantic-gate-block line={line} char={character} reason={gateReason ?? "unknown"}");
                return Fail("No queryable LINQ expression was resolved at this cursor location.", sourceLine);
            }
        }

        var usingContext = LspSyntaxHelper.ExtractUsingContext(sourceText);
        var additionalImports = BuildAdditionalImports(usingContext.Imports);
        var factoryDbContextCandidates = AssemblyResolver.TryExtractDbContextTypeNamesFromFactories(targetAssembly);
        var dbContextResolution = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
            sourceText,
            contextVariableName,
            factoryDbContextCandidates);
        var dbContextTypeName = LspSyntaxHelper.GetPreferredDbContextTypeName(dbContextResolution);

        if (!originValid)
        {
            log($"preview-blocked line={line} char={character} reason=missing-origin");
            return Fail("No preview: missing extraction origin.", sourceLine);
        }

        var extractionLine = origin!.Line;
        var extractionChar = origin.Character;
        var extractionFilePath = origin.FilePath;
        var extractionSourceText = sourceText;
        if (!string.IsNullOrWhiteSpace(extractionFilePath)
            && !string.Equals(
                Path.GetFullPath(extractionFilePath),
                Path.GetFullPath(filePath),
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                extractionSourceText = File.ReadAllText(extractionFilePath);
            }
            catch (Exception ex)
            {
                log($"preview-blocked line={line} char={character} reason=origin-read-failed path={extractionFilePath} error={ex.GetType().Name}:{ex.Message}");
                return Fail("No preview: failed to read extraction-origin source for symbol graph.", sourceLine);
            }
        }

        var helperSubstitutionApplied = string.Equals(origin.Scope, "helper-method", StringComparison.Ordinal);

        // Build v2 extraction plan (slice 1: extraction IR)
        var extractionPlanSuccess = LspSyntaxHelper.TryBuildV2ExtractionPlan(
            extractionSourceText,
            extractionFilePath ?? filePath,
            extractionLine,
            extractionChar,
            out var extractionPlan,
            out var extractionDiagnostics,
            sourceIndex: null,
            targetAssemblyPath: targetAssembly);

        if (!extractionPlanSuccess)
        {
            var firstDiagnostic = extractionDiagnostics.FirstOrDefault();
            var reason = firstDiagnostic?.Code ?? "extraction-plan-failed";
            log($"preview-info line={line} char={character} reason={reason} (falling back to capture-plan-only)");
        }

        var capturePlanSuccess = LspSyntaxHelper.TryBuildV2CapturePlan(
            expression,
            contextVariableName!,
            extractionSourceText,
            extractionLine,
            extractionChar,
            targetAssembly,
            out var capturePlan,
            secondarySourceText: helperSubstitutionApplied ? sourceText : null,
            secondaryLine: helperSubstitutionApplied ? line : null,
            secondaryCharacter: helperSubstitutionApplied ? character : null,
            dbContextTypeName: dbContextTypeName,
            debugLog: detail => log($"capture-plan {detail}"),
            factoryCandidateTypeNames: factoryDbContextCandidates);

        if (!capturePlanSuccess || capturePlan is null)
        {
            var firstDiagnostic = capturePlan?.Diagnostics.FirstOrDefault();
            var reason = firstDiagnostic is null
                ? "capture-plan-failed"
                : $"{firstDiagnostic.Code}:{firstDiagnostic.SymbolName}";
            log($"preview-blocked line={line} char={character} reason={reason}");
            return Fail(
                firstDiagnostic?.Message ?? "No preview: capture plan failed.",
                sourceLine);
        }

        var executableExpression = capturePlan.ExecutableExpression;
        var localSymbolGraph = LspSyntaxHelper.AdaptCapturePlanToLocalSymbolGraph(capturePlan);
        var unresolvedNames = FindUnresolvedSymbols(executableExpression, contextVariableName!, localSymbolGraph);
        var declaredNames = new HashSet<string>(
            localSymbolGraph.Select(s => s.Name),
            StringComparer.Ordinal);
        var unresolvedDependencies = localSymbolGraph
            .SelectMany(s => s.Dependencies.Select(d => (symbol: s.Name, dep: d)))
            .Where(p => !declaredNames.Contains(p.dep))
            .ToArray();
        var graphComplete = capturePlan.IsComplete
                            && unresolvedNames.Count == 0
                            && unresolvedDependencies.Length == 0;
        var replayInitializerCount = capturePlan.Entries.Count(s =>
            string.Equals(s.CapturePolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal));
        var placeholderCount = capturePlan.Entries.Count(s =>
            string.Equals(s.CapturePolicy, LocalSymbolReplayPolicies.UsePlaceholder, StringComparison.Ordinal));
        var rejectedCount = capturePlan.Entries.Count(s =>
            string.Equals(s.CapturePolicy, LocalSymbolReplayPolicies.Reject, StringComparison.Ordinal));
        log(
            $"extract-local-types line={line} char={character} originLine={extractionLine} originChar={extractionChar} " +
            $"count={localSymbolGraph.Count} vars={string.Join(",", localSymbolGraph.Select(s => s.Name))} " +
            $"unresolved={unresolvedNames.Count} unresolvedDeps={unresolvedDependencies.Length} graphComplete={graphComplete} " +
            $"helperSubstitution={helperSubstitutionApplied} replayInit={replayInitializerCount} placeholder={placeholderCount} rejected={rejectedCount} " +
            $"rewritten={(string.Equals(executableExpression, expression, StringComparison.Ordinal) ? "False" : "True")} " +
            $"hasMemberCapture={(executableExpression.Contains("__qlm_", StringComparison.Ordinal) ? "True" : "False")}");

        if (!graphComplete)
        {
            string reason;
            if (capturePlan.Diagnostics.Count > 0)
            {
                var top = capturePlan.Diagnostics.First();
                reason = $"capture-plan:{top.Code}:{top.SymbolName}";
            }
            else if (localSymbolGraph.Count == 0 && unresolvedNames.Count > 0)
            {
                reason = "incomplete-symbol-graph:empty";
            }
            else if (unresolvedNames.Count > 0)
            {
                reason = $"incomplete-symbol-graph:missing={string.Join(",", unresolvedNames)}";
            }
            else
            {
                var deps = string.Join(",", unresolvedDependencies.Select(p => $"{p.symbol}->{p.dep}"));
                reason = $"incomplete-symbol-graph:dependencies={deps}";
            }

            log($"preview-blocked line={line} char={character} reason={reason}");
            return Fail($"No preview: symbol graph incomplete ({reason}).", sourceLine);
        }
        try
        {
            var sw = Stopwatch.StartNew();
            log($"translate-start line={line} char={character} assembly={targetAssembly}");

            var translationRequest = new TranslationRequest
            {
                AssemblyPath = targetAssembly,
                Expression = executableExpression,
                OriginalExpression = callSiteExpression ?? expression,
                RewrittenExpression = executableExpression,
                RewriteFlags = BuildRewriteFlags(callSiteExpression, executableExpression),
                ContextVariableName = contextVariableName,
                DbContextTypeName = dbContextTypeName,
                DbContextResolution = dbContextResolution,
                AdditionalImports = additionalImports,
                UsingAliases = new Dictionary<string, string>(usingContext.Aliases, StringComparer.Ordinal),
                UsingStaticTypes = usingContext.StaticTypes.ToArray(),
                LocalSymbolGraph = localSymbolGraph,
                V2CapturePlan = capturePlan,
                V2ExtractionPlan = extractionPlanSuccess && extractionPlan is not null
                    ? new Core.Contracts.V2QueryExtractionPlanSnapshot
                    {
                        Expression = extractionPlan.Expression,
                        ContextVariableName = extractionPlan.ContextVariableName,
                        RootContextVariableName = extractionPlan.RootContextVariableName,
                        RootMemberName = extractionPlan.RootMemberName,
                        CallSiteExpression = extractionPlan.CallSiteExpression,
                        Origin = extractionPlan.Origin,
                        BoundaryKind = extractionPlan.BoundaryKind.ToString(),
                        NeedsMaterialization = extractionPlan.NeedsMaterialization,
                        AppliedHelperMethods = extractionPlan.AppliedHelperMethods,
                        Diagnostics = extractionPlan.Diagnostics.Select(d => new Core.Contracts.V2ExtractionDiagnostic { Code = d.Code, Message = d.Message }).ToList(),
                    }
                    : null,
                UseAsyncRunner = true,
                ExtractionOrigin = extraction?.Origin,
                UsingContextSnapshot = new UsingContextSnapshot
                {
                    Imports = usingContext.Imports.ToList(),
                    Aliases = new Dictionary<string, string>(usingContext.Aliases, StringComparer.Ordinal),
                    StaticTypes = usingContext.StaticTypes.ToList(),
                },
                ExpressionMetadata = new ParsedExpressionMetadata
                {
                    ExpressionType = "Invocation", // Expression type will be determined at parse time
                    SourceLine = line,
                    SourceCharacter = character,
                    Confidence = 1.0,
                },
            };

            var queued = await TranslateQueuedOrImmediateAsync(translationRequest, cancellationToken);

            if (queued.Status is not QueryTranslationStatus.Ready)
            {
                sw.Stop();
                var statusMessage = BuildStatusText(queued.Status);
                log(
                    $"queued-status line={line} char={character} " +
                    $"status={queued.Status} avgMs={queued.AverageTranslationMs:0.##} lastMs={queued.LastTranslationMs:0.##}");

                return new HoverCanonicalComputationResult(
                    Success: true,
                    Message: statusMessage,
                    Status: queued.Status,
                    AvgTranslationMs: queued.AverageTranslationMs,
                    LastTranslationMs: queued.LastTranslationMs,
                    SourceExpression: callSiteExpression ?? expression,
                    ExecutedExpression: null,
                    SourceLine: sourceLine,
                    Metadata: null,
                    Commands: [],
                    Warnings: []);
            }

            var translation = queued.Result;
            if (translation is null)
            {
                sw.Stop();
                log($"translate-missing-result line={line} char={character}");
                return Fail("Queued translation completed without a result payload.", sourceLine);
            }

            sw.Stop();
            log(
                $"translate-finished line={line} char={character} " +
                $"success={translation.Success} elapsedMs={sw.ElapsedMilliseconds} " +
                $"commands={translation.Commands.Count} sqlLen={(translation.Sql?.Length ?? 0)}");

            if (!translation.Success)
            {
                log($"translate-error line={line} char={character} message={translation.ErrorMessage}");
                if (!string.IsNullOrWhiteSpace(translation.DiagnosticDetail))
                    log($"translate-error-detail line={line} char={character} detail={translation.DiagnosticDetail}");
                return Fail(translation.ErrorMessage ?? "Translation failed.", sourceLine);
            }

            var commands = translation.Commands.Count > 0
                ? translation.Commands
                : translation.Sql is null
                    ? []
                    : [new QuerySqlCommand { Sql = translation.Sql, Parameters = translation.Parameters }];

            if (commands.Count == 0)
            {
                log($"translate-empty-commands line={line} char={character}");
                return Fail("No SQL was produced for this expression.", sourceLine);
            }

            for (var i = 0; i < commands.Count; i++)
            {
                var commandSql = commands[i].Sql ?? string.Empty;
                log(
                    $"translate-sql line={line} char={character} idx={i + 1}/{commands.Count} " +
                    $"sql={BuildSqlLogPreview(commandSql)}");
            }

            return new HoverCanonicalComputationResult(
                Success: true,
                Message: string.Empty,
                Status: QueryTranslationStatus.Ready,
                AvgTranslationMs: queued.AverageTranslationMs,
                LastTranslationMs: queued.LastTranslationMs,
                SourceExpression: callSiteExpression ?? expression,
                ExecutedExpression: ResolveExecutedExpression(
                    callSiteExpression,
                    executableExpression,
                    translation.ExecutedExpression),
                SourceLine: sourceLine,
                Metadata: translation.Metadata,
                Commands: commands,
                Warnings: translation.Warnings);
        }
        catch (Exception ex)
        {
            log($"translate-exception line={line} char={character} type={ex.GetType().Name} message={ex.Message}");
            return Fail($"{ex.GetType().Name}: {ex.Message}", sourceLine, QueryTranslationStatus.DaemonUnavailable);
        }
    }

    private static IReadOnlyList<string> FindUnresolvedSymbols(
        string expression,
        string contextVariableName,
        IReadOnlyList<LocalSymbolGraphEntry> graph)
    {
        var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(expression);
        var declared = new HashSet<string>(StringComparer.Ordinal)
        {
            contextVariableName,
            "__qlFactoryContext",
        };

        foreach (var symbol in graph)
            declared.Add(symbol.Name);

        foreach (var lambdaParam in parsed.DescendantNodesAndSelf()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax>()
                     .Select(p => p.Identifier.ValueText))
        {
            if (!string.IsNullOrWhiteSpace(lambdaParam))
                declared.Add(lambdaParam);
        }

        foreach (var fromClause in parsed.DescendantNodesAndSelf()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FromClauseSyntax>())
        {
            var name = fromClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        foreach (var joinClause in parsed.DescendantNodesAndSelf()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.JoinClauseSyntax>())
        {
            var name = joinClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        foreach (var letClause in parsed.DescendantNodesAndSelf()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LetClauseSyntax>())
        {
            var name = letClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        foreach (var continuation in parsed.DescendantNodesAndSelf()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.QueryContinuationSyntax>())
        {
            var name = continuation.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in parsed.DescendantNodesAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>())
        {
            if (id.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.NameEqualsSyntax nameEquals
                && ReferenceEquals(nameEquals.Name, id))
            {
                continue;
            }
            if (id.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax assignment
                && ReferenceEquals(assignment.Left, id)
                && assignment.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.InitializerExpressionSyntax)
            {
                continue;
            }
            if (id.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member
                && ReferenceEquals(member.Name, id))
            {
                continue;
            }
            if (id.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation
                && ReferenceEquals(invocation.Expression, id))
            {
                continue;
            }
            if (id.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.NameColonSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.TypeArgumentListSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax)
            {
                continue;
            }

            var name = id.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (char.IsUpper(name[0]))
                continue;
            if (declared.Contains(name))
                continue;

            unresolved.Add(name);
        }

        return unresolved.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    internal async Task<CombinedHoverResult> BuildCombinedAsync(
        string filePath,
        string sourceText,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        Action<string> log = message => LogDebug($"combined {message}");
        var canonical = await BuildCanonicalAsync(
            filePath,
            sourceText,
            line,
            character,
            cancellationToken,
            log);

        var markdown = FormatMarkdown(canonical, filePath, line, character);
        var structured = FormatStructured(canonical, filePath);

        if (markdown.Success && markdown.Status is QueryTranslationStatus.Ready)
        {
            LogDebug($"combined hover-ready line={line} char={character} markdownLen={markdown.Output.Length}");
        }

        return new CombinedHoverResult(markdown, structured);
    }

    private static IReadOnlyList<LocalSymbolGraphEntry> DowngradeReplayInitializersWithMissingDependencies(
        IReadOnlyList<LocalSymbolGraphEntry> graph,
        out int downgradedCount)
    {
        downgradedCount = 0;
        if (graph.Count == 0)
            return graph;

        var declaredNames = new HashSet<string>(graph.Select(s => s.Name), StringComparer.Ordinal);
        var adjusted = new List<LocalSymbolGraphEntry>(graph.Count);

        foreach (var entry in graph)
        {
            var isReplayInitializer = string.Equals(
                entry.ReplayPolicy,
                LocalSymbolReplayPolicies.ReplayInitializer,
                StringComparison.Ordinal);
            var hasConcreteType = !string.Equals(entry.TypeName, "?", StringComparison.Ordinal);
            var hasMissingDependency = entry.Dependencies.Any(dep => !declaredNames.Contains(dep));

            if (isReplayInitializer && hasConcreteType && hasMissingDependency)
            {
                adjusted.Add(entry with
                {
                    ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                    InitializerExpression = null,
                    Dependencies = [],
                });
                downgradedCount++;
                continue;
            }

            adjusted.Add(entry);
        }

        return adjusted;
    }

    private static string BuildSqlLogPreview(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        var oneLine = sql.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return oneLine.Length <= 1200
            ? oneLine
            : oneLine[..1200] + "...";
    }

    private async Task<QueuedTranslationResult> TranslateQueuedOrImmediateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _engine.TranslateAsync(request, cancellationToken);
        var lastTranslationMs = Math.Max(0, result.Metadata.TranslationTime.TotalMilliseconds);
        return new QueuedTranslationResult
        {
            Status = QueryTranslationStatus.Ready,
            AverageTranslationMs = 0,
            LastTranslationMs = lastTranslationMs,
            Result = result,
        };
    }

    private static IReadOnlyList<string> BuildAdditionalImports(IEnumerable<string> extractedImports)
    {
        var imports = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var import in extractedImports)
        {
            if (!string.IsNullOrWhiteSpace(import) && seen.Add(import))
            {
                imports.Add(import);
            }
        }

        // Hover compilation can miss implicit/global usings from the project.
        // Ensure LINQ extension methods remain in scope for common query shapes.
        if (seen.Add("System.Linq"))
        {
            imports.Add("System.Linq");
        }

        return imports;
    }

    private static IReadOnlyList<string> BuildRewriteFlags(string? callSiteExpression, string extractedExpression)
    {
        var flags = new List<string>(capacity: 4) { "lsp-extraction" };

        if (!string.IsNullOrWhiteSpace(callSiteExpression)
            && !string.Equals(callSiteExpression, extractedExpression, StringComparison.Ordinal))
        {
            flags.Add("callsite-rewritten");
        }

        if (extractedExpression.Contains("Concat(", StringComparison.Ordinal))
        {
            flags.Add("setop-checked");
        }

        if (extractedExpression.Contains("ToListAsync(", StringComparison.Ordinal)
            || extractedExpression.Contains("ToList(", StringComparison.Ordinal))
        {
            flags.Add("materialization-boundary");
        }

        return flags;
    }
}
