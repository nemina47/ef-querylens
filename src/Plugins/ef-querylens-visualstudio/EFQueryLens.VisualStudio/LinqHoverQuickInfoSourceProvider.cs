// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

[Export(typeof(IAsyncQuickInfoSourceProvider))]
[Name("Linq Hover QuickInfo Source")]
[Order(Before = "default")]
[ContentType("csharp")]
internal sealed class LinqHoverQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
{
    public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
    {
        return new LinqHoverQuickInfoSource(textBuffer);
    }
}

