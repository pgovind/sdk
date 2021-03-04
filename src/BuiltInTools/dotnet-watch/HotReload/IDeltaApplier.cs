﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.EditAndContinue;

namespace Microsoft.DotNet.Watcher.Tools
{
    interface IDeltaApplier
    {
        ValueTask InitializeAsync(DotNetWatchContext context, CancellationToken cancellationToken);

        ValueTask<bool> Apply(DotNetWatchContext context, ManagedModuleUpdates2 solutionUpdate, CancellationToken cancellationToken);

        ValueTask ReportDiagnosticsAsync(DotNetWatchContext context, IEnumerable<string> diagnostics, CancellationToken cancellationToken);
    }
}
