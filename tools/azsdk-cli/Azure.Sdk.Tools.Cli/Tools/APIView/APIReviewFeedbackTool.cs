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
        IProcessHelper processHelper)
    {
        _logger = logger;
        _apiViewService = apiViewService;
        _typeSpecHelper = typeSpecHelper;
        _gitHelper = gitHelper;
        _processHelper = processHelper;
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

            // TODO: Step 3: Infer TypeSpec project path from APIView metadata
            // For now, fall back to discovery if not provided
            if (string.IsNullOrWhiteSpace(typeSpecProjectPath))
            {
                typeSpecProjectPath = DiscoverTypeSpecProject();
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

            // Step 5: Generate client.tsp customizations from all comments
            var clientTspPath = Path.Combine(typeSpecProjectPath, "client.tsp");
            var customizations = GenerateClientTspCustomizations(comments);

            if (dryRun)
            {
                return CreateDryRunResponse(typeSpecProjectPath, comments.Count, comments.Count, 
                    customizations, clientTspPath);
            }

            // Step 5: Apply changes to client.tsp
            await ApplyCustomizations(clientTspPath, customizations, ct);

            // Step 6: Run TypeSpec validation
            var validationPassed = await ValidateTypeSpecProject(typeSpecProjectPath, ct);

            // Step 7: Get git information
            var currentBranch = _gitHelper.GetBranchName(typeSpecProjectPath);

            // Step 8: Build response
            var response = CreateResponse(
                typeSpecProjectPath,
                validationPassed ? "API review feedback applied successfully" : 
                    "Changes applied but validation failed - please review and fix errors",
                comments.Count,
                comments.Count,
                0
            );

            response.ValidationPassed = validationPassed;
            response.ClientTspPath = clientTspPath;
            response.BranchName = currentBranch;
            response.ChangesSummary = customizations.Select(c => c.Summary).ToList();
            response.NextSteps = GenerateNextSteps(validationPassed, currentBranch, clientTspPath);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying API review feedback");
            return CreateErrorResponse(typeSpecProjectPath, $"Failed to apply feedback: {ex.Message}");
        }
    }

    /// <summary>
    /// Discovers TypeSpec projects in the current workspace by searching for tspconfig.yaml files
    /// </summary>
    private string? DiscoverTypeSpecProject()
    {
        var currentDir = Directory.GetCurrentDirectory();
        _logger.LogDebug("Searching for TypeSpec projects starting from: {Dir}", currentDir);

        // Search for tspconfig.yaml files in the specification directory structure
        var tspConfigFiles = Directory.GetFiles(currentDir, "tspconfig.yaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/node_modules/") && !f.Contains("/.git/"))
            .ToList();

        if (tspConfigFiles.Count == 0)
        {
            _logger.LogWarning("No tspconfig.yaml files found in workspace");
            return null;
        }

        if (tspConfigFiles.Count == 1)
        {
            var projectPath = Path.GetDirectoryName(tspConfigFiles[0]);
            _logger.LogInformation("Found single TypeSpec project: {Path}", projectPath);
            return projectPath;
        }

        // Multiple projects found - try to pick the most relevant one
        // Prefer projects under specification/ directory and closer to current directory
        var sortedProjects = tspConfigFiles
            .Select(f => new { Path = Path.GetDirectoryName(f)!, File = f })
            .OrderBy(p => !p.Path.Contains("/specification/"))  // Prefer specification/ paths
            .ThenBy(p => !p.Path.Contains(currentDir))          // Prefer paths containing current dir
            .ThenBy(p => p.Path.Split('/').Length)              // Prefer shorter paths (less nested)
            .ToList();

        var selectedProject = sortedProjects.First().Path;
        _logger.LogInformation("Multiple TypeSpec projects found ({Count}). Selected: {Path}", tspConfigFiles.Count, selectedProject);
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

    private List<ClientTspCustomization> GenerateClientTspCustomizations(List<APIViewComment> comments)
    {
        var customizations = new List<ClientTspCustomization>();

        foreach (var comment in comments)
        {
            var customization = ParseCommentToCustomization(comment);
            if (customization != null)
            {
                customizations.Add(customization);
            }
        }

        return customizations;
    }

    private ClientTspCustomization? ParseCommentToCustomization(APIViewComment comment)
    {
        var commentText = comment.CommentText ?? "";

        // TODO: Implement more sophisticated parsing logic
        // For now, create a placeholder customization that includes the comment
        var truncatedText = commentText.Length > 100 
            ? commentText.Substring(0, 100) + "..." 
            : commentText;
            
        return new ClientTspCustomization
        {
            Type = CustomizationType.Comment,
            Summary = $"Comment: {truncatedText}",
            Code = $"// APIView feedback: {commentText}"
        };
    }

    private async Task ApplyCustomizations(string clientTspPath, List<ClientTspCustomization> customizations, CancellationToken ct)
    {
        // Ensure client.tsp exists
        if (!File.Exists(clientTspPath))
        {
            // Create new client.tsp with basic structure
            var initialContent = @"import ""./main.tsp"";
import ""@azure-tools/typespec-client-generator-core"";

using Azure.ClientGenerator.Core;

// Client customizations for API review feedback
";
            await File.WriteAllTextAsync(clientTspPath, initialContent, ct);
        }

        // Read existing content
        var existingContent = await File.ReadAllTextAsync(clientTspPath, ct);

        // Append customizations
        var newContent = existingContent + "\n// === API Review Feedback Customizations ===\n";
        foreach (var customization in customizations)
        {
            newContent += customization.Code + "\n";
        }

        // Write updated content
        await File.WriteAllTextAsync(clientTspPath, newContent, ct);
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
        List<ClientTspCustomization> customizations,
        string clientTspPath)
    {
        var response = CreateResponse(
            typeSpecProjectPath,
            "Dry run completed - no changes applied",
            totalComments,
            0,
            totalComments
        );

        response.ClientTspPath = clientTspPath;
        response.ChangesSummary = customizations.Select(c => $"[Would apply] {c.Summary}").ToList();
        response.NextSteps = new List<string>
        {
            "Review the proposed changes above",
            "Run without --dry-run flag to apply changes",
            "Manually review and adjust customizations in client.tsp as needed"
        };

        return response;
    }

    private List<string> GenerateNextSteps(bool validationPassed, string branchName, string clientTspPath)
    {
        var steps = new List<string>();

        if (validationPassed)
        {
            steps.Add($"Review changes in {clientTspPath}");
            steps.Add($"Run 'git diff' to see all modifications");
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
    
    [JsonPropertyName("lineNo")]
    public int? LineNumber { get; set; }
    
    [JsonPropertyName("isResolved")]
    public bool? IsResolved { get; set; }
}

public enum CustomizationType
{
    ClientName,
    Access,
    ClientLocation,
    Comment
}

public class ClientTspCustomization
{
    public CustomizationType Type { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}
