using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFQueryLens.Lsp.Parsing;

public sealed record SourceUsingContext(
    IReadOnlyList<string> Imports,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<string> StaticTypes);

public sealed record LinqChainInfo(
    string Expression,
    string ContextVariableName,
    string DbSetMemberName,
    /// <summary>Line/character of the hover anchor (end of query) for opening doc / LSP hover.</summary>
    int Line,
    int Character,
    int EndLine,
    int EndCharacter,
    /// <summary>Line/character where the CodeLens badge is drawn (above the statement).</summary>
    int BadgeLine,
    int BadgeCharacter,
    /// <summary>Full statement span: hover doc is shown when caret is anywhere in this range.</summary>
    int StatementStartLine,
    int StatementStartCharacter,
    int StatementEndLine,
    int StatementEndCharacter);

public static partial class LspSyntaxHelper
{
}
