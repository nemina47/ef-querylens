// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;

internal sealed partial class QueryLensLanguageClient
{
    private static object BuildInitializationOptions()
    {
        return new
        {
            queryLens = new
            {
                debugEnabled = true,
                enableLspHover = false,
                hoverProgressNotify = false,
                hoverProgressDelayMs = 350,
                hoverCacheTtlMs = 15000,
                hoverCancelGraceMs = 1200,
                markdownQueueAdaptiveWaitMs = 200,
                structuredQueueAdaptiveWaitMs = 200,
                warmupSuccessTtlMs = 60_000,
                warmupFailureCooldownMs = 5_000,
            }
        };
    }
}
