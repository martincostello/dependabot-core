using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;

using NuGetUpdater.Core.Analyze;
using NuGetUpdater.Core.Utilities;

namespace NuGetUpdater.Core.Discover;

public partial class DiscoveryWorker
{
    public const string DiscoveryResultFileName = "./.dependabot/discovery.json";

    private readonly ILogger _logger;
    private readonly HashSet<string> _processedProjectPaths = new(StringComparer.OrdinalIgnoreCase); private readonly HashSet<string> _restoredMSBuildSdks = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public DiscoveryWorker(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(string repoRootPath, string workspacePath, string outputPath)
    {
        var result = await RunWithErrorHandlingAsync(repoRootPath, workspacePath);
        await WriteResultsAsync(repoRootPath, outputPath, result);
    }

    internal async Task<WorkspaceDiscoveryResult> RunWithErrorHandlingAsync(string repoRootPath, string workspacePath)
    {
        WorkspaceDiscoveryResult result;
        try
        {
            result = await RunAsync(repoRootPath, workspacePath);
        }
        catch (HttpRequestException ex)
        when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            result = new WorkspaceDiscoveryResult
            {
                ErrorType = ErrorType.AuthenticationFailure,
                ErrorDetails = "(" + string.Join("|", NuGetContext.GetPackageSourceUrls(PathHelper.JoinPath(repoRootPath, workspacePath))) + ")",
                Path = workspacePath,
                Projects = [],
            };
        }

        return result;
    }

    internal async Task<WorkspaceDiscoveryResult> RunAsync(string repoRootPath, string workspacePath)
    {
        MSBuildHelper.RegisterMSBuild(Environment.CurrentDirectory, repoRootPath);

        // the `workspacePath` variable is relative to a repository root, so a rooted path actually isn't rooted; the
        // easy way to deal with this is to just trim the leading "/" if it exists
        if (workspacePath.StartsWith("/"))
        {
            workspacePath = workspacePath[1..];
        }

        string initialWorkspacePath = workspacePath;
        workspacePath = Path.Combine(repoRootPath, workspacePath);

        DotNetToolsJsonDiscoveryResult? dotNetToolsJsonDiscovery = null;
        GlobalJsonDiscoveryResult? globalJsonDiscovery = null;
        DirectoryPackagesPropsDiscoveryResult? directoryPackagesPropsDiscovery = null;

        ImmutableArray<ProjectDiscoveryResult> projectResults = [];
        WorkspaceDiscoveryResult result;

        if (Directory.Exists(workspacePath))
        {
            _logger.Log($"Discovering build files in workspace [{workspacePath}].");

            dotNetToolsJsonDiscovery = DotNetToolsJsonDiscovery.Discover(repoRootPath, workspacePath, _logger);
            globalJsonDiscovery = GlobalJsonDiscovery.Discover(repoRootPath, workspacePath, _logger);

            if (globalJsonDiscovery is not null)
            {
                await TryRestoreMSBuildSdksAsync(repoRootPath, workspacePath, globalJsonDiscovery.Dependencies, _logger);
            }

            // this next line should throw or something
            projectResults = await RunForDirectoryAsnyc(repoRootPath, workspacePath);

            directoryPackagesPropsDiscovery = DirectoryPackagesPropsDiscovery.Discover(repoRootPath, workspacePath, projectResults, _logger);

            if (directoryPackagesPropsDiscovery is not null)
            {
                projectResults = projectResults.Remove(projectResults.First(p => p.FilePath.Equals(directoryPackagesPropsDiscovery.FilePath, StringComparison.OrdinalIgnoreCase)));
            }
        }
        else
        {
            _logger.Log($"Workspace path [{workspacePath}] does not exist.");
        }

        result = new WorkspaceDiscoveryResult
        {
            Path = initialWorkspacePath,
            DotNetToolsJson = dotNetToolsJsonDiscovery,
            GlobalJson = globalJsonDiscovery,
            DirectoryPackagesProps = directoryPackagesPropsDiscovery,
            Projects = projectResults.OrderBy(p => p.FilePath).ToImmutableArray(),
        };

        _logger.Log("Discovery complete.");
        _processedProjectPaths.Clear();

        return result;
    }

