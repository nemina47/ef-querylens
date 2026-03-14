// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Shared;

internal static class EfQueryLensHoverMarkdownContent
{
    internal const string DocumentationMarkdown = """
                                                  # LINQ Query Reference

                                                  This panel documents QueryLens hover behavior and SQL preview usage.

                                                  ## Notes
                                                  - Hover on LINQ expressions to preview translated SQL.
                                                  - Restart daemon from the Tools menu if previews become stale.
                                                  - Use Open Logs to inspect LSP and extension diagnostics.
                                                  """;
}
