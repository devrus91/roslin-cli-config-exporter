using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynConfigExporter;

internal static class WorkspaceLoader
{
    public static async Task<(MSBuildWorkspace Workspace, Solution Solution, List<ProjectIssue> Issues)> LoadAsync(
        string target,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target))
        {
            throw new FileNotFoundException("Solution or project was not found.", target);
        }

        if (!MSBuildLocator.IsRegistered)
        {
            var instance = MSBuildLocator.QueryVisualStudioInstances()
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();
            if (instance is null)
            {
                throw new InvalidOperationException("No compatible .NET SDK or MSBuild installation was found.");
            }

            MSBuildLocator.RegisterInstance(instance);
        }

        var properties = new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true",
            ["BuildProjectReferences"] = "false",
            ["SkipCompilerExecution"] = "true",
            ["ProvideCommandLineArgs"] = "true"
        };
        var workspace = MSBuildWorkspace.Create(properties);
        var issues = new List<ProjectIssue>();
        workspace.RegisterWorkspaceFailedHandler(args =>
            issues.Add(new("workspace", args.Diagnostic.Kind.ToString().ToLowerInvariant(), args.Diagnostic.Message)));

        var extension = Path.GetExtension(target).ToLowerInvariant();
        var solution = extension switch
        {
            ".csproj" => (await workspace.OpenProjectAsync(target, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                .Solution,
            ".sln" or ".slnx" => await workspace.OpenSolutionAsync(target, cancellationToken: cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentException("Target must be a .sln, .slnx, or .csproj file.")
        };

        return (workspace, solution, issues);
    }
}
