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
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetRoot();
            var textLines = tree.GetText().Lines;

            if (line >= textLines.Count) return result;

            var lineStart = textLines[line].Start;
            var charOffset = Math.Min(character, textLines[line].End - lineStart);
            var cursorPosition = lineStart + charOffset;

            var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(cursorPosition, 0));
            var anchorStatement = node.FirstAncestorOrSelf<StatementSyntax>();
            if (anchorStatement is null) return result;

            CollectLocalsInScope(anchorStatement, result);
        }
        catch
        {
            // Best-effort — never propagate to caller.
        }

        return result;
    }

    private static void CollectLocalsInScope(StatementSyntax anchorStatement, Dictionary<string, string> result)
    {
        var visited = anchorStatement;

        for (SyntaxNode? scope = anchorStatement.Parent; scope is not null; scope = scope.Parent)
        {
            // Collect parameters from any enclosing method / constructor / local function.
            CollectParametersFromScope(scope, result);

            if (!TryGetStatementContainer(scope, out var statements))
                continue;

            var anchorIndex = statements.FindIndex(s => ReferenceEquals(s, visited));
            if (anchorIndex >= 0)
            {
                for (var i = anchorIndex - 1; i >= 0; i--)
                {
                    if (statements[i] is not LocalDeclarationStatementSyntax localDecl)
                        continue;

                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        var varName = variable.Identifier.ValueText;
                        if (result.ContainsKey(varName)) continue;

                        var typeName = GetTypeStringForDeclaration(localDecl.Declaration.Type, variable);
                        if (typeName is not null)
                            result[varName] = typeName;
                    }
                }
            }

            // Move anchor up to the statement containing this scope node.
            var outerStatement = scope.Parent?.FirstAncestorOrSelf<StatementSyntax>();
            if (outerStatement is not null)
                visited = outerStatement;
        }
    }

    private static void CollectParametersFromScope(SyntaxNode scope, Dictionary<string, string> result)
    {
        ParameterListSyntax? paramList = scope switch
        {
            MethodDeclarationSyntax m => m.ParameterList,
            ConstructorDeclarationSyntax c => c.ParameterList,
            LocalFunctionStatementSyntax lf => lf.ParameterList,
            _ => null,
        };

        if (paramList is null) return;

        // Collect open generic type parameter names so we can skip method parameters whose
        // declared types reference them. For example, in GetResults<TResult>(...,
        // Expression<Func<Entity, TResult>> selector), the string "TResult" cannot be
        // resolved inside the eval script and causes a compile error. Excluding the parameter
        // from LocalVariableTypes lets the existing Expression<Func<...>> heuristic in
        // BuildStubDeclaration generate a correct "_ => default!" stub instead.
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

            // Skip parameters whose declared type contains an open generic type parameter name.
            if (openTypeParams is not null &&
                openTypeParams.Any(tp => typeName.Contains(tp, StringComparison.Ordinal)))
                continue;

            result[paramName] = typeName;
        }
    }

    private static string? GetTypeStringForDeclaration(TypeSyntax typeSyntax, VariableDeclaratorSyntax variable)
    {
        // Explicit type declaration: int x = ..., Guid id = ..., List<string> names = ...
        if (typeSyntax is not IdentifierNameSyntax { IsVar: true })
            return typeSyntax.ToString();

        // var x = ... — try to infer from initializer expression.
        return variable.Initializer?.Value is { } initializer
            ? TryInferTypeFromInitializer(initializer)
            : null;
    }

    private static string? TryInferTypeFromInitializer(ExpressionSyntax initializer) =>
        initializer switch
        {
            // new TypeName(...) or new TypeName<T>(...)
            ObjectCreationExpressionSyntax creation => creation.Type.ToString(),

            // (TypeName)expr
            CastExpressionSyntax cast => cast.Type.ToString(),

            // default(TypeName)
            DefaultExpressionSyntax { Type: { } t } => t.ToString(),

            // new TypeName[] { ... }
            ArrayCreationExpressionSyntax array => array.Type.ToString(),

            // new[] { ... } — element type unknown without semantic model
            ImplicitArrayCreationExpressionSyntax => null,

            // new() — target type unknown without semantic model
            ImplicitObjectCreationExpressionSyntax => null,

            // 5, 5L, 5.0f, "str", true, false, ...
            LiteralExpressionSyntax literal => literal.Kind() switch
            {
                SyntaxKind.NumericLiteralExpression => InferNumericLiteralType(literal.Token),
                SyntaxKind.StringLiteralExpression => "string",
                SyntaxKind.CharacterLiteralExpression => "char",
                SyntaxKind.TrueLiteralExpression => "bool",
                SyntaxKind.FalseLiteralExpression => "bool",
                _ => null,
            },

            // TypeName.Property — heuristic: PascalCase receiver is likely a type
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }
                when IsProbablyTypeName(receiver.Identifier.ValueText)
                => receiver.Identifier.ValueText,

            // TypeName.Method(...)
            InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver2 }
            } when IsProbablyTypeName(receiver2.Identifier.ValueText)
                => receiver2.Identifier.ValueText,

            _ => null,
        };

    private static string? InferNumericLiteralType(SyntaxToken token)
    {
        var text = token.Text;
        if (text.EndsWith("L", StringComparison.OrdinalIgnoreCase)) return "long";
        if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase)) return "decimal";
        if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase)) return "float";
        if (text.EndsWith("d", StringComparison.OrdinalIgnoreCase)) return "double";
        if (text.Contains('.')) return "double";
        return "int";
    }

    private static bool IsProbablyTypeName(string identifier) =>
        !string.IsNullOrEmpty(identifier) && char.IsUpper(identifier[0]);
}
