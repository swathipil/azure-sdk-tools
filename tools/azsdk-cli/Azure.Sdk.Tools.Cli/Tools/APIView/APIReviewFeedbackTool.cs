// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.CommandLine;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Sdk.Tools.Cli.Commands;
using Azure.Sdk.Tools.Cli.Helpers;
using Azure.Sdk.Tools.Cli.Models;
using Azure.Sdk.Tools.Cli.Models.Responses.TypeSpec;
using Azure.Sdk.Tools.Cli.Services.APIView;
using ModelContextProtocol.Server;

namespace Azure.Sdk.Tools.Cli.Tools.APIView;

[McpServerToolType]
[Description("Apply API review feedback to TypeSpec specifications")]
public class APIReviewFeedbackTool : MCPTool
{
    private const string ApplyFeedbackToolName = "azsdk_apiview_apply_feedback";

    public override CommandGroup[] CommandHierarchy { get; set; } = [SharedCommandGroups.APIView];

    private readonly IAPIViewService _apiViewService;
    private readonly ITypeSpecHelper _typeSpecHelper;
    private readonly IGitHelper _gitHelper;
    private readonly IProcessHelper _processHelper;
    private readonly ICommentClassificationService _classificationService;
    private readonly ILogger<APIReviewFeedbackTool> _logger;

    // CLI Options
    private readonly Argument<string> apiViewUrlArg = new("apiview-url")
    {
        Description = "The URL to the API review in APIView (e.g., https://apiview.dev/review/{reviewId}?activeApiRevisionId={revisionId})",
        Arity = ArgumentArity.ExactlyOne
    };

    private readonly Option<string?> typeSpecProjectPathOption = new("--project-path", "-p")
    {
        Description = "Absolute path to the TypeSpec project root directory (containing tspconfig.yaml). If not provided, will search the current workspace.",
        Required = false
    };

    private readonly Option<string> branchOption = new("--branch", "-b")
    {
        Description = "Target branch name (will create if doesn't exist, or use existing)",
        Required = false
    };

    private readonly Option<bool> dryRunOption = new("--dry-run")
    {
        Description = "Show proposed changes without applying them",
        DefaultValueFactory = _ => false
    };

    public APIReviewFeedbackTool(
        ILogger<APIReviewFeedbackTool> logger,
        IAPIViewService apiViewService,
        ITypeSpecHelper typeSpecHelper,
        IGitHelper gitHelper,
        IProcessHelper processHelper,
        ICommentClassificationService classificationService)
    {
        _logger = logger;
        _apiViewService = apiViewService;
        _typeSpecHelper = typeSpecHelper;
        _gitHelper = gitHelper;
        _processHelper = processHelper;
        _classificationService = classificationService;
    }

    protected override Command GetCommand() => new("apply-feedback", "Apply API review feedback to TypeSpec client.tsp")
    {
        apiViewUrlArg,
        typeSpecProjectPathOption,
        branchOption,
        dryRunOption
    };

    public override async Task<CommandResponse> HandleCommand(ParseResult parseResult, CancellationToken ct)
    {
        var apiViewUrl = parseResult.GetValue(apiViewUrlArg);
        var typeSpecProjectPath = parseResult.GetValue(typeSpecProjectPathOption);
        var branch = parseResult.GetValue(branchOption);
        var dryRun = parseResult.GetValue(dryRunOption);

        return await ApplyReviewFeedback(apiViewUrl!, typeSpecProjectPath, branch, dryRun, ct);
    }