    /// <summary>
    /// Restores MSBuild SDKs from the given dependencies.
    /// </summary>
    /// <returns>Returns `true` when SDKs were restored successfully.</returns>
    private async Task<bool> TryRestoreMSBuildSdksAsync(string repoRootPath, string workspacePath, ImmutableArray<Dependency> dependencies, ILogger logger)
    {
        var msbuildSdks = dependencies
            .Where(d => d.Type == DependencyType.MSBuildSdk && !string.IsNullOrEmpty(d.Version))
            .Where(d => !d.Name.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase))
            .Where(d => !_restoredMSBuildSdks.Contains($"{d.Name}/{d.Version}"))
            .ToImmutableArray();

        if (msbuildSdks.Length == 0)
        {
            return false;
        }

        var keys = msbuildSdks.Select(d => $"{d.Name}/{d.Version}");

        _restoredMSBuildSdks.AddRange(keys);

        _logger.Log($"  Restoring MSBuild SDKs: {string.Join(", ", keys)}");

        return await NuGetHelper.DownloadNuGetPackagesAsync(repoRootPath, workspacePath, msbuildSdks, logger);
    }

    private async Task<ImmutableArray<ProjectDiscoveryResult>> RunForDirectoryAsnyc(string repoRootPath, string workspacePath)
    {
        _logger.Log($"  Discovering projects beneath [{Path.GetRelativePath(repoRootPath, workspacePath)}].");
        var entryPoints = FindEntryPoints(workspacePath);
        var projects = ExpandEntryPointsIntoProjects(entryPoints);
        if (projects.IsEmpty)
        {
            _logger.Log("  No project files found.");
            return [];
        }

        return await RunForProjectPathsAsync(repoRootPath, workspacePath, projects);
    }

    private static ImmutableArray<string> FindEntryPoints(string workspacePath)
    {
        return Directory.EnumerateFiles(workspacePath)
            .Where(path =>
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                switch (extension)
                {
                    case ".sln":
                    case ".proj":
                    case ".csproj":
                    case ".fsproj":
                    case ".vbproj":
                        return true;
                    default:
                        return false;
                }
            })
            .ToImmutableArray();
    }

    private static ImmutableArray<string> ExpandEntryPointsIntoProjects(IEnumerable<string> entryPoints)
    {
        HashSet<string> expandedProjects = new();
        HashSet<string> seenProjects = new();
        Stack<string> filesToExpand = new(entryPoints);
        while (filesToExpand.Count > 0)
        {
            string candidateEntryPoint = filesToExpand.Pop();
            if (seenProjects.Add(candidateEntryPoint))
            {
                string extension = Path.GetExtension(candidateEntryPoint).ToLowerInvariant();
                if (extension == ".sln")
                {
                    SolutionFile solution = SolutionFile.Parse(candidateEntryPoint);
                    foreach (ProjectInSolution project in solution.ProjectsInOrder)
                    {
                        filesToExpand.Push(project.AbsolutePath);
                    }
                }
                else if (extension == ".proj")
                {
                    IEnumerable<string> foundProjects = ExpandItemGroupFilesFromProject(candidateEntryPoint, "ProjectFile", "ProjectReference");
                    foreach (string foundProject in foundProjects)
                    {
                        filesToExpand.Push(foundProject);
                    }
                }
                else
                {
                    switch (extension)
                    {
                        case ".csproj":
                        case ".fsproj":
                        case ".vbproj":
                            // keep this project and check for references
                            expandedProjects.Add(candidateEntryPoint);
                            IEnumerable<string> referencedProjects = ExpandItemGroupFilesFromProject(candidateEntryPoint, "ProjectReference");
                            foreach (string referencedProject in referencedProjects)
                            {
                                filesToExpand.Push(referencedProject);
                            }
                            break;
                        default:
                            continue;
                    }
                }
            }
        }

        return expandedProjects.ToImmutableArray();
    }

    private static IEnumerable<string> ExpandItemGroupFilesFromProject(string projectPath, params string[] itemTypes)
    {
        if (!File.Exists(projectPath))
        {
            return [];
        }

        using ProjectCollection projectCollection = new();
        Project project = Project.FromFile(projectPath, new ProjectOptions
        {
            LoadSettings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreEmptyImports | ProjectLoadSettings.IgnoreInvalidImports,
            ProjectCollection = projectCollection,
        });

        HashSet<string> allowableItemTypes = new(itemTypes, StringComparer.OrdinalIgnoreCase);
        List<ProjectItem> projectItems = project.Items.Where(i => allowableItemTypes.Contains(i.ItemType)).ToList();
        string projectDir = Path.GetDirectoryName(projectPath)!;
        HashSet<string> seenItems = new(StringComparer.OrdinalIgnoreCase);
        List<string> foundItems = new();
        foreach (ProjectItem projectItem in projectItems)
        {
            // referenced projects commonly use the Windows-style directory separator which can cause problems on Unix
            // but Windows is able to handle a Unix-style path, so we normalize everything to that then normalize again
            // with regards to relative paths, e.g., "some/path/" + "..\other\file" => "some/other/file"
            string referencedProjectPath = Path.Join(projectDir, projectItem.EvaluatedInclude.NormalizePathToUnix());
            string normalizedReferenceProjectPath = new FileInfo(referencedProjectPath).FullName;
            if (seenItems.Add(normalizedReferenceProjectPath))
            {
                foundItems.Add(normalizedReferenceProjectPath);
            }
        }

        return foundItems;
    }

    private async Task<ImmutableArray<ProjectDiscoveryResult>> RunForProjectPathsAsync(string repoRootPath, string workspacePath, IEnumerable<string> projectPaths)
    {
        var results = new Dictionary<string, ProjectDiscoveryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in projectPaths)
        {
            // If there is some MSBuild logic that needs to run to fully resolve the path skip the project
            if (!File.Exists(projectPath))
            {
                continue;
            }

            if (_processedProjectPaths.Contains(projectPath))
            {
                continue;
            }
            _processedProjectPaths.Add(projectPath);

            var relativeProjectPath = Path.GetRelativePath(workspacePath, projectPath);
            var packagesConfigDependencies = PackagesConfigDiscovery.Discover(workspacePath, projectPath, _logger)
                    ?.Dependencies;

            var projectResults = await SdkProjectDiscovery.DiscoverAsync(repoRootPath, workspacePath, projectPath, _logger);

            // Determine if there were unrestored MSBuildSdks
            var msbuildSdks = projectResults.SelectMany(p => p.Dependencies.Where(d => d.Type == DependencyType.MSBuildSdk)).ToImmutableArray();
            if (msbuildSdks.Length > 0)
            {
                // If new SDKs were restored, then we need to rerun SdkProjectDiscovery.
                if (await TryRestoreMSBuildSdksAsync(repoRootPath, workspacePath, msbuildSdks, _logger))
                {
                    projectResults = await SdkProjectDiscovery.DiscoverAsync(repoRootPath, workspacePath, projectPath, _logger);
                }
            }

            foreach (var projectResult in projectResults)
            {
                if (results.ContainsKey(projectResult.FilePath))
                {
                    continue;
                }

                // If we had packages.config dependencies, merge them with the project dependencies
                if (projectResult.FilePath == relativeProjectPath && packagesConfigDependencies is not null)
                {
                    packagesConfigDependencies = packagesConfigDependencies.Value
                        .Select(d => d with { TargetFrameworks = projectResult.TargetFrameworks })
                        .ToImmutableArray();

                    results[projectResult.FilePath] = projectResult with
                    {
                        Dependencies = [.. projectResult.Dependencies, .. packagesConfigDependencies],
                    };
                }
                else
                {
                    results[projectResult.FilePath] = projectResult;
                }
            }
        }

        return [.. results.Values];
    }

    internal static async Task WriteResultsAsync(string repoRootPath, string outputPath, WorkspaceDiscoveryResult result)
    {
        var resultPath = Path.IsPathRooted(outputPath)
            ? outputPath
            : Path.GetFullPath(outputPath, repoRootPath);

        var resultDirectory = Path.GetDirectoryName(resultPath)!;
        if (!Directory.Exists(resultDirectory))
        {
            Directory.CreateDirectory(resultDirectory);
        }

        var resultJson = JsonSerializer.Serialize(result, SerializerOptions);
        await File.WriteAllTextAsync(path: resultPath, resultJson);
    }
}
