// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Pipes;
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
    internal class AspNetCoreDeltaApplier : IDeltaApplier
    {
        private readonly IReporter _reporter;
        private Task _task;
        private NamedPipeServerStream _pipe;

        public AspNetCoreDeltaApplier(IReporter reporter)
        {
            _reporter = reporter;
        }

        public async ValueTask InitializeAsync(DotNetWatchContext context, CancellationToken cancellationToken)
        {
            if (_pipe is not null)
            {
                await _pipe.DisposeAsync();
            }

            _pipe = new NamedPipeServerStream("netcore-hot-reload", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _task = _pipe.WaitForConnectionAsync(cancellationToken);

            var deltaApplier = Path.Combine(AppContext.BaseDirectory, "hotreload", "Microsoft.Extensions.AspNetCoreDeltaApplier.dll");
            context.ProcessSpec.EnvironmentVariables.DotNetStartupHooks.Add(deltaApplier);
        }

        public async ValueTask<bool> Apply(DotNetWatchContext context, Solution solution, (ManagedModuleUpdates Updates, ImmutableArray<DiagnosticData> Diagnostics) solutionUpdate, CancellationToken cancellationToken)
        {
            if (!_task.IsCompletedSuccessfully || !_pipe.IsConnected)
            {
                // The client isn't listening
                _reporter.Verbose("No client connected to receive delta updates.");
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

            // Jank mode. We should send this in a better (not json) format
            await JsonSerializer.SerializeAsync(_pipe, payload, cancellationToken: cancellationToken);
            await _pipe.FlushAsync(cancellationToken);

            int result;
            try
            {
                result = _pipe.ReadByte();
            }
            catch (IOException)
            {
                result = -1;
            }

            if (result != 0)
            {
                return false;
            }

            if (context.BrowserRefreshServer != null)
            {
                await context.BrowserRefreshServer.ReloadAsync(cancellationToken);
            }

            return true;
        }

        private readonly struct UpdatePayload
        {
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