    [McpServerTool(Name = ApplyFeedbackToolName)]
    [Description("Apply API review feedback from APIView to TypeSpec client.tsp file. Fetches comments, parses applicable feedback, generates TypeSpec customizations following the guidelines at https://github.com/Azure/azure-rest-api-specs/blob/main/eng/common/knowledge/customizing-client-tsp.md, and updates the client.tsp file.")]
    public async Task<APIReviewFeedbackResponse> ApplyReviewFeedback(
        string apiViewUrl,
        string? typeSpecProjectPath = null,
        string? targetBranch = null,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Starting API review feedback application for {ApiViewUrl}", apiViewUrl);

            // Step 1: Fetch comments from APIView
            var (revisionId, reviewId) = ExtractIdsFromUrl(apiViewUrl);
            string? commentsJson = await _apiViewService.GetCommentsByRevisionAsync(revisionId);

            if (string.IsNullOrEmpty(commentsJson))
            {
                return CreateErrorResponse(string.Empty, "Failed to retrieve comments from APIView");
            }

            // Step 2: Parse comments
            var comments = ParseComments(commentsJson);

            if (comments.Count == 0)
            {
                return CreateResponse(string.Empty, "No comments found in APIView review", 0, 0, 0);
            }

            _logger.LogInformation("Found {Count} comments, classifying with AI agent...", comments.Count);

            // Step 2a: Classify comments with AI agent
            var classification = await _classificationService.ClassifyCommentsAsync(comments, ct);

            _logger.LogInformation(
                "Classification complete: {Tsp} TSP-applicable, {Handwritten} handwritten, {Discussion} discussion",
                classification.TspApplicable.Count,
                classification.HandwrittenRequired.Count,
                classification.DiscussionOnly.Count);

            // Step 3: Infer TypeSpec project path from APIView content or discover until metadata endpoint is available
            if (string.IsNullOrWhiteSpace(typeSpecProjectPath))
            {
                typeSpecProjectPath = await DiscoverTypeSpecProjectAsync(revisionId, reviewId, ct);
                if (typeSpecProjectPath == null)
                {
                    return CreateErrorResponse(string.Empty, "Could not determine TypeSpec project. Please provide --project-path parameter.");
                }
            }

            // Step 4: Validate TypeSpec project
            if (!_typeSpecHelper.IsValidTypeSpecProjectPath(typeSpecProjectPath))
            {
                return CreateErrorResponse(typeSpecProjectPath, "Invalid TypeSpec project path. Must contain tspconfig.yaml and main.tsp files.");
            }

            // Step 5: Apply only TSP-applicable comments to client.tsp
            var clientTspPath = Path.Combine(typeSpecProjectPath, "client.tsp");

            if (classification.TspApplicable.Count == 0)
            {
                _logger.LogInformation("No TSP-applicable comments found. Only handwritten or discussion comments.");
                
                var noTspResponse = CreateResponse(
                    typeSpecProjectPath,
                    "No TypeSpec customizations to apply. See handwritten and discussion comments below.",
                    comments.Count,
                    0,
                    comments.Count
                );
                
                noTspResponse.HandwrittenRequired = classification.HandwrittenRequired.Select(c => new HandwrittenComment
                {
                    CommentText = c.CommentText,
                    Reasoning = c.Reasoning
                }).ToList();
                
                noTspResponse.DiscussionOnly = classification.DiscussionOnly.Select(c => new DiscussionComment
                {
                    CommentText = c.CommentText
                }).ToList();

                return noTspResponse;
            }

            if (dryRun)
            {
                return CreateDryRunResponse(typeSpecProjectPath, comments.Count, classification.TspApplicable.Count, 
                    classification, clientTspPath);
            }

            // Step 6: Apply TypeSpec customizations to client.tsp
            await ApplyTypeSpecCustomizations(clientTspPath, classification.TspApplicable, ct);

            // Step 7: Run TypeSpec validation
            var validationPassed = await ValidateTypeSpecProject(typeSpecProjectPath, ct);

            // Step 8: Get git information
            var currentBranch = _gitHelper.GetBranchName(typeSpecProjectPath);

            // Step 9: Build response
            var response = CreateResponse(
                typeSpecProjectPath,
                validationPassed ? "API review feedback applied successfully" : 
                    "Changes applied but validation failed - please review and fix errors",
                comments.Count,
                classification.TspApplicable.Count,
                classification.HandwrittenRequired.Count + classification.DiscussionOnly.Count
            );

            response.ValidationPassed = validationPassed;
            response.ClientTspPath = clientTspPath;
            response.BranchName = currentBranch;
            response.ChangesSummary = classification.TspApplicable
                .Select(c => $"Applied: {c.CommentText.Substring(0, Math.Min(60, c.CommentText.Length))}...")
                .ToList();
            response.HandwrittenRequired = classification.HandwrittenRequired.Select(c => new HandwrittenComment
            {
                CommentText = c.CommentText,
                Reasoning = c.Reasoning
            }).ToList();
            response.DiscussionOnly = classification.DiscussionOnly.Select(c => new DiscussionComment
            {
                CommentText = c.CommentText
            }).ToList();
            response.NextSteps = GenerateNextSteps(validationPassed, currentBranch, clientTspPath, 
                classification.HandwrittenRequired.Count);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying API review feedback");
            return CreateErrorResponse(typeSpecProjectPath, $"Failed to apply feedback: {ex.Message}");
        }
    }

    /// <summary>
    /// Discovers TypeSpec projects by:
    /// 1. Getting package name from APIView content
    /// 2. Finding package path in azure-sdk-for-python repo using GitHub API
    /// 3. Reading tsp-location.yaml from GitHub to get TypeSpec repo, commit, and directory
    /// 4. Checking out the correct commit in the local TypeSpec repo
    /// </summary>
    private async Task<string?> DiscoverTypeSpecProjectAsync(string revisionId, string reviewId, CancellationToken ct)
    {
        // Step 1: Get package name from APIView content
        var packageName = await GetPackageNameFromAPIViewContentAsync(revisionId, reviewId, ct);
        
        if (string.IsNullOrEmpty(packageName))
        {
            _logger.LogWarning("Could not extract package name from APIView content");
            return null;
        }

        _logger.LogInformation("Extracted package name from APIView: {PackageName}", packageName);

        // Step 2: Find package path in azure-sdk-for-python using GitHub API
        var packagePath = await FindPackagePathOnGitHubAsync(packageName, ct);
        if (packagePath == null)
        {
            _logger.LogWarning("Could not find package '{Package}' in Azure/azure-sdk-for-python repo on GitHub", packageName);
            return null;
        }

        _logger.LogInformation("Found package path on GitHub: {Path}", packagePath);

        // Step 3: Read tsp-location.yaml from GitHub
        var tspLocation = await ReadTspLocationFromGitHubAsync(packagePath, ct);
        if (tspLocation == null)
        {
            _logger.LogError("No tsp-location.yaml found in {Path}. This package may not be generated from TypeSpec.", packagePath);
            _logger.LogInformation("Cannot automatically discover TypeSpec project location without tsp-location.yaml file.");
            return null;
        }

        _logger.LogInformation("TypeSpec location - Repo: {Repo}, Commit: {Commit}, Directory: {Dir}", 
            tspLocation.Repo, tspLocation.Commit, tspLocation.Directory);

        // Step 5: Checkout the correct commit in local TypeSpec repo
        var typespecRepoPath = FindLocalTypeSpecRepo(tspLocation.Repo);
        if (typespecRepoPath == null)
        {
            _logger.LogWarning("Could not find local clone of TypeSpec repo: {Repo}", tspLocation.Repo);
            return null;
        }

        _logger.LogInformation("Found local TypeSpec repo at: {Path}", typespecRepoPath);

        // Checkout the specified commit
        var checkoutSuccess = await CheckoutCommitAsync(typespecRepoPath, tspLocation.Commit, ct);
        if (!checkoutSuccess)
        {
            _logger.LogWarning("Failed to checkout commit {Commit}, falling back to main branch", tspLocation.Commit);
            
            // Fall back to main branch if commit doesn't exist
            var checkoutMainSuccess = await CheckoutBranchAsync(typespecRepoPath, "main", ct);
            if (!checkoutMainSuccess)
            {
                _logger.LogWarning("Failed to checkout main branch in {Repo}", typespecRepoPath);
                return null;
            }
            
            _logger.LogInformation("Using current main branch instead of specific commit");
        }
        else
        {
            _logger.LogInformation("Successfully checked out commit {Commit}", tspLocation.Commit);
        }

        // Return the full path to the TypeSpec directory
        var fullTypeSpecPath = Path.Combine(typespecRepoPath, tspLocation.Directory);
        
        if (!Directory.Exists(fullTypeSpecPath))
        {
            _logger.LogWarning("TypeSpec directory does not exist: {Path}", fullTypeSpecPath);
            return null;
        }

        return fullTypeSpecPath;
    }

    /// <summary>
    /// Gets package name from APIView content (text format)
    /// Package name should be at the beginning of the content
    /// </summary>
    private async Task<string?> GetPackageNameFromAPIViewContentAsync(string revisionId, string reviewId, CancellationToken ct)
    {
        try
        {
            // Get content in text format - package name is at the beginning
            var contentJson = await _apiViewService.GetRevisionContent(revisionId, reviewId, "text");
            if (string.IsNullOrEmpty(contentJson))
            {
                _logger.LogDebug("No content returned from APIView for revision {RevisionId}", revisionId);
                return null;
            }

            // The API returns JSON-encoded string, so we need to parse it
            string content;
            try
            {
                var jsonDoc = JsonDocument.Parse(contentJson);
                content = jsonDoc.RootElement.GetString() ?? "";
            }
            catch (JsonException)
            {
                // If it's not JSON, use the content as-is
                content = contentJson;
            }

            _logger.LogDebug("Got content from APIView, length: {Length}", content.Length);

            // Parse the content to extract package name from namespace declaration
            // Format: "namespace azure.ai.textanalytics.authoring" or similar
            // Note: Content may have header lines before the actual code, so check more lines
            // Use Environment.NewLine-agnostic splitting
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            
            _logger.LogInformation("Checking {Count} lines for namespace declaration", lines.Length);
            
            int lineNum = 0;
            foreach (var line in lines.Take(150)) // Check first 150 lines to account for headers
            {
                lineNum++;
                var trimmedLine = line.Trim();
                
                if (lineNum <= 10)
                {
                    _logger.LogInformation("Line {Num}: '{Line}'", lineNum, trimmedLine.Length > 100 ? trimmedLine.Substring(0, 100) + "..." : trimmedLine);
                }
                
                // Look for lines that start with "namespace " followed by package name
                if (trimmedLine.StartsWith("namespace ") && trimmedLine.Contains("."))
                {
                    var packageName = trimmedLine.Substring("namespace ".Length).Trim();
                    _logger.LogInformation("Found package name from APIView content at line {LineNum}: {PackageName}", lineNum, packageName);
                    return packageName;
                }
            }

            _logger.LogWarning("No namespace declaration found in APIView content. First 500 chars: {Content}", 
                content.Substring(0, Math.Min(500, content.Length)));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get package name from APIView content");
            return null;
        }
    }

    /// <summary>
    /// Finds package path in Azure/azure-sdk-for-python repository by trying common locations
    /// </summary>
    private async Task<string?> FindPackagePathOnGitHubAsync(string packageName, CancellationToken ct)
    {
        try
        {
            // Convert package name to expected folder name
            var folderName = packageName.Replace(".", "-");
            _logger.LogInformation("Searching GitHub for package folder: {FolderName}", folderName);

            // Try to find the package by probing common service directories
            // The SDK follows pattern: sdk/{service}/{packageName}/tsp-location.yaml
            // We'll try to guess the service name from the package name
            
            // Extract service name from package (e.g., "azure.ai.textanalytics" -> "textanalytics")
            var parts = packageName.Split('.');
            string? serviceName = parts.Length >= 3 ? parts[2] : parts.Length >= 2 ? parts[1] : null;
            
            var possiblePaths = new List<string>();
            
            if (serviceName != null)
            {
                // Try exact service name
                possiblePaths.Add($"sdk/{serviceName}/{folderName}");
                // Try with common variations
                possiblePaths.Add($"sdk/cognitive{serviceName}/{folderName}");
                possiblePaths.Add($"sdk/{serviceName.ToLower()}service/{folderName}");
            }
            
            // Also try searching all sdk subdirectories by pattern matching
            // For planetarycomputer: sdk/planetaryComputer/azure-planetarycomputer
            possiblePaths.Add($"sdk/planetaryComputer/{folderName}");
            possiblePaths.Add($"sdk/planetarycomputer/{folderName}");
            
            // Add common service directories
            possiblePaths.Add($"sdk/cognitivelanguage/{folderName}");
            possiblePaths.Add($"sdk/ai/{folderName}");
            
            foreach (var path in possiblePaths)
            {
                var tspLocationPath = await TryFetchTspLocationFromGitHubAsync(path, ct);
                if (tspLocationPath != null)
                {
                    _logger.LogInformation("Found package path on GitHub: {Path}", path);
                    return path;
                }
            }

            _logger.LogWarning("Could not find tsp-location.yaml for package {FolderName} in common SDK locations", folderName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching GitHub for package path");
            return null;
        }
    }
    
    /// <summary>
    /// Tries to fetch tsp-location.yaml from a specific path on GitHub
    /// </summary>
    private async Task<string?> TryFetchTspLocationFromGitHubAsync(string packagePath, CancellationToken ct)
    {
        try
        {
            var rawUrl = $"https://raw.githubusercontent.com/Azure/azure-sdk-for-python/main/{packagePath}/tsp-location.yaml";
            
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Azure-SDK-Tools-CLI");
            httpClient.Timeout = TimeSpan.FromSeconds(5); // Quick timeout for probing

            var response = await httpClient.GetAsync(rawUrl, ct);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(ct);
            }
            
            return null;
        }
        catch
        {
            return null; // Ignore errors during probing
        }
    }

    /// <summary>
    /// Reads tsp-location.yaml content from GitHub using raw content API
    /// Package path should already include the tsp-location.yaml content from probing
    /// </summary>
    private async Task<TspLocationInfo?> ReadTspLocationFromGitHubAsync(string packagePath, CancellationToken ct)
    {
        try
        {
            // Try to fetch the content directly
            var content = await TryFetchTspLocationFromGitHubAsync(packagePath, ct);
            if (content == null)
            {
                _logger.LogWarning("Failed to fetch tsp-location.yaml from GitHub for path: {Path}", packagePath);
                return null;
            }

            return ParseTspLocationYaml(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading tsp-location.yaml from GitHub");
            return null;
        }
    }

    /// <summary>
    /// Parses tsp-location.yaml content
    /// </summary>
    private TspLocationInfo? ParseTspLocationYaml(string content)
    {
        try
        {
            var lines = content.Split('\n');

            string? repo = null;
            string? commit = null;
            string? directory = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("repo:"))
                {
                    repo = trimmed.Substring("repo:".Length).Trim();
                }
                else if (trimmed.StartsWith("commit:"))
                {
                    commit = trimmed.Substring("commit:".Length).Trim();
                }
                else if (trimmed.StartsWith("directory:"))
                {
                    var dirValue = trimmed.Substring("directory:".Length).Trim();
                    if (!string.IsNullOrEmpty(dirValue))
                    {
                        directory = dirValue;
                    }
                }
            }

            if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(commit) || string.IsNullOrEmpty(directory))
            {
                _logger.LogWarning("tsp-location.yaml is missing required fields (repo, commit, or directory)");
                return null;
            }

            return new TspLocationInfo
            {
                Repo = repo,
                Commit = commit,
                Directory = directory
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse tsp-location.yaml content");
            return null;
        }
    }

    /// <summary>
    /// Finds azure-sdk-for-python repository in common locations
    /// </summary>
    private string? FindAzureSdkForPythonRepo()
    {
        var homeDir = Environment.GetEnvironmentVariable("HOME") ?? "";
        var possiblePaths = new[]
        {
            Path.Combine(homeDir, "repos", "azure-sdk-for-python"),
            Path.Combine(Directory.GetCurrentDirectory(), "azure-sdk-for-python"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "azure-sdk-for-python")
        };

        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path) && Directory.Exists(Path.Combine(path, ".git")))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Updates azure-sdk-for-python repo by checking out main and pulling latest changes
    /// </summary>
    private async Task<bool> UpdateSdkRepoAsync(string repoPath, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Updating azure-sdk-for-python repo at: {Path}", repoPath);

            // Checkout main branch
            var checkoutOptions = new ProcessOptions(
                "git",
                new[] { "checkout", "main" },
                logOutputStream: true,
                workingDirectory: repoPath
            );
            
            var checkoutResult = await _processHelper.Run(checkoutOptions, ct);
            if (checkoutResult.ExitCode != 0)
            {
                _logger.LogWarning("Failed to checkout main branch");
                return false;
            }

            _logger.LogInformation("Checked out main branch");

            // Pull latest changes from upstream
            var pullOptions = new ProcessOptions(
                "git",
                new[] { "pull", "upstream", "main" },
                logOutputStream: true,
                workingDirectory: repoPath
            );
            
            var pullResult = await _processHelper.Run(pullOptions, ct);
            if (pullResult.ExitCode != 0)
            {
                // Try origin if upstream doesn't exist
                _logger.LogDebug("Pull from upstream failed, trying origin");
                var pullOriginOptions = new ProcessOptions(
                    "git",
                    new[] { "pull", "origin", "main" },
                    logOutputStream: true,
                    workingDirectory: repoPath
                );
                
                var pullOriginResult = await _processHelper.Run(pullOriginOptions, ct);
                if (pullOriginResult.ExitCode != 0)
                {
                    _logger.LogWarning("Failed to pull latest changes from origin");
                    return false;
                }
            }

            _logger.LogInformation("Successfully updated azure-sdk-for-python repo");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating azure-sdk-for-python repo");
            return false;
        }
    }

    /// <summary>
    /// Finds package folder in azure-sdk-for-python repo
    /// Package name like "azure.ai.textanalytics.authoring" maps to "sdk/textanalytics/azure-ai-textanalytics-authoring"
    /// </summary>
    private string? FindPackageInAzureSdkForPython(string packageName, string sdkRepoPath)
    {
        _logger.LogDebug("Searching in azure-sdk-for-python at: {Path}", sdkRepoPath);

        // Convert package name to SDK folder name
        // "azure.ai.textanalytics.authoring" -> search for "azure-ai-textanalytics-authoring"
        var sdkPackageName = packageName.Replace(".", "-");
        
        _logger.LogInformation("Looking for package folder: {PackageName}", sdkPackageName);
        
        // Search in sdk/ directory - packages are nested under service folders
        // e.g., sdk/cognitivelanguage/azure-ai-textanalytics-authoring/
        var sdkDir = Path.Combine(sdkRepoPath, "sdk");
        if (!Directory.Exists(sdkDir))
        {
            _logger.LogDebug("sdk/ directory not found in azure-sdk-for-python");
            return null;
        }

        // Search recursively under sdk/ for the exact package folder name
        var packageFolders = Directory.GetDirectories(sdkDir, sdkPackageName, SearchOption.AllDirectories);
        if (packageFolders.Length > 0)
        {
            _logger.LogInformation("Found exact match: {Path}", packageFolders[0]);
            return packageFolders[0];
        }

        // If exact match not found, try partial match
        _logger.LogDebug("Exact match not found, trying partial match");
        var packageParts = packageName.Split('.').Skip(1).ToArray(); // Skip "azure"
        var partialPattern = $"*{string.Join("-", packageParts)}*";
        
        _logger.LogDebug("Searching with pattern: {Pattern}", partialPattern);
        
        // Search all service directories
        foreach (var serviceDir in Directory.GetDirectories(sdkDir))
        {
            var matchingPackages = Directory.GetDirectories(serviceDir, partialPattern, SearchOption.TopDirectoryOnly);
            if (matchingPackages.Length > 0)
            {
                _logger.LogInformation("Found partial match: {Path}", matchingPackages[0]);
                return matchingPackages[0];
            }
        }

        _logger.LogWarning("No package folder found for: {PackageName}", sdkPackageName);
        return null;
    }

    /// <summary>
    /// Reads tsp-location.yaml file and extracts repo, commit, and directory information
    /// </summary>
    private TspLocationInfo? ReadTspLocationYaml(string packagePath)
    {
        var tspLocationPath = Path.Combine(packagePath, "tsp-location.yaml");
        if (!File.Exists(tspLocationPath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(tspLocationPath);
            var lines = content.Split('\n');

            string? repo = null;
            string? commit = null;
            string? directory = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("repo:"))
                {
                    repo = trimmed.Substring("repo:".Length).Trim();
                }
                else if (trimmed.StartsWith("commit:"))
                {
                    commit = trimmed.Substring("commit:".Length).Trim();
                }
                else if (trimmed.StartsWith("directory:"))
                {
                    var dirValue = trimmed.Substring("directory:".Length).Trim();
                    if (!string.IsNullOrEmpty(dirValue))
                    {
                        directory = dirValue;
                    }
                }
                // additionalDirectories is a separate field, ignore it
            }

            if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(commit) || string.IsNullOrEmpty(directory))
            {
                _logger.LogWarning("tsp-location.yaml is missing required fields (repo, commit, or directory)");
                return null;
            }

            return new TspLocationInfo
            {
                Repo = repo,
                Commit = commit,
                Directory = directory
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tsp-location.yaml");
            return null;
        }
    }

    /// <summary>
    /// Finds local clone of TypeSpec repository
    /// </summary>
    private string? FindLocalTypeSpecRepo(string repoUrl)
    {
        // Extract repo name from URL (e.g., "Azure/azure-rest-api-specs" from "https://github.com/Azure/azure-rest-api-specs")
        var repoName = repoUrl.Split('/').Last().Replace(".git", "");

        var homeDir = Environment.GetEnvironmentVariable("HOME") ?? "";
        var possiblePaths = new[]
        {
            Path.Combine(homeDir, "repos", repoName),
            Path.Combine(Directory.GetCurrentDirectory(), repoName),
            Path.Combine(Directory.GetCurrentDirectory(), "..", repoName)
        };

        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path) && Directory.Exists(Path.Combine(path, ".git")))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks out a branch and pulls latest changes
    /// </summary>
    private async Task<bool> CheckoutBranchAsync(string repoPath, string branch, CancellationToken ct)
    {
        try
        {
            // Checkout branch
            var checkoutOptions = new ProcessOptions(
                "git",
                new[] { "checkout", branch },
                logOutputStream: true,
                workingDirectory: repoPath
            );
            
            var checkoutResult = await _processHelper.Run(checkoutOptions, ct);
            if (checkoutResult.ExitCode != 0)
            {
                return false;
            }

            // Pull latest changes
            var pullOptions = new ProcessOptions(
                "git",
                new[] { "pull" },
                logOutputStream: false,
                workingDirectory: repoPath
            );
            
            await _processHelper.Run(pullOptions, ct);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to checkout branch {Branch}", branch);
            return false;
        }
    }

    /// <summary>
    /// Checks out a specific commit in a git repository
    /// Fetches from upstream (Azure) and origin to ensure commit is available
    /// </summary>
    private async Task<bool> CheckoutCommitAsync(string repoPath, string commit, CancellationToken ct)
    {
        try
        {
            // First, try to fetch from upstream (Azure repo) since commits usually come from there
            _logger.LogInformation("Fetching latest changes from upstream...");
            var fetchUpstreamOptions = new ProcessOptions(
                "git",
                new[] { "fetch", "upstream" },
                logOutputStream: false,
                workingDirectory: repoPath
            );
            
            var fetchUpstreamResult = await _processHelper.Run(fetchUpstreamOptions, ct);
            if (fetchUpstreamResult.ExitCode != 0)
            {
                _logger.LogDebug("Failed to fetch from upstream, trying origin");
                
                // Fall back to origin if upstream doesn't exist
                var fetchOriginOptions = new ProcessOptions(
                    "git",
                    new[] { "fetch", "origin" },
                    logOutputStream: false,
                    workingDirectory: repoPath
                );
                
                var fetchOriginResult = await _processHelper.Run(fetchOriginOptions, ct);
                if (fetchOriginResult.ExitCode != 0)
                {
                    _logger.LogWarning("Failed to fetch from both upstream and origin");
                }
            }

            // Run git checkout
            _logger.LogInformation("Checking out commit {Commit}...", commit);
            var options = new ProcessOptions(
                "git",
                new[] { "checkout", commit },
                logOutputStream: true,
                workingDirectory: repoPath
            );
            
            var result = await _processHelper.Run(options, ct);

            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to checkout commit {Commit}", commit);
            return false;
        }
    }

    private class TspLocationInfo
    {
        public required string Repo { get; set; }
        public required string Commit { get; set; }
        public required string Directory { get; set; }
    }

    /// <summary>
    /// Discovers TypeSpec projects in the current workspace by searching for tspconfig.yaml files.
    /// Prioritizes azure-rest-api-specs/specification directory structure.
    /// </summary>
    private string? DiscoverTypeSpecProject()
    {
        var currentDir = Directory.GetCurrentDirectory();
        _logger.LogDebug("Searching for TypeSpec projects starting from: {Dir}", currentDir);

        // Look for specification directory in common locations
        var searchPaths = new[]
        {
            Path.Combine(currentDir, "specification"),
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "repos", "azure-rest-api-specs", "specification"),
            currentDir // fallback to current directory
        };

        foreach (var searchPath in searchPaths)
        {
            if (!Directory.Exists(searchPath))
            {
                continue;
            }

            _logger.LogDebug("Searching for TypeSpec projects in: {Path}", searchPath);
            
            var tspConfigFiles = Directory.GetFiles(searchPath, "tspconfig.yaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/node_modules/") && !f.Contains("/.git/"))
                .ToList();

            if (tspConfigFiles.Count > 0)
            {
                var projectPath = Path.GetDirectoryName(tspConfigFiles[0]);
                _logger.LogInformation("Found TypeSpec project: {Path}", projectPath);
                if (tspConfigFiles.Count > 1)
                {
                    _logger.LogInformation("Note: {Count} TypeSpec projects found. Using first match. Specify --project-path for different project.", tspConfigFiles.Count);
                }
                return projectPath;
            }
        }

        // Fallback: Search for tspconfig.yaml files in entire workspace
        var allTspConfigFiles = Directory.GetFiles(currentDir, "tspconfig.yaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/node_modules/") && !f.Contains("/.git/"))
            .ToList();

        if (allTspConfigFiles.Count == 0)
        {
            _logger.LogWarning("No tspconfig.yaml files found in workspace");
            return null;
        }

        if (allTspConfigFiles.Count == 1)
        {
            var projectPath = Path.GetDirectoryName(allTspConfigFiles[0]);
            _logger.LogInformation("Found single TypeSpec project: {Path}", projectPath);
            return projectPath;
        }

        // Multiple projects found - try to pick the most relevant one
        var sortedProjects = allTspConfigFiles
            .Select(f => new { Path = Path.GetDirectoryName(f)!, File = f })
            .OrderBy(p => !p.Path.Contains("/specification/"))  // Prefer specification/ paths
            .ThenBy(p => !p.Path.Contains(currentDir))          // Prefer paths containing current dir
            .ThenBy(p => p.Path.Split('/').Length)              // Prefer shorter paths (less nested)
            .ToList();

        var selectedProject = sortedProjects.First().Path;
        _logger.LogInformation("Multiple TypeSpec projects found ({Count}). Selected: {Path}", allTspConfigFiles.Count, selectedProject);
        _logger.LogDebug("Other projects found: {Others}", string.Join(", ", sortedProjects.Skip(1).Select(p => p.Path)));

        return selectedProject;
    }

    /// <summary>
    /// Infers the target language from tspconfig.yaml emitter configuration
    /// </summary>
    private string? InferLanguageFromTspConfig(string typeSpecProjectPath)
    {
        var tspConfigPath = Path.Combine(typeSpecProjectPath, "tspconfig.yaml");
        if (!File.Exists(tspConfigPath))
        {
            _logger.LogWarning("tspconfig.yaml not found at {Path}", tspConfigPath);
            return null;
        }

        try
        {
            var content = File.ReadAllText(tspConfigPath);
            
            // Look for emitter configurations in tspconfig.yaml
            // Common patterns: @azure-tools/typespec-python, @azure-tools/typespec-csharp, etc.
            var emitterMappings = new Dictionary<string, string>
            {
                { "typespec-python", "python" },
                { "typespec-csharp", "csharp" },
                { "typespec-java", "java" },
                { "typespec-ts", "javascript" },
                { "typespec-go", "go" }
            };

            foreach (var mapping in emitterMappings)
            {
                if (content.Contains(mapping.Key))
                {
                    _logger.LogDebug("Found emitter {Emitter} in tspconfig.yaml", mapping.Key);
                    return mapping.Value;
                }
            }

            _logger.LogWarning("No recognized language emitter found in tspconfig.yaml");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or parse tspconfig.yaml");
            return null;
        }
    }

    private (string revisionId, string reviewId) ExtractIdsFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Input cannot be null or empty", nameof(url));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException("Input needs to be a valid APIView URL (e.g., https://apiview.dev/review/{reviewId}?activeApiRevisionId={revisionId})", nameof(url));
        }

        var match = Regex.Match(url, @"/review/([^/?]+).*[?&]activeApiRevisionId=([^&#]+)", RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            throw new ArgumentException("APIView URL must contain both 'activeApiRevisionId' query parameter AND '/review/{reviewId}' path segment");
        }

        string reviewId = match.Groups[1].Value;
        string revisionId = match.Groups[2].Value;

        return (revisionId, reviewId);
    }

    private List<APIViewComment> ParseComments(string commentsJson)
    {
        try
        {
            var comments = JsonSerializer.Deserialize<List<APIViewComment>>(commentsJson);
            return comments ?? new List<APIViewComment>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse comments JSON, returning empty list");
            return new List<APIViewComment>();
        }
    }

    private async Task ApplyTypeSpecCustomizations(string clientTspPath, List<ClassifiedComment> tspComments, CancellationToken ct)
    {
        // Ensure client.tsp exists
        if (!File.Exists(clientTspPath))
        {
            // Create new client.tsp with basic structure
            var initialContent = @"import ""./main.tsp"";
import ""@azure-tools/typespec-client-generator-core"";

using Azure.ClientGenerator.Core;

// Client customizations
";
            await File.WriteAllTextAsync(clientTspPath, initialContent, ct);
        }

        // Read existing content
        var existingContent = await File.ReadAllTextAsync(clientTspPath, ct);

        // TODO: Generate TypeSpec customizations with full project context
        // For now, just add comments listing what needs to be implemented
        var newContent = existingContent + "\n// === API Review Feedback - TSP Applicable ===\n";
        newContent += $"// Applied on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n";
        newContent += "// TODO: Implement TypeSpec customizations for the following feedback:\n\n";
        
        foreach (var comment in tspComments)
        {
            newContent += $"// - {comment.Reasoning}\n";
        }

        // Write updated content
        await File.WriteAllTextAsync(clientTspPath, newContent, ct);
        
        _logger.LogInformation("Applied {Count} TypeSpec customizations to {Path}", tspComments.Count, clientTspPath);
    }

    private async Task<bool> ValidateTypeSpecProject(string typeSpecProjectPath, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Validating TypeSpec project at {Path}", typeSpecProjectPath);

            var options = new ProcessOptions(
                command: "npx",
                args: ["tsp", "compile", "."],
                workingDirectory: typeSpecProjectPath,
                timeout: TimeSpan.FromMinutes(5)
            );

            var result = await _processHelper.Run(options, ct);

            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeSpec validation failed");
            return false;
        }
    }

    private APIReviewFeedbackResponse CreateResponse(
        string typeSpecProjectPath,
        string message,
        int processed,
        int applied,
        int skipped)
    {
        return new APIReviewFeedbackResponse
        {
            TypeSpecProject = typeSpecProjectPath,
            PackageType = _typeSpecHelper.IsTypeSpecProjectForMgmtPlane(typeSpecProjectPath) 
                ? SdkType.Management 
                : SdkType.Dataplane,
            Message = message,
            CommentsProcessed = processed,
            CommentsApplied = applied,
            CommentsSkipped = skipped,
            ValidationPassed = false
        };
    }

    private APIReviewFeedbackResponse CreateErrorResponse(string typeSpecProjectPath, string error)
    {
        return new APIReviewFeedbackResponse
        {
            TypeSpecProject = typeSpecProjectPath,
            PackageType = SdkType.Unknown,
            ResponseError = error,
            CommentsProcessed = 0,
            CommentsApplied = 0,
            CommentsSkipped = 0,
            ValidationPassed = false
        };
    }

    private APIReviewFeedbackResponse CreateDryRunResponse(
        string typeSpecProjectPath,
        int totalComments,
        int applicableComments,
        CommentClassificationResult classification,
        string clientTspPath)
    {
        var response = CreateResponse(
            typeSpecProjectPath,
            "Dry run completed - no changes applied",
            totalComments,
            0,
            totalComments - applicableComments
        );

        response.ClientTspPath = clientTspPath;
        response.ChangesSummary = classification.TspApplicable
            .Select(c => $"[Would apply] {c.CommentText.Substring(0, Math.Min(50, c.CommentText.Length))}...")
            .ToList();
        response.HandwrittenRequired = classification.HandwrittenRequired.Select(c => new HandwrittenComment
        {
            CommentText = c.CommentText,
            Reasoning = c.Reasoning
        }).ToList();
        response.DiscussionOnly = classification.DiscussionOnly.Select(c => new DiscussionComment
        {
            CommentText = c.CommentText
        }).ToList();
        response.NextSteps = new List<string>
        {
            "Review the proposed TypeSpec customizations above",
            $"{classification.HandwrittenRequired.Count} comments require handwritten code (see details above)",
            "Run without --dry-run flag to apply TypeSpec changes",
            "Manually implement handwritten code changes in SDK"
        };

        return response;
    }

    private List<string> GenerateNextSteps(bool validationPassed, string branchName, string clientTspPath, int handwrittenCount)
    {
        var steps = new List<string>();

        if (validationPassed)
        {
            steps.Add($"Review changes in {clientTspPath}");
            steps.Add($"Run 'git diff' to see all modifications");
            if (handwrittenCount > 0)
            {
                steps.Add($"Implement {handwrittenCount} handwritten code changes (see details above)");
            }
            steps.Add($"Stage changes: git add {clientTspPath}");
            steps.Add($"Commit changes: git commit -m \"Apply API review feedback\"");
            steps.Add($"Push changes: git push origin {branchName}");
        }
        else
        {
            steps.Add($"Fix TypeSpec compilation errors in the project");
            steps.Add($"Review and adjust customizations in {clientTspPath}");
            steps.Add($"Run 'npx tsp compile .' to validate fixes");
            steps.Add("Repeat until validation passes");
            if (handwrittenCount > 0)
            {
                steps.Add($"After validation passes, implement {handwrittenCount} handwritten code changes");
            }
        }

        return steps;
    }
}

// Supporting classes
public class APIViewComment
{
    [JsonPropertyName("commentId")]
    public string? CommentId { get; set; }
    
    [JsonPropertyName("commentText")]
    public string? CommentText { get; set; }
    
    [JsonPropertyName("elementId")]
    public string? ElementId { get; set; }
    
    [JsonPropertyName("createdBy")]
    public string? Username { get; set; }
    
    
    [JsonPropertyName("isResolved")]
    public bool? IsResolved { get; set; }
}
