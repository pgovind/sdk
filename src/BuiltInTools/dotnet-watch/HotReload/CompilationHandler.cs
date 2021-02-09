// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#nullable enable

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Tools.Internal;
using Microsoft.VisualStudio.Debugger.Contracts.EditAndContinue;

namespace Microsoft.DotNet.Watcher.Tools
{
    internal class CompilationHandler
    {
        private static readonly SolutionActiveStatementSpanProvider NoActiveSpans = (_, _) => new(ImmutableArray<TextSpan>.Empty);
        private Task? _initializeTask;
        private Solution? _currentSolution;
        private IEditAndContinueWorkspaceService? _editAndContinue;

        private bool _failedToInitialize;
        private readonly IDeltaApplier _deltaApplier;
        private readonly IReporter _reporter;

        public CompilationHandler(IDeltaApplier deltaApplier, IReporter reporter)
        {
            _deltaApplier = deltaApplier;
            _reporter = reporter;
        }

        public async ValueTask InitializeAsync(DotNetWatchContext context, CancellationToken cancellationToken)
        {
            await _deltaApplier.InitializeAsync(context, cancellationToken);

            if (context.Iteration == 0)
            {
                var instance = MSBuildLocator.QueryVisualStudioInstances().First();

                _reporter.Verbose($"Using MSBuild at '{instance.MSBuildPath}' to load projects.");
                MSBuildLocator.RegisterInstance(instance);
            }
            else
            {
                var (success, currentSolution, editAndContinue) = await EnsureSolutionInitializedAsync();
                if (success)
                {
                    currentSolution.Workspace.Dispose();
                }
            }

            if (context.FileSet.Project.IsNetCoreApp60OrNewer())
            {
                _initializeTask = Task.Run(() => InitializeMSBuildSolutionAsync(context.FileSet.Project.ProjectPath, _reporter), cancellationToken);

                context.ProcessSpec.EnvironmentVariables["COMPLUS_ForceEnc"] = "1";
            }

            return;
        }

        public async ValueTask<bool> TryHandleFileChange(DotNetWatchContext context, FileItem file, CancellationToken cancellationToken)
        {
            if (!file.FilePath.EndsWith(".cs", StringComparison.Ordinal) &&
                !file.FilePath.EndsWith(".razor", StringComparison.Ordinal))
            {
                return false;
            }

            var (success, currentSolution, editAndContinue) = await EnsureSolutionInitializedAsync();

            if (!success)
            {
                return false;
            }

            editAndContinue.StartEditSession(StubDebuggerService.Instance, out _);

            Solution? updatedSolution = null;
            ProjectId updatedProjectId;
            if (currentSolution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.FilePath == file.FilePath) is Document documentToUpdate)
            {
                var sourceText = await GetSourceTextAsync(file.FilePath);
                updatedSolution = documentToUpdate.WithText(sourceText).Project.Solution;
                updatedProjectId = documentToUpdate.Project.Id;
            }
            else if (currentSolution.Projects.SelectMany(p => p.AdditionalDocuments).FirstOrDefault(d => d.FilePath == file.FilePath) is AdditionalDocument additionalDocument)
            {
                var sourceText = await GetSourceTextAsync(file.FilePath);
                updatedSolution = currentSolution.WithAdditionalDocumentText(additionalDocument.Id, sourceText, PreservationMode.PreserveValue);
                updatedProjectId = additionalDocument.Project.Id;
            }
            else
            {
                _reporter.Verbose($"Could not find document with path {file.FilePath} in the workspace.");
                return false;
            }

            var (updates, diagnostics) = await editAndContinue.EmitSolutionUpdateAsync(updatedSolution, NoActiveSpans, cancellationToken);

            if (updates.Status == ManagedModuleUpdateStatus.None)
            {
                editAndContinue.EndEditSession(out _);
                return true;
            }
            else if (updates.Status == ManagedModuleUpdateStatus.Blocked)
            {
                // Rude edit or compilation error. We eventually want to try and distinguish between the two cases since one of these
                // can be handled on the client. But for now, let's handle them as the same.
                _reporter.Verbose("Unable to apply update.");

                foreach (var diagnosticData in diagnostics)
                {
                    var project = updatedSolution.GetProject(diagnosticData.ProjectId);
                    if (project is null)
                    {
                        continue;
                    }
                    var diagnostic = await diagnosticData.ToDiagnosticAsync(project, cancellationToken);
                    _reporter.Verbose(diagnostic.ToString());
                }

                return false;
            }

            editAndContinue.CommitSolutionUpdate();
            editAndContinue.EndEditSession(out _);

            // Calling Workspace.TryApply on an MSBuildWorkspace causes the workspace to write source text changes to disk.
            // We'll instead simply keep track of the updated solution.
            currentSolution = updatedSolution;

            return await _deltaApplier.Apply(context, updatedSolution, (updates, diagnostics), cancellationToken);
        }

        private async ValueTask<(bool, Solution, IEditAndContinueWorkspaceService)> EnsureSolutionInitializedAsync()
        {
            if (_initializeTask is null)
            {
                return (false, null!, null!);
            }

            await _initializeTask;

            if (_failedToInitialize)
            {
                return (false, null!, null!);
            }

            Debug.Assert(_currentSolution is not null);
            Debug.Assert(_editAndContinue is not null);

            return (true, _currentSolution, _editAndContinue);
        }

        private async ValueTask<SourceText> GetSourceTextAsync(string filePath)
        {
            // FSW events sometimes appear before the file has been completely written to disk. Provide a small delay before we read the file contents
            // to ensure we are not contending with partial writes or write locks.
            await Task.Delay(20);

            for (var attemptIndex = 0; attemptIndex < 10; attemptIndex++)
            {
                try
                {
                    using var stream = File.OpenRead(filePath);
                    return SourceText.From(stream, Encoding.UTF8);
                }
                catch (IOException) when (attemptIndex < 8)
                {
                    await Task.Delay(100);
                }
            }

            Debug.Fail("This shouldn't happen.");
            return null;
        }

        private async Task InitializeMSBuildSolutionAsync(string projectPath, IReporter reporter)
        {
            var workspace = MSBuildWorkspace.Create();

            workspace.WorkspaceFailed += (_sender, diag) =>
            {
                if (diag.Diagnostic.Kind == WorkspaceDiagnosticKind.Warning)
                {
                    reporter.Verbose($"MSBuildWorkspace warning: {diag.Diagnostic}");
                }
                else
                {
                    reporter.Warn($"Failed to create MSBuildWorkspace: {diag.Diagnostic}");
                    _failedToInitialize = true;
                }
            };

            var enc = workspace.Services.GetRequiredService<IEditAndContinueWorkspaceService>();
            await workspace.OpenProjectAsync(projectPath);
            enc.StartDebuggingSession(workspace.CurrentSolution);

            foreach (var project in workspace.CurrentSolution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    await document.GetTextAsync();
                    await enc.OnSourceFileUpdatedAsync(document);
                }

                foreach (var document in project.AdditionalDocuments)
                {
                    await document.GetTextAsync();
                }
            }

            _editAndContinue = enc;
            _currentSolution = workspace.CurrentSolution;
            _failedToInitialize = false;
        }
    }
}
