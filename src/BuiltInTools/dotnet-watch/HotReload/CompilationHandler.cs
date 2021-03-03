// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Tools.Internal;

namespace Microsoft.DotNet.Watcher.Tools
{
    internal class CompilationHandler
    {
        private Task<(Solution, IEditAndContinueWorkspaceService)>? _initializeTask;
        private Solution? _currentSolution;
        private IEditAndContinueWorkspaceService? _editAndContinue;

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

            _initializeTask = Task.Run(() => CompilationWorkspaceProvider.CreateWorkspaceAsync(context.FileSet.Project.ProjectPath, _reporter, cancellationToken), cancellationToken);
            context.ProcessSpec.EnvironmentVariables["COMPLUS_ForceEnc"] = "1";

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

            editAndContinue.StartEditSession(out _);

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

            var (updates, diagnostics) = await editAndContinue.EmitSolutionUpdate2Async(updatedSolution, cancellationToken);

            if (updates.Status == ManagedModuleUpdateStatus2.None)
            {
                editAndContinue.EndEditSession(out _);
                var project = updatedSolution.GetProject(updatedProjectId)!;
                if (project.TryGetCompilation(out var compilation) && compilation.GetDiagnostics() is { Length: > 0 } compilationDiagnostics)
                {
                    foreach (var item in compilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        _reporter.Warn(CSharpDiagnosticFormatter.Instance.Format(item));
                    }

                    return false;
                }

                _reporter.Verbose("No update to apply.");
                return true;
            }
            else if (updates.Status == ManagedModuleUpdateStatus2.Blocked)
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

            try
            {
                (_currentSolution, _editAndContinue) = await _initializeTask;
            }
            catch (Exception ex)
            {
                _reporter.Warn(ex.Message);
                return (false, null!, null!);
            }

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
    }
}
