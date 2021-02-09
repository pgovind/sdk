// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Tools.Internal;
using Microsoft.VisualStudio.Debugger.Contracts.EditAndContinue;

namespace Microsoft.DotNet.Watcher.Tools
{
    internal class BlazorWebAssemblyDeltaApplier : IDeltaApplier
    {
        private readonly IReporter _reporter;

        public BlazorWebAssemblyDeltaApplier(IReporter reporter)
        {
            _reporter = reporter;
        }

        public ValueTask InitializeAsync(DotNetWatchContext context, CancellationToken cancellationToken)
        {
            return default;
        }

        public async ValueTask<bool> Apply(DotNetWatchContext context, Solution solution, (ManagedModuleUpdates Updates, ImmutableArray<DiagnosticData> Diagnostics) solutionUpdate, CancellationToken cancellationToken)
        {
            if (context.BrowserRefreshServer is null)
            {
                _reporter.Verbose("Unable to send deltas because the refresh server is unavailable.");
                return false;
            }

            var (updates, diagnostics) = solutionUpdate;

            var payload = new UpdatePayload
            {
                Deltas = updates.Updates.Select(c => new UpdateDelta
                {
                    ModuleId = c.Module,
                    ILDelta = c.ILDelta.ToArray(),
                    MetadataDelta = c.MetadataDelta.ToArray(),
                }),
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await context.BrowserRefreshServer.SendMessage(bytes, cancellationToken);

            return true;
        }

        private readonly struct UpdatePayload
        {
            public string Type => "HotReloadDelta";
            public IEnumerable<UpdateDelta> Deltas { get; init; }
        }

        private readonly struct UpdateDelta
        {
            public Guid ModuleId { get; init; }
            public byte[] MetadataDelta { get; init; }
            public byte[] ILDelta { get; init; }
        }
    }
}
