// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

internal sealed class StartupHook
{
    public static void Initialize()
    {
        Task.Run(async () =>
        {
            Console.WriteLine("Starting update loop.");
            
            using var pipeClient = new NamedPipeClientStream(".", "netcore-hot-reload", PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            try
            {
                await pipeClient.ConnectAsync(5000);
                Console.WriteLine("Connected.");
            }
            catch (TimeoutException)
            {
                Console.WriteLine("Unable to connect to hot-reload server.");
            }

            while (pipeClient.IsConnected)
            {
                var bytes = new byte[4096];
                var numBytes = await pipeClient.ReadAsync(bytes);

                var update = JsonSerializer.Deserialize<UpdatePayload>(bytes.AsSpan(0, numBytes));
                Console.WriteLine("Attempting to apply diff.");

                try
                {
                    foreach (var item in update.Deltas)
                    {
                        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.Modules.FirstOrDefault() is Module m && m.ModuleVersionId == item.ModuleId);
                        if (assembly is not null)
                        {
                            System.Reflection.Metadata.AssemblyExtensions.ApplyUpdate(assembly, item.MetadataDelta, item.ILDelta, ReadOnlySpan<byte>.Empty);
                        }
                    }

                    pipeClient.WriteByte(0);

                    Console.WriteLine("Applied diff");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }
            Console.WriteLine("Exited update loops.");
        });
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

