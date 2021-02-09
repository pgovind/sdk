// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Debugger.Contracts.EditAndContinue;

namespace Microsoft.DotNet.Watcher.Tools
{
    internal sealed class StubDebuggerService : IManagedEditAndContinueDebuggerService
    {
        public static readonly StubDebuggerService Instance = new();

        private StubDebuggerService() { }

        public Task<ImmutableArray<ManagedActiveStatementDebugInfo>> GetActiveStatementsAsync(CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ManagedActiveStatementDebugInfo>.Empty);

        public Task<ManagedEditAndContinueAvailability> GetAvailabilityAsync(Guid moduleVersionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ManagedEditAndContinueAvailability(ManagedEditAndContinueAvailabilityStatus.Available));
        }

        public Task PrepareModuleForUpdateAsync(Guid moduleVersionId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
