// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.Debugger.Contracts.EditAndContinue;

namespace Microsoft.DotNet.Watcher.Tools
{
    interface IDeltaApplier
    {
        ValueTask InitializeAsync(DotNetWatchContext context, CancellationToken cancellationToken);

        ValueTask<bool> Apply(DotNetWatchContext context, Solution solution, (ManagedModuleUpdates Updates, ImmutableArray<DiagnosticData> Diagnostics) solutionUpdate, CancellationToken cancellationToken);
    }
}
