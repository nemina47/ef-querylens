using System.Reflection;
using System.Text.RegularExpressions;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    /// <summary>
    /// Returns syntactically-determined type name strings for local variables visible at
    /// the cursor position. Only yields entries where the type can be determined without
    /// a semantic model — explicit declarations are always included; <c>var</c> declarations
    /// are included only when the type is inferable from the initializer expression.
    /// </summary>
    internal static Dictionary<string, string> ExtractLocalVariableTypesAtPosition(
        string sourceText, int line, int character)
    {
        var hints = ExtractLocalSymbolHintsAtPosition(sourceText, line, character, targetAssemblyPath: null);
        return hints
            .GroupBy(h => h.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().TypeName, StringComparer.Ordinal);
    }

    internal static Dictionary<string, string> ExtractLocalVariableTypesAtPosition(
        string sourceText, int line, int character, string? targetAssemblyPath)
    {
        var hints = ExtractLocalSymbolHintsAtPosition(sourceText, line, character, targetAssemblyPath);
        return hints
            .GroupBy(h => h.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().TypeName, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns rich symbol hints for variables visible at cursor position.
    /// Includes method/local-function/lambda parameters and local declarations.
    /// </summary>
    internal static IReadOnlyList<LocalSymbolHint> ExtractLocalSymbolHintsAtPosition(
        string sourceText, int line, int character)
        => ExtractLocalSymbolHintsAtPosition(sourceText, line, character, targetAssemblyPath: null);

    internal static IReadOnlyList<LocalSymbolHint> ExtractLocalSymbolHintsAtPosition(
        string sourceText, int line, int character, string? targetAssemblyPath)
    {
        var result = new Dictionary<string, LocalSymbolHint>(StringComparer.Ordinal);
        try
        {
            SyntaxTree tree;
            SyntaxNode root;
            SemanticModel? semanticModel = null;
            if (TryCreateSemanticModel(sourceText, targetAssemblyPath, out tree, out root, out var model))
            {
                semanticModel = model;
            }
            else
            {
                tree = CSharpSyntaxTree.ParseText(sourceText);
                root = tree.GetRoot();
            }

            var textLines = tree.GetText().Lines;

            if (line >= textLines.Count) return [];

            var lineStart = textLines[line].Start;
            var charOffset = Math.Min(character, textLines[line].End - lineStart);
            var cursorPosition = lineStart + charOffset;

            var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(cursorPosition, 0));
            var anchorStatement = node.FirstAncestorOrSelf<StatementSyntax>();
            if (anchorStatement is null) return [];

            CollectLocalsInScope(anchorStatement, result, semanticModel);
        }
        catch
        {
            // Best-effort — never propagate to caller.
        }

        return result.Values.ToArray();
    }

    internal static IReadOnlyList<LocalSymbolGraphEntry> ExtractLocalSymbolGraphAtPosition(
        string sourceText,
        int line,
        int character,
        string? targetAssemblyPath)
    {
        var hints = ExtractLocalSymbolHintsAtPosition(sourceText, line, character, targetAssemblyPath);
        return hints
            .GroupBy(h => h.Name, StringComparer.Ordinal)
            .Select(g => g.OrderBy(h => h.DeclarationOrder).First())
            .OrderBy(h => h.DeclarationOrder)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .Select(h => new LocalSymbolGraphEntry
            {
                Name = h.Name,
                TypeName = h.TypeName,
                Kind = h.Kind,
                InitializerExpression = h.InitializerExpression,
                DeclarationOrder = h.DeclarationOrder,
                Dependencies = h.Dependencies,
                Scope = h.Scope,
            })
            .ToArray();
    }

    internal static IReadOnlyList<LocalSymbolGraphEntry> ExtractFreeVariableSymbolGraph(
        string expression,
        string contextVariableName,
        string primarySourceText,
        int primaryLine,
        int primaryCharacter,
        string? targetAssemblyPath,
        string? secondarySourceText = null,
        int? secondaryLine = null,
        int? secondaryCharacter = null,
        string? dbContextTypeName = null,
        Action<string>? debugLog = null)
        => ExtractFreeVariableSymbolGraph(
            expression,
            contextVariableName,
            primarySourceText,
            primaryLine,
            primaryCharacter,
            targetAssemblyPath,
            out _,
            secondarySourceText,
            secondaryLine,
            secondaryCharacter,
            dbContextTypeName,
            debugLog);

    internal static IReadOnlyList<LocalSymbolGraphEntry> ExtractFreeVariableSymbolGraph(
        string expression,
        string contextVariableName,
        string primarySourceText,
        int primaryLine,
        int primaryCharacter,
        string? targetAssemblyPath,
        out string rewrittenExpression,
        string? secondarySourceText = null,
        int? secondaryLine = null,
        int? secondaryCharacter = null,
        string? dbContextTypeName = null,
        Action<string>? debugLog = null)
    {
        rewrittenExpression = expression;
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        try
        {
            var parsedExpression = SyntaxFactory.ParseExpression(expression);

            var primaryContext = TryBuildScopeContext(
                primarySourceText,
                primaryLine,
                primaryCharacter,
                targetAssemblyPath);
            debugLog?.Invoke($"primary-scope={(primaryContext is null ? "missing" : "ok")} line={primaryLine} char={primaryCharacter}");

            ScopeResolutionContext? secondaryContext = null;
            if (!string.IsNullOrWhiteSpace(secondarySourceText)
                && secondaryLine is not null
                && secondaryCharacter is not null)
            {
                secondaryContext = TryBuildScopeContext(
                    secondarySourceText!,
                    secondaryLine.Value,
                    secondaryCharacter.Value,
                    targetAssemblyPath);
                debugLog?.Invoke($"secondary-scope={(secondaryContext is null ? "missing" : "ok")} line={secondaryLine.Value} char={secondaryCharacter.Value}");
            }

            var inferredLambdaMemberTypes = BuildLambdaParameterMemberTypeMap(
                parsedExpression,
                contextVariableName,
                targetAssemblyPath,
                dbContextTypeName,
                debugLog);

            parsedExpression = RewriteReceiverMemberAccessCaptures(
                parsedExpression,
                contextVariableName,
                primaryContext,
                secondaryContext,
                out var memberCaptureEntries,
                debugLog,
                inferredLambdaMemberTypes);
            rewrittenExpression = parsedExpression.WithoutTrivia().NormalizeWhitespace().ToString();
            debugLog?.Invoke($"rewrite-result changed={(!string.Equals(rewrittenExpression, expression, StringComparison.Ordinal))} captureCount={memberCaptureEntries.Count}");

            var freeVariableNames = CollectFreeVariableNames(parsedExpression, contextVariableName);
            debugLog?.Invoke($"free-vars count={freeVariableNames.Count} names={string.Join(",", freeVariableNames)}");
            if (freeVariableNames.Count == 0 && memberCaptureEntries.Count == 0)
                return [];

            var resolved = new Dictionary<string, LocalSymbolGraphEntry>(StringComparer.Ordinal);
            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in freeVariableNames)
            {
                TryPopulateSymbolGraphEntry(
                    name,
                    contextVariableName,
                    primaryContext,
                    secondaryContext,
                    resolved,
                    unresolved,
                    visiting);
            }
            if (unresolved.Count > 0)
            {
                debugLog?.Invoke($"unresolved-vars count={unresolved.Count} names={string.Join(",", unresolved.OrderBy(x => x, StringComparer.Ordinal))}");
            }

            foreach (var memberCaptureEntry in memberCaptureEntries)
            {
                resolved[memberCaptureEntry.Name] = memberCaptureEntry;
            }

            foreach (var entry in resolved.Values.OrderBy(s => s.DeclarationOrder).ThenBy(s => s.Name, StringComparer.Ordinal))
            {
                debugLog?.Invoke(
                    $"entry name={entry.Name} type={entry.TypeName} policy={entry.ReplayPolicy} deps={string.Join(",", entry.Dependencies)} kind={entry.Kind}");
            }

            return resolved.Values
                .OrderBy(s => s.DeclarationOrder)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            rewrittenExpression = expression;
            return [];
        }
    }

    internal static IReadOnlyList<MemberTypeHint> BuildMemberTypeHints(
        string expression,
        IEnumerable<LocalSymbolHint> localSymbolHints)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var receiverTypes = localSymbolHints
            .Where(h => !string.IsNullOrWhiteSpace(h.Name) && !string.IsNullOrWhiteSpace(h.TypeName))
            .GroupBy(h => h.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().TypeName, StringComparer.Ordinal);

        if (receiverTypes.Count == 0)
            return [];

        var map = new Dictionary<(string Receiver, string Member), string>();
        foreach (var kv in receiverTypes)
        {
            if (!TryGetNullableUnderlyingTypeName(kv.Value, out var underlying))
                continue;

            map[(kv.Key, "HasValue")] = "bool";
            map[(kv.Key, "Value")] = underlying!;
        }

        if (map.Count == 0)
            return [];

        var hints = new List<MemberTypeHint>();
        foreach (var ((receiver, member), typeName) in map
                     .OrderBy(x => x.Key.Receiver, StringComparer.Ordinal)
                     .ThenBy(x => x.Key.Member, StringComparer.Ordinal))
        {
            if (!expression.Contains($"{receiver}.{member}", StringComparison.Ordinal))
                continue;

            hints.Add(new MemberTypeHint
            {
                ReceiverName = receiver,
                MemberName = member,
                TypeName = typeName,
            });
        }

        return hints;
    }

    private static bool TryGetNullableUnderlyingTypeName(string typeName, out string? underlying)
    {
        underlying = null;
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var trimmed = typeName.Trim();
        if (trimmed.EndsWith("?", StringComparison.Ordinal) && trimmed.Length > 1)
        {
            underlying = trimmed[..^1];
            return true;
        }

        const string nullablePrefix = "System.Nullable<";
        if (trimmed.StartsWith(nullablePrefix, StringComparison.Ordinal)
            && trimmed.EndsWith(">", StringComparison.Ordinal)
            && trimmed.Length > nullablePrefix.Length + 1)
        {
            underlying = trimmed[nullablePrefix.Length..^1];
            return true;
        }

        if (trimmed.StartsWith("Nullable<", StringComparison.Ordinal)
            && trimmed.EndsWith(">", StringComparison.Ordinal)
            && trimmed.Length > "Nullable<".Length + 1)
        {
            underlying = trimmed["Nullable<".Length..^1];
            return true;
        }

        return false;
    }

    private static void CollectLocalsInScope(
        StatementSyntax anchorStatement,
        Dictionary<string, LocalSymbolHint> result,
        SemanticModel? semanticModel)
    {
        var declarationOrder = 0;
        var visited = anchorStatement;
        var scopeId = anchorStatement.FirstAncestorOrSelf<MemberDeclarationSyntax>()?.ToString();
        var scopeChain = new List<SyntaxNode>();

        for (SyntaxNode? scope = anchorStatement.Parent; scope is not null; scope = scope.Parent)
        {
            scopeChain.Add(scope);
        }

        // Collect parameter symbols first (outer-to-inner) so dependent locals
        // (for example page/pageSize initialized from request.*) always sort after
        // the parameters they depend on.
        for (var i = scopeChain.Count - 1; i >= 0; i--)
        {
            CollectParametersFromScope(scopeChain[i], result, ref declarationOrder, scopeId);
        }

        for (var scopeIndex = 0; scopeIndex < scopeChain.Count; scopeIndex++)
        {
            var scope = scopeChain[scopeIndex];
            if (!TryGetStatementContainer(scope, out var statements))
                continue;

            var anchorIndex = statements.FindIndex(s => ReferenceEquals(s, visited));
            if (anchorIndex >= 0)
            {
                for (var i = 0; i < anchorIndex; i++)
                {
                    if (statements[i] is not LocalDeclarationStatementSyntax localDecl)
                        continue;

                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        var varName = variable.Identifier.ValueText;
                        var typeName = GetTypeStringForDeclaration(localDecl.Declaration.Type, variable, semanticModel);
                        if (typeName is null)
                        {
                            continue;
                        }

                        var initializerExpression = variable.Initializer?.Value is { } init
                            ? NormalizeInitializerExpression(init)
                            : null;

                        result[varName] = new LocalSymbolHint
                        {
                            Name = varName,
                            TypeName = typeName,
                            Kind = "local",
                            InitializerExpression = initializerExpression,
                            DeclarationOrder = declarationOrder++,
                            Dependencies = ExtractInitializerDependencies(variable, semanticModel),
                            Scope = scopeId,
                        };
                    }
                }
            }

            // Move anchor up to the statement containing this scope node.
            var outerStatement = scope.Parent?.FirstAncestorOrSelf<StatementSyntax>();
            if (outerStatement is not null)
                visited = outerStatement;
        }
    }

    private static void CollectParametersFromScope(
        SyntaxNode scope,
        Dictionary<string, LocalSymbolHint> result,
        ref int declarationOrder,
        string? scopeId)
    {
        ParameterListSyntax? paramList = scope switch
        {
            MethodDeclarationSyntax m => m.ParameterList,
            ConstructorDeclarationSyntax c => c.ParameterList,
            LocalFunctionStatementSyntax lf => lf.ParameterList,
            ParenthesizedLambdaExpressionSyntax l => l.ParameterList,
            _ => null,
        };

        if (scope is SimpleLambdaExpressionSyntax simpleLambda)
        {
            var param = simpleLambda.Parameter;
            var paramName = param.Identifier.ValueText;
            var typeName = param.Type?.ToString();
            if (!string.IsNullOrWhiteSpace(paramName)
                && !result.ContainsKey(paramName)
                && !string.IsNullOrWhiteSpace(typeName))
            {
                result[paramName] = new LocalSymbolHint
                {
                    Name = paramName,
                    TypeName = typeName!,
                    Kind = "lambda-parameter",
                    DeclarationOrder = declarationOrder++,
                    Scope = scopeId,
                };
            }
        }

        if (paramList is null) return;

        // Collect open generic type parameter names so we can normalize parameter types that
        // reference them. For example, in GetResults<TResult>(...,
        // Expression<Func<Entity, TResult>> selector), "TResult" cannot be resolved in the
        // daemon eval script. We replace open generic parameter names with "object" so Core
        // can still build a deterministic typed stub without semantic-model binding.
        var openTypeParams = scope switch
        {
            MethodDeclarationSyntax { TypeParameterList: { } tpl } =>
                tpl.Parameters.Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal),
            LocalFunctionStatementSyntax { TypeParameterList: { } tpl } =>
                tpl.Parameters.Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal),
            _ => null,
        };

        foreach (var param in paramList.Parameters)
        {
            var paramName = param.Identifier.ValueText;
            if (result.ContainsKey(paramName) || param.Type is null) continue;

            var typeName = param.Type.ToString();
            if (string.IsNullOrWhiteSpace(typeName)) continue;

            if (openTypeParams is not null && openTypeParams.Count > 0)
                typeName = ReplaceOpenGenericTypeParameters(typeName, openTypeParams);

            result[paramName] = new LocalSymbolHint
            {
                Name = paramName,
                TypeName = typeName,
                Kind = scope is ParenthesizedLambdaExpressionSyntax ? "lambda-parameter" : "parameter",
                DeclarationOrder = declarationOrder++,
                Scope = scopeId,
            };
        }
    }

    private static string? GetTypeStringForDeclaration(TypeSyntax typeSyntax, VariableDeclaratorSyntax variable)
        => GetTypeStringForDeclaration(typeSyntax, variable, semanticModel: null);

    private static string? GetTypeStringForDeclaration(
        TypeSyntax typeSyntax,
        VariableDeclaratorSyntax variable,
        SemanticModel? semanticModel)
    {
        // Explicit type declaration: int x = ..., Guid id = ..., List<string> names = ...
        if (typeSyntax is not IdentifierNameSyntax { IsVar: true })
            return typeSyntax.ToString();

        // var x = ... — semantic model is the single source of truth.
        return TryInferVarTypeFromSemanticModel(variable, semanticModel);
    }

    private static string? TryInferVarTypeFromSemanticModel(
        VariableDeclaratorSyntax variable,
        SemanticModel? semanticModel)
    {
        if (semanticModel is null)
            return null;

        var symbol = semanticModel.GetDeclaredSymbol(variable) as ILocalSymbol;
        var type = symbol?.Type;
        if (type is null || type.TypeKind == TypeKind.Error)
            return null;

        return type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private static string ReplaceOpenGenericTypeParameters(string typeName, IReadOnlyCollection<string> openTypeParams)
    {
        var rewritten = typeName;
        foreach (var typeParam in openTypeParams)
        {
            if (string.IsNullOrWhiteSpace(typeParam))
                continue;

            rewritten = Regex.Replace(
                rewritten,
                $@"\b{Regex.Escape(typeParam)}\b",
                "object",
                RegexOptions.CultureInvariant);
        }

        return rewritten;
    }

    private static string NormalizeInitializerExpression(ExpressionSyntax initializer) =>
        initializer.WithoutTrivia().NormalizeWhitespace().ToString();

    private static IReadOnlyList<string> ExtractInitializerDependencies(
        VariableDeclaratorSyntax variable,
        SemanticModel? semanticModel)
    {
        if (semanticModel is null || variable.Initializer?.Value is not { } init)
            return [];

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in init.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(id).Symbol;
            if (symbol is not (ILocalSymbol or IParameterSymbol))
            {
                continue;
            }

            // Ignore lambda/query-range locals declared inside the initializer itself.
            // Example: `query = _dbContext.Orders.Where(o => o.IsNotDeleted)` should not
            // record `o` as a dependency of `query`.
            if (IsDeclaredWithinInitializer(symbol, init))
            {
                continue;
            }

            var name = id.Identifier.ValueText;
            if (!string.Equals(name, variable.Identifier.ValueText, StringComparison.Ordinal))
                names.Add(name);
        }

        return names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static bool IsDeclaredWithinInitializer(ISymbol symbol, ExpressionSyntax initializer)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            if (initializer.Span.Contains(syntax.Span))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ScopeResolutionContext(
        string SourceText,
        SemanticModel SemanticModel,
        StatementSyntax AnchorStatement,
        string? ScopeId,
        IReadOnlyDictionary<(string Receiver, string Member), string> MemberTypeMap,
        IReadOnlyDictionary<string, string> MemberNameTypeMap);

    private static ScopeResolutionContext? TryBuildScopeContext(
        string sourceText,
        int line,
        int character,
        string? targetAssemblyPath)
    {
        if (!TryCreateSemanticModel(sourceText, targetAssemblyPath, out var tree, out var root, out var semanticModel))
            return null;

        var textLines = tree.GetText().Lines;
        if (line < 0 || line >= textLines.Count)
            return null;

        var lineStart = textLines[line].Start;
        var charOffset = Math.Min(Math.Max(character, 0), textLines[line].End - lineStart);
        var cursorPosition = lineStart + charOffset;
        var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(cursorPosition, 0));
        var anchorStatement = node.FirstAncestorOrSelf<StatementSyntax>();
        if (anchorStatement is null)
            return null;

        var (memberTypeMap, memberNameTypeMap) = BuildMemberTypeMapsFromAnchorStatement(anchorStatement, semanticModel);

        return new ScopeResolutionContext(
            sourceText,
            semanticModel,
            anchorStatement,
            anchorStatement.FirstAncestorOrSelf<MemberDeclarationSyntax>()?.ToString(),
            memberTypeMap,
            memberNameTypeMap);
    }

    private static ExpressionSyntax RewriteReceiverMemberAccessCaptures(
        ExpressionSyntax expression,
        string contextVariableName,
        ScopeResolutionContext? primaryContext,
        ScopeResolutionContext? secondaryContext,
        out IReadOnlyList<LocalSymbolGraphEntry> captures,
        Action<string>? debugLog = null,
        IReadOnlyDictionary<(string Receiver, string Member), string>? inferredLambdaMemberTypes = null)
    {
        captures = [];
        var usedNames = expression
            .DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Select(id => id.Identifier.ValueText)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        var declaredInsideExpression = new HashSet<string>(StringComparer.Ordinal)
        {
            contextVariableName,
        };
        foreach (var parameter in expression.DescendantNodesAndSelf().OfType<ParameterSyntax>())
        {
            var name = parameter.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declaredInsideExpression.Add(name);
        }

        foreach (var fromClause in expression.DescendantNodesAndSelf().OfType<FromClauseSyntax>())
        {
            var name = fromClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declaredInsideExpression.Add(name);
        }

        foreach (var joinClause in expression.DescendantNodesAndSelf().OfType<JoinClauseSyntax>())
        {
            var name = joinClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declaredInsideExpression.Add(name);
        }

        foreach (var letClause in expression.DescendantNodesAndSelf().OfType<LetClauseSyntax>())
        {
            var name = letClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declaredInsideExpression.Add(name);
        }

        foreach (var continuation in expression.DescendantNodesAndSelf().OfType<QueryContinuationSyntax>())
        {
            var name = continuation.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declaredInsideExpression.Add(name);
        }

        var captureMap = new Dictionary<(string Receiver, string Member), LocalSymbolGraphEntry>();
        var nextDeclarationOrder = -1_000_000;
        var collisionIndex = 0;

        string AllocateCaptureName(string receiver, string member)
        {
            var baseName = $"__qlm_{receiver}_{member}";
            var candidate = baseName;
            while (usedNames.Contains(candidate))
            {
                candidate = $"{baseName}_{collisionIndex++}";
            }

            usedNames.Add(candidate);
            return candidate;
        }

        var rewriter = new ReceiverMemberCaptureRewriter(node =>
        {
            if (node.Parent is InvocationExpressionSyntax invocation
                && ReferenceEquals(invocation.Expression, node))
            {
                debugLog?.Invoke($"capture-skip reason=invocation-target access={node}");
                return null;
            }

            if (node.Expression is not IdentifierNameSyntax receiverIdentifier)
            {
                debugLog?.Invoke($"capture-skip reason=receiver-not-identifier access={node}");
                return null;
            }

            var receiverName = receiverIdentifier.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(receiverName))
            {
                debugLog?.Invoke($"capture-skip reason=empty-receiver access={node}");
                return null;
            }
            if (declaredInsideExpression.Contains(receiverName))
            {
                debugLog?.Invoke($"capture-skip reason=declared-inside-expression receiver={receiverName} access={node}");
                return null;
            }
            if (IsStaticTypeOrNamespaceReceiver(receiverName, primaryContext, secondaryContext))
            {
                debugLog?.Invoke($"capture-skip reason=receiver-is-type-or-namespace receiver={receiverName} access={node}");
                return null;
            }
            if (receiverName.StartsWith("__qlm_", StringComparison.Ordinal))
            {
                debugLog?.Invoke($"capture-skip reason=synthetic-receiver receiver={receiverName} access={node}");
                return null;
            }

            var memberName = node.Name.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(memberName))
            {
                debugLog?.Invoke($"capture-skip reason=empty-member receiver={receiverName} access={node}");
                return null;
            }

            string? memberTypeName;
            var resolvedFromComparison = TryResolveMemberTypeFromComparisonPartner(
                node,
                primaryContext,
                secondaryContext,
                inferredLambdaMemberTypes,
                out memberTypeName);

            if (resolvedFromComparison)
            {
                debugLog?.Invoke(
                    $"capture-type-from-comparison receiver={receiverName} member={memberName} inferredType={memberTypeName}");
            }

            if (!resolvedFromComparison
                && !TryResolveReceiverMemberTypeName(
                    receiverName,
                    memberName,
                    primaryContext,
                    secondaryContext,
                    out memberTypeName))
            {
                if (!TryResolveMemberTypeFromDeclarationChain(
                        receiverName,
                        memberName,
                        primaryContext,
                        secondaryContext,
                        out memberTypeName))
                {
                    string? primaryByNameType = null;
                    string? secondaryByNameType = null;
                    var primaryHasByName = primaryContext?.MemberNameTypeMap.TryGetValue(memberName, out primaryByNameType!) == true;
                    var secondaryHasByName = secondaryContext?.MemberNameTypeMap.TryGetValue(memberName, out secondaryByNameType!) == true;
                    var primaryKeys = primaryContext?.MemberTypeMap.Count ?? 0;
                    var secondaryKeys = secondaryContext?.MemberTypeMap.Count ?? 0;
                    debugLog?.Invoke(
                        $"capture-unresolved-detail receiver={receiverName} member={memberName} " +
                        $"primaryMapKeys={primaryKeys} secondaryMapKeys={secondaryKeys} " +
                        $"primaryByName={(primaryHasByName ? primaryByNameType : "none")} " +
                        $"secondaryByName={(secondaryHasByName ? secondaryByNameType : "none")}");
                    debugLog?.Invoke($"capture-skip reason=member-type-unresolved receiver={receiverName} member={memberName} access={node}");
                    return null;
                }

            }

            if (string.IsNullOrWhiteSpace(memberTypeName))
            {
                debugLog?.Invoke($"capture-skip reason=member-type-empty receiver={receiverName} member={memberName} access={node}");
                return null;
            }

            var key = (receiverName, memberName);
            if (!captureMap.TryGetValue(key, out var entry))
            {
                entry = new LocalSymbolGraphEntry
                {
                    Name = AllocateCaptureName(receiverName, memberName),
                    TypeName = memberTypeName!,
                    Kind = "member-capture",
                    InitializerExpression = null,
                    DeclarationOrder = nextDeclarationOrder++,
                    Dependencies = [],
                    Scope = primaryContext?.ScopeId ?? secondaryContext?.ScopeId,
                    ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                };
                captureMap[key] = entry;
                debugLog?.Invoke($"capture-add receiver={receiverName} member={memberName} capture={entry.Name} type={entry.TypeName}");
            }
            else
            {
                debugLog?.Invoke($"capture-reuse receiver={receiverName} member={memberName} capture={entry.Name}");
            }

            return entry.Name;
        });

        var rewritten = rewriter.Visit(expression) as ExpressionSyntax ?? expression;
        captures = captureMap.Values
            .OrderBy(c => c.DeclarationOrder)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToArray();
        return rewritten;
    }

    private static bool TryResolveMemberTypeFromDeclarationChain(
        string receiverName,
        string memberName,
        ScopeResolutionContext? primaryContext,
        ScopeResolutionContext? secondaryContext,
        out string? memberTypeName)
    {
        memberTypeName = null;
        return TryResolveMemberTypeFromDeclarationChain(receiverName, memberName, primaryContext, out memberTypeName)
               || TryResolveMemberTypeFromDeclarationChain(receiverName, memberName, secondaryContext, out memberTypeName);
    }

    private static bool TryResolveMemberTypeFromDeclarationChain(
        string receiverName,
        string memberName,
        ScopeResolutionContext? context,
        out string? memberTypeName)
    {
        memberTypeName = null;
        if (context is null)
            return false;

        if (!TryFindLocalDeclarationBeforeAnchor(context, receiverName, context.AnchorStatement, out var receiverDeclaration))
            return false;

        var receiverInitializer = receiverDeclaration.Initializer?.Value;
        if (receiverInitializer is null)
            return false;

        // Common pattern:
        //   var item = items.FirstOrDefault();
        //   item.Member == ...
        if (receiverInitializer is InvocationExpressionSyntax receiverInvocation
            && receiverInvocation.Expression is MemberAccessExpressionSyntax receiverMemberAccess
            && string.Equals(receiverMemberAccess.Name.Identifier.ValueText, "FirstOrDefault", StringComparison.Ordinal)
            && receiverMemberAccess.Expression is IdentifierNameSyntax sourceIdentifier)
        {
            var sourceName = sourceIdentifier.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(sourceName))
                return false;

            if (!TryFindLocalDeclarationBeforeAnchor(
                    context,
                    sourceName,
                    receiverDeclaration.FirstAncestorOrSelf<StatementSyntax>() ?? context.AnchorStatement,
                    out var sourceDeclaration))
            {
                return false;
            }

            var sourceInitializer = sourceDeclaration.Initializer?.Value;
            if (sourceInitializer is null)
                return false;

            foreach (var anonymous in sourceInitializer.DescendantNodesAndSelf().OfType<AnonymousObjectCreationExpressionSyntax>())
            {
                foreach (var init in anonymous.Initializers)
                {
                    var anonMemberName = init.NameEquals?.Name.Identifier.ValueText
                        ?? (init.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText;
                    if (!string.Equals(anonMemberName, memberName, StringComparison.Ordinal))
                        continue;

                    var inferredType = context.SemanticModel.GetTypeInfo(init.Expression).Type;
                    memberTypeName = ToDeterministicTypeName(inferredType);
                    if (!string.IsNullOrWhiteSpace(memberTypeName))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool IsStaticTypeOrNamespaceReceiver(
        string receiverName,
        ScopeResolutionContext? primaryContext,
        ScopeResolutionContext? secondaryContext)
    {
        // A real in-scope local/parameter should win over type/namespace symbols.
        if (TryResolveAccessibleReceiverType(receiverName, primaryContext, secondaryContext, out _))
            return false;

        return IsTypeOrNamespaceInScope(receiverName, primaryContext)
               || IsTypeOrNamespaceInScope(receiverName, secondaryContext);
    }

    private static bool IsTypeOrNamespaceInScope(string receiverName, ScopeResolutionContext? context)
    {
        if (context is null || string.IsNullOrWhiteSpace(receiverName))
            return false;

        var lookupPositions = new[]
        {
            context.AnchorStatement.SpanStart,
            context.AnchorStatement.Span.End,
            context.AnchorStatement.FullSpan.Start,
            context.AnchorStatement.FullSpan.End,
        };

        foreach (var position in lookupPositions.Distinct())
        {
            var symbols = context.SemanticModel.LookupSymbols(position, name: receiverName);
            foreach (var symbol in symbols)
            {
                if (symbol is INamedTypeSymbol or INamespaceSymbol)
                    return true;

                if (symbol is IAliasSymbol alias
                    && alias.Target is INamedTypeSymbol or INamespaceSymbol)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindLocalDeclarationBeforeAnchor(
        ScopeResolutionContext context,
        string variableName,
        StatementSyntax anchorStatement,
        out VariableDeclaratorSyntax variable)
    {
        variable = null!;
        var visited = anchorStatement;
        for (SyntaxNode? scope = anchorStatement.Parent; scope is not null; scope = scope.Parent)
        {
            if (!TryGetStatementContainer(scope, out var statements))
                continue;

            var anchorIndex = FindAnchorIndexForVisitedStatement(statements, visited);
            if (anchorIndex < 0)
                continue;

            for (var i = anchorIndex - 1; i >= 0; i--)
            {
                if (statements[i] is not LocalDeclarationStatementSyntax localDeclaration)
                    continue;

                foreach (var candidate in localDeclaration.Declaration.Variables)
                {
                    if (string.Equals(candidate.Identifier.ValueText, variableName, StringComparison.Ordinal))
                    {
                        variable = candidate;
                        return true;
                    }
                }
            }

            if (scope is StatementSyntax statementScope)
                visited = statementScope;
        }

        return false;
    }

    private static bool TryResolveMemberTypeFromComparisonPartner(
        MemberAccessExpressionSyntax memberAccess,
        ScopeResolutionContext? primaryContext,
        ScopeResolutionContext? secondaryContext,
        IReadOnlyDictionary<(string Receiver, string Member), string>? inferredLambdaMemberTypes,
        out string? memberTypeName)
    {
        memberTypeName = null;

        var comparison = memberAccess.Ancestors().OfType<BinaryExpressionSyntax>()
            .FirstOrDefault(static b =>
                b.IsKind(SyntaxKind.EqualsExpression)
                || b.IsKind(SyntaxKind.NotEqualsExpression)
                || b.IsKind(SyntaxKind.GreaterThanExpression)
                || b.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                || b.IsKind(SyntaxKind.LessThanExpression)
                || b.IsKind(SyntaxKind.LessThanOrEqualExpression));
        if (comparison is null)
            return false;

        ExpressionSyntax counterpart;
        if (comparison.Left.Span.Contains(memberAccess.Span))
        {
            counterpart = comparison.Right;
        }
        else if (comparison.Right.Span.Contains(memberAccess.Span))
        {
            counterpart = comparison.Left;
        }
        else
        {
            return false;
        }

        if (counterpart is ParenthesizedExpressionSyntax parenthesized)
            counterpart = parenthesized.Expression;

        if (counterpart is MemberAccessExpressionSyntax counterpartMember
            && counterpartMember.Expression is IdentifierNameSyntax counterpartReceiverIdentifier)
        {
            var counterpartReceiver = counterpartReceiverIdentifier.Identifier.ValueText;
            var counterpartMemberName = counterpartMember.Name.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(counterpartReceiver) || string.IsNullOrWhiteSpace(counterpartMemberName))
                return false;

            if (inferredLambdaMemberTypes is not null
                && inferredLambdaMemberTypes.TryGetValue((counterpartReceiver, counterpartMemberName), out var inferredTypeName)
                && !string.IsNullOrWhiteSpace(inferredTypeName))
            {
                memberTypeName = inferredTypeName;
                return true;
            }

            return TryResolveReceiverMemberTypeName(
                counterpartReceiver,
                counterpartMemberName,
                primaryContext,
                secondaryContext,
                out memberTypeName);
        }

        if (counterpart is IdentifierNameSyntax counterpartIdentifier)
        {
            if (TryResolveAccessibleReceiverType(
                    counterpartIdentifier.Identifier.ValueText,
                    primaryContext,
                    secondaryContext,
                    out var counterpartType))
            {
                memberTypeName = ToDeterministicTypeName(counterpartType);
                return !string.IsNullOrWhiteSpace(memberTypeName);
            }
        }

        if (TryResolveTypeFromExpression(counterpart, out var inferredCounterpartType))
        {
            memberTypeName = inferredCounterpartType;
            return true;
        }

        return false;
    }

    private static bool TryResolveTypeFromExpression(ExpressionSyntax expression, out string? typeName)
    {
        typeName = null;
        expression = expression is ParenthesizedExpressionSyntax parenthesized
            ? parenthesized.Expression
            : expression;

        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                typeName = literal.Kind() switch
                {
                    SyntaxKind.StringLiteralExpression => "global::System.String",
                    SyntaxKind.CharacterLiteralExpression => "global::System.Char",
                    SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression => "global::System.Boolean",
                    SyntaxKind.NumericLiteralExpression => TryResolveNumericLiteralTypeName(literal.Token),
                    _ => null,
                };
                return !string.IsNullOrWhiteSpace(typeName);

            case ObjectCreationExpressionSyntax objectCreation:
                typeName = objectCreation.Type.ToString();
                return !string.IsNullOrWhiteSpace(typeName);

            case DefaultExpressionSyntax defaultExpression:
                typeName = defaultExpression.Type.ToString();
                return !string.IsNullOrWhiteSpace(typeName);

            case CastExpressionSyntax castExpression:
                typeName = castExpression.Type.ToString();
                return !string.IsNullOrWhiteSpace(typeName);
        }

        return false;
    }

    private static string? TryResolveNumericLiteralTypeName(SyntaxToken token)
    {
        return token.Value switch
        {
            byte => "global::System.Byte",
            sbyte => "global::System.SByte",
            short => "global::System.Int16",
            ushort => "global::System.UInt16",
            int => "global::System.Int32",
            uint => "global::System.UInt32",
            long => "global::System.Int64",
            ulong => "global::System.UInt64",
            float => "global::System.Single",
            double => "global::System.Double",
            decimal => "global::System.Decimal",
            _ => null,
        };
    }

    private static bool TryResolveReceiverMemberTypeName(
        string receiverName,
        string memberName,
        ScopeResolutionContext? primaryContext,
        ScopeResolutionContext? secondaryContext,
        out string? memberTypeName)
    {
        memberTypeName = null;

        if (TryResolveReceiverMemberTypeName(receiverName, memberName, primaryContext, out memberTypeName))
            return true;

        return TryResolveReceiverMemberTypeName(receiverName, memberName, secondaryContext, out memberTypeName);
    }

    private static bool TryResolveReceiverMemberTypeName(
        string receiverName,
        string memberName,
        ScopeResolutionContext? context,
        out string? memberTypeName)
    {
        memberTypeName = null;
        if (context is null)
            return false;

        if (context.MemberTypeMap.TryGetValue((receiverName, memberName), out var mappedTypeName)
            && !string.IsNullOrWhiteSpace(mappedTypeName))
        {
            memberTypeName = mappedTypeName;
            return true;
        }

        if (context.MemberNameTypeMap.TryGetValue(memberName, out var byMemberNameType)
            && !string.IsNullOrWhiteSpace(byMemberNameType))
        {
            memberTypeName = byMemberNameType;
            return true;
        }

        if (!TryResolveAccessibleReceiverType(receiverName, context, out var receiverType))
            return false;

        ITypeSymbol? memberType = receiverType
            .GetMembers(memberName)
            .OfType<IPropertySymbol>()
            .Where(m => !m.IsStatic)
            .Select(m => m.Type)
            .FirstOrDefault();

        memberType ??= receiverType
            .GetMembers(memberName)
            .OfType<IFieldSymbol>()
            .Where(m => !m.IsStatic)
            .Select(m => m.Type)
            .FirstOrDefault();

        memberTypeName = ToDeterministicTypeName(memberType);
        return !string.IsNullOrWhiteSpace(memberTypeName);
    }

    private static (IReadOnlyDictionary<(string Receiver, string Member), string> ByReceiverMember, IReadOnlyDictionary<string, string> ByMemberName) BuildMemberTypeMapsFromAnchorStatement(
        StatementSyntax anchorStatement,
        SemanticModel semanticModel)
    {
        var byReceiverMember = new Dictionary<(string Receiver, string Member), string>();
        var byMemberNameCandidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var memberAccess in anchorStatement.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var memberName = memberAccess.Name.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(memberName))
                continue;

            // Prefer Roslyn's resolved member-access type directly; this works for many
            // anonymous/lambda flows where receiver symbol lookup can be fragile.
            ITypeSymbol? memberType = semanticModel.GetTypeInfo(memberAccess).Type;

            // Fallback to receiver-member lookup when direct type info is unavailable.
            if (memberType is null || memberType.TypeKind == TypeKind.Error)
            {
                if (memberAccess.Expression is IdentifierNameSyntax receiverIdentifier)
                {
                    var receiverSymbol = semanticModel.GetSymbolInfo(receiverIdentifier).Symbol;
                    ITypeSymbol? receiverType = receiverSymbol switch
                    {
                        ILocalSymbol local => local.Type,
                        IParameterSymbol parameter => parameter.Type,
                        _ => null,
                    };

                    if (receiverType is not null && receiverType.TypeKind != TypeKind.Error)
                    {
                        memberType = receiverType
                            .GetMembers(memberName)
                            .OfType<IPropertySymbol>()
                            .Where(m => !m.IsStatic)
                            .Select(m => m.Type)
                            .FirstOrDefault();

                        memberType ??= receiverType
                            .GetMembers(memberName)
                            .OfType<IFieldSymbol>()
                            .Where(m => !m.IsStatic)
                            .Select(m => m.Type)
                            .FirstOrDefault();
                    }
                }
            }

            var memberTypeName = ToDeterministicTypeName(memberType);
            if (string.IsNullOrWhiteSpace(memberTypeName))
                continue;

            if (memberAccess.Expression is IdentifierNameSyntax receiverIdentifierForMap)
            {
                byReceiverMember[(receiverIdentifierForMap.Identifier.ValueText, memberName)] = memberTypeName!;
            }

            if (!byMemberNameCandidates.TryGetValue(memberName, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                byMemberNameCandidates[memberName] = set;
            }

            set.Add(memberTypeName!);
        }

        var byMemberName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (memberName, typeCandidates) in byMemberNameCandidates)
        {
            if (typeCandidates.Count == 1)
            {
                byMemberName[memberName] = typeCandidates.First();
            }
        }

        return (byReceiverMember, byMemberName);
    }

    private static bool TryResolveAccessibleReceiverType(
        string receiverName,
        ScopeResolutionContext? primaryContext,
        ScopeResolutionContext? secondaryContext,
        out ITypeSymbol receiverType)
    {
        receiverType = null!;
        return TryResolveAccessibleReceiverType(receiverName, primaryContext, out receiverType)
               || TryResolveAccessibleReceiverType(receiverName, secondaryContext, out receiverType);
    }

    private static bool TryResolveAccessibleReceiverType(
        string receiverName,
        ScopeResolutionContext? context,
        out ITypeSymbol receiverType)
    {
        receiverType = null!;
        if (context is null)
            return false;

        var lookupPositions = new[]
        {
            context.AnchorStatement.SpanStart,
            context.AnchorStatement.Span.End,
            context.AnchorStatement.FullSpan.Start,
            context.AnchorStatement.FullSpan.End,
        };

        foreach (var position in lookupPositions.Distinct())
        {
            var symbolsAtPosition = context.SemanticModel
                .LookupSymbols(position)
                .Where(s => string.Equals(s.Name, receiverName, StringComparison.Ordinal));

            foreach (var symbol in symbolsAtPosition)
            {
                switch (symbol)
                {
                    case ILocalSymbol local when local.Type is not null && local.Type.TypeKind != TypeKind.Error:
                        receiverType = local.Type;
                        return true;
                    case IParameterSymbol parameter when parameter.Type is not null && parameter.Type.TypeKind != TypeKind.Error:
                        receiverType = parameter.Type;
                        return true;
                    case IFieldSymbol field when field.Type is not null && field.Type.TypeKind != TypeKind.Error:
                        receiverType = field.Type;
                        return true;
                    case IPropertySymbol property when property.Type is not null && property.Type.TypeKind != TypeKind.Error:
                        receiverType = property.Type;
                        return true;
                }
            }
        }

        // Fallback: resolve from preceding local declarations in the current scope chain.
        var visited = context.AnchorStatement;
        for (SyntaxNode? scope = context.AnchorStatement.Parent; scope is not null; scope = scope.Parent)
        {
            if (!TryGetStatementContainer(scope, out var statements))
                continue;

            var anchorIndex = FindAnchorIndexForVisitedStatement(statements, visited);
            if (anchorIndex < 0)
                continue;

            for (var i = anchorIndex - 1; i >= 0; i--)
            {
                if (statements[i] is not LocalDeclarationStatementSyntax localDeclaration)
                    continue;

                foreach (var variable in localDeclaration.Declaration.Variables)
                {
                    if (!string.Equals(variable.Identifier.ValueText, receiverName, StringComparison.Ordinal))
                        continue;

                    if (context.SemanticModel.GetDeclaredSymbol(variable) is ILocalSymbol local
                        && local.Type is not null
                        && local.Type.TypeKind != TypeKind.Error)
                    {
                        receiverType = local.Type;
                        return true;
                    }
                }
            }

            if (scope is StatementSyntax statementScope)
                visited = statementScope;
        }

        return false;
    }

    private sealed class ReceiverMemberCaptureRewriter : CSharpSyntaxRewriter
    {
        private readonly Func<MemberAccessExpressionSyntax, string?> _resolver;

        public ReceiverMemberCaptureRewriter(Func<MemberAccessExpressionSyntax, string?> resolver)
        {
            _resolver = resolver;
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
            var replacement = _resolver(visited);
            if (string.IsNullOrWhiteSpace(replacement))
                return visited;

            return SyntaxFactory.IdentifierName(replacement).WithTriviaFrom(visited);
        }
    }

    private static IReadOnlyList<string> CollectFreeVariableNames(
        ExpressionSyntax expression,
        string contextVariableName)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal)
        {
            contextVariableName,
        };

        foreach (var parameter in expression.DescendantNodesAndSelf().OfType<ParameterSyntax>())
        {
            var name = parameter.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        foreach (var fromClause in expression.DescendantNodesAndSelf().OfType<FromClauseSyntax>())
        {
            var name = fromClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        foreach (var joinClause in expression.DescendantNodesAndSelf().OfType<JoinClauseSyntax>())
        {
            var name = joinClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        foreach (var letClause in expression.DescendantNodesAndSelf().OfType<LetClauseSyntax>())
        {
            var name = letClause.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        foreach (var continuation in expression.DescendantNodesAndSelf().OfType<QueryContinuationSyntax>())
        {
            var name = continuation.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        foreach (var local in expression.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
        {
            var name = local.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(name))
                declared.Add(name);
        }

        var free = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (id.Parent is NameEqualsSyntax nameEquals
                && ReferenceEquals(nameEquals.Name, id))
            {
                continue;
            }

            if (id.Parent is AssignmentExpressionSyntax assignment
                && ReferenceEquals(assignment.Left, id)
                && assignment.Parent is InitializerExpressionSyntax)
            {
                continue;
            }

            if (id.Parent is MemberAccessExpressionSyntax memberAccess
                && ReferenceEquals(memberAccess.Name, id))
            {
                continue;
            }

            if (id.Parent is InvocationExpressionSyntax invocation
                && ReferenceEquals(invocation.Expression, id))
            {
                continue;
            }

            if (id.Parent is TypeArgumentListSyntax or QualifiedNameSyntax or NameColonSyntax)
            {
                continue;
            }

            var name = id.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (declared.Contains(name))
                continue;
            if (char.IsUpper(name[0]))
                continue;

            free.Add(name);
        }

        return free.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static bool TryPopulateSymbolGraphEntry(
        string name,
        string contextVariableName,
        ScopeResolutionContext? primaryContext,
        ScopeResolutionContext? secondaryContext,
        IDictionary<string, LocalSymbolGraphEntry> resolved,
        ISet<string> unresolved,
        ISet<string> visiting)
    {
        if (resolved.ContainsKey(name))
            return true;
        if (visiting.Contains(name))
            return false;

        visiting.Add(name);
        try
        {
            if (!TryResolveAccessibleSymbolEntry(name, contextVariableName, primaryContext, out var entry)
                && !TryResolveAccessibleSymbolEntry(name, contextVariableName, secondaryContext, out entry))
            {
                unresolved.Add(name);
                return false;
            }

            entry = DowngradeReplayInitializerWhenAnonymousDependenciesPresent(
                entry,
                contextVariableName,
                primaryContext,
                secondaryContext);

            foreach (var dependency in entry.Dependencies)
            {
                TryPopulateSymbolGraphEntry(
                    dependency,
                    contextVariableName,
                    primaryContext,
                    secondaryContext,
                    resolved,
                    unresolved,
                    visiting);
            }

            resolved[name] = entry;
            return true;
        }
        finally
        {
            visiting.Remove(name);
        }
    }

    private static LocalSymbolGraphEntry DowngradeReplayInitializerWhenAnonymousDependenciesPresent(
        LocalSymbolGraphEntry entry,
        string contextVariableName,
        ScopeResolutionContext? primaryContext,
        ScopeResolutionContext? secondaryContext)
    {
        if (!string.Equals(entry.ReplayPolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal))
            return entry;

        if (entry.Dependencies.Count == 0)
            return entry;

        foreach (var dependency in entry.Dependencies)
        {
            if (!TryResolveAccessibleSymbolEntry(
                    dependency,
                    contextVariableName,
                    primaryContext,
                    out var depEntry)
                && !TryResolveAccessibleSymbolEntry(
                    dependency,
                    contextVariableName,
                    secondaryContext,
                    out depEntry))
            {
                continue;
            }

            if (IsReplayUnsafeDependency(depEntry))
            {
                return entry with
                {
                    ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                    InitializerExpression = null,
                    Dependencies = [],
                };
            }
        }

        return entry;
    }

    private static bool IsReplayUnsafeDependency(LocalSymbolGraphEntry dependency)
    {
        if (IsAnonymousTypeName(dependency.TypeName))
            return true;

        if (!string.Equals(dependency.ReplayPolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(dependency.InitializerExpression))
            return false;

        try
        {
            var initializer = SyntaxFactory.ParseExpression(dependency.InitializerExpression);
            if (IsQueryableInitializerExpression(initializer))
                return true;
        }
        catch
        {
            // Best effort only.
        }

        return false;
    }

    private static bool IsAnonymousTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        return typeName.Contains("<anonymous type:", StringComparison.Ordinal)
               || typeName.Contains("AnonymousType", StringComparison.Ordinal)
               || typeName.Contains("<>", StringComparison.Ordinal);
    }

    private static bool TryResolveAccessibleSymbolEntry(
        string name,
        string contextVariableName,
        ScopeResolutionContext? context,
        out LocalSymbolGraphEntry entry)
    {
        entry = null!;
        if (context is null)
            return false;

        if (TryCreateEntryFromAccessibleSymbolLookup(name, context.SemanticModel, context.AnchorStatement, context.ScopeId, out entry))
            return true;

        var visited = context.AnchorStatement;
        for (SyntaxNode? scope = context.AnchorStatement.Parent; scope is not null; scope = scope.Parent)
        {
            if (TryGetStatementContainer(scope, out var statements))
            {
                var anchorIndex = FindAnchorIndexForVisitedStatement(statements, visited);
                if (anchorIndex >= 0)
                {
                    for (var i = anchorIndex - 1; i >= 0; i--)
                    {
                        if (TryCreateEntryFromStatement(
                                statements[i],
                                name,
                                contextVariableName,
                                context.SemanticModel,
                                context.ScopeId,
                                out entry))
                        {
                            return true;
                        }
                    }
                }
            }

            if (TryCreateEntryFromScopeParameter(scope, name, context.SemanticModel, context.ScopeId, out entry))
                return true;

            if (scope is StatementSyntax statementScope)
                visited = statementScope;
        }

        return false;
    }

    private static int FindAnchorIndexForVisitedStatement(
        IReadOnlyList<StatementSyntax> statements,
        StatementSyntax visited)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            if (ReferenceEquals(statements[i], visited))
                return i;
        }

        // When the active statement is nested inside an `if`/`foreach`/`using` block,
        // find the immediate container statement in this statement list.
        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i].Span.Contains(visited.Span))
                return i;
        }

        return -1;
    }

    private static bool TryCreateEntryFromStatement(
        StatementSyntax statement,
        string name,
        string contextVariableName,
        SemanticModel semanticModel,
        string? scopeId,
        out LocalSymbolGraphEntry entry)
    {
        entry = null!;

        if (statement is LocalDeclarationStatementSyntax localDeclaration)
        {
            foreach (var variable in localDeclaration.Declaration.Variables)
            {
                if (!string.Equals(variable.Identifier.ValueText, name, StringComparison.Ordinal))
                    continue;

                var localSymbol = semanticModel.GetDeclaredSymbol(variable) as ILocalSymbol;
                var typeName = ToDeterministicTypeName(localSymbol?.Type)
                    ?? GetTypeStringForDeclaration(localDeclaration.Declaration.Type, variable, semanticModel);
                var initializer = variable.Initializer?.Value;
                var replayPolicy = initializer is null
                    ? LocalSymbolReplayPolicies.UsePlaceholder
                    : DetermineReplayPolicy(initializer);
                if (typeName is null && initializer is not null)
                {
                    var initializerType = semanticModel.GetTypeInfo(initializer).Type
                                          ?? semanticModel.GetTypeInfo(initializer).ConvertedType;
                    typeName = ToDeterministicTypeName(initializerType);
                }
                if (typeName is null)
                {
                    if (initializer is null)
                        return false;

                    replayPolicy = LocalSymbolReplayPolicies.ReplayInitializer;
                    typeName = "?";
                }
                var initializerExpression = initializer is null ? null : NormalizeInitializerExpression(initializer);
                IReadOnlyList<string> dependencies = initializer is null
                    ? []
                    : ExtractExpressionDependencies(initializer, name, contextVariableName, semanticModel);
                if (!string.Equals(replayPolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal))
                {
                    initializerExpression = null;
                    dependencies = [];
                }

                entry = new LocalSymbolGraphEntry
                {
                    Name = name,
                    TypeName = typeName,
                    Kind = "local",
                    InitializerExpression = initializerExpression,
                    DeclarationOrder = variable.SpanStart,
                    Dependencies = dependencies,
                    Scope = scopeId,
                    ReplayPolicy = replayPolicy,
                };
                return true;
            }
        }

        if (statement is ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax assignment
            }
            && assignment.Left is IdentifierNameSyntax leftIdentifier
            && string.Equals(leftIdentifier.Identifier.ValueText, name, StringComparison.Ordinal))
        {
            var type = semanticModel.GetTypeInfo(assignment.Right).Type;
            var typeName = ToDeterministicTypeName(type)
                ?? ToDeterministicTypeName(semanticModel.GetTypeInfo(assignment.Right).ConvertedType);
            var replayPolicy = DetermineReplayPolicy(assignment.Right);
            var initializerExpression = NormalizeInitializerExpression(assignment.Right);
            IReadOnlyList<string> dependencies = ExtractExpressionDependencies(
                assignment.Right,
                name,
                contextVariableName,
                semanticModel);

            if (string.IsNullOrWhiteSpace(typeName))
            {
                typeName = TryResolveAssignmentTargetTypeName(leftIdentifier, semanticModel);
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    replayPolicy = LocalSymbolReplayPolicies.UsePlaceholder;
                    initializerExpression = null;
                    dependencies = [];
                }
                else
                {
                    replayPolicy = LocalSymbolReplayPolicies.ReplayInitializer;
                    typeName = "?";
                }
            }
            if (!string.Equals(replayPolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal))
            {
                initializerExpression = null;
                dependencies = [];
            }

            entry = new LocalSymbolGraphEntry
            {
                Name = name,
                TypeName = typeName!,
                Kind = "local",
                InitializerExpression = initializerExpression,
                DeclarationOrder = assignment.SpanStart,
                Dependencies = dependencies,
                Scope = scopeId,
                ReplayPolicy = replayPolicy,
            };
            return true;
        }

        return false;
    }

    private static string? TryResolveAssignmentTargetTypeName(
        IdentifierNameSyntax leftIdentifier,
        SemanticModel semanticModel)
    {
        var leftSymbol = semanticModel.GetSymbolInfo(leftIdentifier).Symbol;
        return leftSymbol switch
        {
            ILocalSymbol local => ToDeterministicTypeName(local.Type),
            IParameterSymbol parameter => ToDeterministicTypeName(parameter.Type),
            IFieldSymbol field => ToDeterministicTypeName(field.Type),
            IPropertySymbol property => ToDeterministicTypeName(property.Type),
            _ => null,
        };
    }

    private static bool TryCreateEntryFromScopeParameter(
        SyntaxNode scope,
        string name,
        SemanticModel semanticModel,
        string? scopeId,
        out LocalSymbolGraphEntry entry)
    {
        entry = null!;

        ParameterSyntax? parameterSyntax = scope switch
        {
            MethodDeclarationSyntax method => method.ParameterList.Parameters
                .FirstOrDefault(p => string.Equals(p.Identifier.ValueText, name, StringComparison.Ordinal)),
            ConstructorDeclarationSyntax ctor => ctor.ParameterList.Parameters
                .FirstOrDefault(p => string.Equals(p.Identifier.ValueText, name, StringComparison.Ordinal)),
            ClassDeclarationSyntax classDecl => classDecl.ParameterList?.Parameters
                .FirstOrDefault(p => string.Equals(p.Identifier.ValueText, name, StringComparison.Ordinal)),
            StructDeclarationSyntax structDecl => structDecl.ParameterList?.Parameters
                .FirstOrDefault(p => string.Equals(p.Identifier.ValueText, name, StringComparison.Ordinal)),
            LocalFunctionStatementSyntax localFunction => localFunction.ParameterList.Parameters
                .FirstOrDefault(p => string.Equals(p.Identifier.ValueText, name, StringComparison.Ordinal)),
            ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters
                .FirstOrDefault(p => string.Equals(p.Identifier.ValueText, name, StringComparison.Ordinal)),
            SimpleLambdaExpressionSyntax simpleLambda
                when string.Equals(simpleLambda.Parameter.Identifier.ValueText, name, StringComparison.Ordinal)
                => simpleLambda.Parameter,
            _ => null,
        };

        if (parameterSyntax is null)
            return false;

        var symbol = semanticModel.GetDeclaredSymbol(parameterSyntax) as IParameterSymbol;
        var typeName = ToDeterministicTypeName(symbol?.Type)
            ?? parameterSyntax.Type?.ToString();
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var openTypeParams = scope switch
        {
            MethodDeclarationSyntax { TypeParameterList: { } tpl } =>
                tpl.Parameters.Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal),
            LocalFunctionStatementSyntax { TypeParameterList: { } tpl } =>
                tpl.Parameters.Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal),
            _ => null,
        };
        if (openTypeParams is not null && openTypeParams.Count > 0)
            typeName = ReplaceOpenGenericTypeParameters(typeName!, openTypeParams);

        entry = new LocalSymbolGraphEntry
        {
            Name = name,
            TypeName = typeName!,
            Kind = scope is SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax
                ? "lambda-parameter"
                : "parameter",
            DeclarationOrder = parameterSyntax.SpanStart,
            Scope = scopeId,
            ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };
        return true;
    }

    private static bool TryCreateEntryFromAccessibleSymbolLookup(
        string name,
        SemanticModel semanticModel,
        StatementSyntax anchorStatement,
        string? scopeId,
        out LocalSymbolGraphEntry entry)
    {
        entry = null!;

        var lookupPositions = new[]
        {
            anchorStatement.SpanStart,
            anchorStatement.Span.End,
            anchorStatement.FullSpan.Start,
            anchorStatement.FullSpan.End,
        };

        foreach (var position in lookupPositions.Distinct())
        {
            var symbol = semanticModel.LookupSymbols(position, name: name)
                .FirstOrDefault(static s => s is IFieldSymbol or IPropertySymbol);
            if (symbol is null)
                continue;

            var typeName = symbol switch
            {
                IFieldSymbol field => ToDeterministicTypeName(field.Type),
                IPropertySymbol property => ToDeterministicTypeName(property.Type),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(typeName))
                continue;

            entry = new LocalSymbolGraphEntry
            {
                Name = name,
                TypeName = typeName!,
                Kind = symbol is IFieldSymbol ? "field" : "property",
                DeclarationOrder = symbol.Locations.FirstOrDefault()?.SourceSpan.Start ?? anchorStatement.SpanStart,
                Scope = scopeId,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            };
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractExpressionDependencies(
        ExpressionSyntax expression,
        string selfName,
        string contextVariableName,
        SemanticModel semanticModel)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(id).Symbol;
            if (symbol is not (ILocalSymbol or IParameterSymbol))
                continue;

            if (IsDeclaredWithinInitializer(symbol, expression))
                continue;

            var name = id.Identifier.ValueText;
            if (!string.Equals(name, selfName, StringComparison.Ordinal)
                && !string.Equals(name, contextVariableName, StringComparison.Ordinal))
                names.Add(name);
        }

        return names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static string DetermineReplayPolicy(ExpressionSyntax initializer)
    {
        if (initializer.DescendantNodesAndSelf().OfType<ConditionalAccessExpressionSyntax>().Any())
            return LocalSymbolReplayPolicies.UsePlaceholder;

        if (IsQueryableInitializerExpression(initializer))
            return LocalSymbolReplayPolicies.ReplayInitializer;

        foreach (var memberAccess in initializer.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            if (string.Equals(memberAccess.Name.Identifier.ValueText, "Value", StringComparison.Ordinal))
                return LocalSymbolReplayPolicies.UsePlaceholder;
        }

        foreach (var invocation in initializer.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax)
                return LocalSymbolReplayPolicies.UsePlaceholder;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return LocalSymbolReplayPolicies.UsePlaceholder;

            if (memberAccess.Expression is IdentifierNameSyntax identifier
                && identifier.Identifier.ValueText.Length > 0
                && char.IsUpper(identifier.Identifier.ValueText[0]))
            {
                continue;
            }

            return LocalSymbolReplayPolicies.UsePlaceholder;
        }

        return LocalSymbolReplayPolicies.ReplayInitializer;
    }

    private static bool IsQueryableInitializerExpression(ExpressionSyntax initializer)
    {
        var outermostInvocations = initializer.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Select(GetOutermostInvocationChain)
            .GroupBy(i => (i.SpanStart, i.Span.Length))
            .Select(g => g.First());

        foreach (var invocation in outermostInvocations)
        {
            var methodNames = GetInvocationChainMethodNames(invocation).ToArray();
            if (methodNames.Length == 0)
                continue;

            if (methodNames.Any(name => QueryChainMethods.Contains(name))
                || methodNames.Any(name => TerminalMethods.Contains(name)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ToDeterministicTypeName(ITypeSymbol? type)
    {
        if (type is null || type.TypeKind == TypeKind.Error)
            return null;

        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

}
