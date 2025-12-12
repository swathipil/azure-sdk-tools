// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;
using Azure.Sdk.Tools.Cli.Tools.APIView;
using OpenAI;
using OpenAI.Chat;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Azure.Sdk.Tools.Cli.Services.APIView;

public enum CommentCategory
{
    TspApplicable,
    HandwrittenRequired,
    DiscussionOnly
}

public class ClassifiedComment
{
    public string CommentId { get; set; } = string.Empty;
    public string CommentText { get; set; } = string.Empty;
    public CommentCategory Category { get; set; }
    public string Reasoning { get; set; } = string.Empty;
    public double Confidence { get; set; } = 0.0;
}

public class CommentClassificationResult
{
    public List<ClassifiedComment> TspApplicable { get; set; } = new();
    public List<ClassifiedComment> HandwrittenRequired { get; set; } = new();
    public List<ClassifiedComment> DiscussionOnly { get; set; } = new();
}

public interface ICommentClassificationService
{
    Task<CommentClassificationResult> ClassifyCommentsAsync(
        List<APIViewComment> comments,
        CancellationToken cancellationToken = default);
}

public class CommentClassificationService : ICommentClassificationService
{
    private readonly ILogger<CommentClassificationService> _logger;
    private readonly OpenAIClient _openAIClient;
    private readonly string _typeSpecGuidelines;

    public CommentClassificationService(
        ILogger<CommentClassificationService> logger,
        OpenAIClient openAIClient)
    {
        _logger = logger;
        _openAIClient = openAIClient;
        _typeSpecGuidelines = LoadTypeSpecGuidelines();
    }

    public async Task<CommentClassificationResult> ClassifyCommentsAsync(
        List<APIViewComment> comments,
        CancellationToken cancellationToken = default)
    {
        if (comments == null || comments.Count == 0)
        {
            return new CommentClassificationResult();
        }

        try
        {
            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(comments);

            var anthropicModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
            var azureOpenAIModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL") ?? "gpt-5.1";
            var modelName = !string.IsNullOrEmpty(anthropicModel) ? anthropicModel : azureOpenAIModel;

            _logger.LogInformation("Classifying {Count} comments using {Model}", comments.Count, modelName);
            
            var responseContent = await SendChatCompletionAsync(systemPrompt, userPrompt, cancellationToken);

            _logger.LogInformation("Raw response from AI: {Response}", responseContent.Length > 500 ? responseContent.Substring(0, 500) + "..." : responseContent);
            
            var result = ParseClassificationResponse(responseContent, comments);

            _logger.LogInformation(
                "Classification complete: {Tsp} TSP-applicable, {Handwritten} handwritten-required, {Discussion} discussion-only",
                result.TspApplicable.Count, result.HandwrittenRequired.Count, result.DiscussionOnly.Count);

            WriteDebugOutput(result);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to classify comments with AI agent");
            // Fallback: treat all as discussion-only if classification fails
            return new CommentClassificationResult
            {
                DiscussionOnly = comments.Select(c => new ClassifiedComment
                {
                    CommentId = c.CommentId ?? string.Empty,
                    CommentText = c.CommentText ?? string.Empty,
                    Category = CommentCategory.DiscussionOnly,
                    Reasoning = "Classification service unavailable"
                }).ToList()
            };
        }
    }

    private string BuildSystemPrompt()
    {
        return $@"You are a TypeSpec customization expert analyzing API review feedback.

Your task is to classify each comment into one of three categories:

1. **TSP_APPLICABLE**: Can be implemented using TypeSpec decorators in client.tsp
   - Generate the exact TypeSpec code using decorators
   - Include proper imports if needed
   
2. **HANDWRITTEN_REQUIRED**: Requires custom SDK code beyond TypeSpec capabilities
   - Explain why it needs handwritten code
   
3. **DISCUSSION_ONLY**: Questions, acknowledgments, or non-actionable feedback
   - Mark as informational

## TypeSpec Capabilities (TSP_APPLICABLE examples):

### Renaming
- Comment: ""rename X to Y"" → `@@clientName(X, ""Y"")`
- Comment: ""call this FooClient"" → `@@clientName(Foo, ""FooClient"")`

### Visibility
- Comment: ""make this internal"" → `@@access(X, Access.internal)`
- Comment: ""hide this from public API"" → `@@access(X, Access.internal)`

### Moving operations
- Comment: ""move to AdminClient"" → `@@clientLocation(op, ""AdminClient"")`
- Comment: ""this should be on root client"" → `@@clientLocation(op, ServiceName)`

### Documentation
- Comment: ""add description: ..."" → `@@clientDoc(X, ""..."", DocumentationMode.append)`

### Namespace changes
- Comment: ""move to different package"" → `@@clientNamespace(X, ""New.Namespace"")`

## Handwritten Code Required (HANDWRITTEN_REQUIRED examples):
- ""Add retry logic with exponential backoff""
- ""Implement custom authentication flow""
- ""Add pagination helper method""
- ""Custom serialization for this type""
- ""Transform response to flatten nested structure""
- ""Add convenience method that combines multiple calls""
- Protocol-level changes (HTTP headers, auth, status codes)

## Discussion Only (DISCUSSION_ONLY examples):
- Questions: ""Why is this async?"", ""Should we support...?""
- Acknowledgments: ""LGTM"", ""Approved"", ""Thanks for the update""
- Out of scope: ""File a GitHub issue for..."", ""Let's discuss offline""

## TypeSpec Customization Guidelines:
{_typeSpecGuidelines}

## Response Format:
Return a JSON object with this structure:
{{
  ""classifications"": [
    {{
      ""commentId"": ""comment-id-here"",
      ""category"": ""TSP_APPLICABLE"" | ""HANDWRITTEN_REQUIRED"" | ""DISCUSSION_ONLY"",
      ""reasoning"": ""Brief explanation"",
      ""confidence"": 0.0-1.0
    }}
  ]
}}

Confidence scoring:
- 0.9-1.0: Very confident (clear, unambiguous case)
- 0.7-0.89: Confident (typical case with some interpretation)
- 0.5-0.69: Moderate confidence (borderline or requires assumptions)
- <0.5: Low confidence (uncertain, needs review)

Be conservative: if unsure whether TypeSpec can handle it, classify as HANDWRITTEN_REQUIRED.";
    }

    private string BuildUserPrompt(List<APIViewComment> comments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Classify these API review comments:");
        sb.AppendLine();

        for (int i = 0; i < comments.Count; i++)
        {
            var comment = comments[i];
            sb.AppendLine($"Comment {i + 1}:");
            sb.AppendLine($"  ID: {comment.CommentId ?? "unknown"}");
            sb.AppendLine($"  Text: {comment.CommentText ?? ""}");
            sb.AppendLine($"  Element: {comment.ElementId ?? "N/A"}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<string> SendChatCompletionAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var anthropicEndpoint = Environment.GetEnvironmentVariable("ANTHROPIC_ENDPOINT");
        var anthropicModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");

        if (!string.IsNullOrEmpty(anthropicEndpoint) && !string.IsNullOrEmpty(anthropicModel))
        {
            return await SendAnthropicRequestAsync(anthropicEndpoint, anthropicModel, systemPrompt, userPrompt, cancellationToken);
        }

        var azureOpenAIModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL") ?? "gpt-5.1";
        return await SendOpenAIRequestAsync(azureOpenAIModel, systemPrompt, userPrompt, cancellationToken);
    }

    private async Task<string> SendAnthropicRequestAsync(string endpoint, string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var authToken = await GetAuthTokenAsync(cancellationToken);
        
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var requestBody = new AnthropicRequest(
            Model: model,
            MaxTokens: 4096,
            Temperature: 0.1f,
            System: systemPrompt,
            Messages: [new AnthropicMessage("user", userPrompt)]
        );

        var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        var url = $"{endpoint.TrimEnd('/')}/v1/messages";

        _logger.LogDebug("Sending request to Anthropic endpoint: {Url}", url);
        var response = await httpClient.PostAsync(url, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Anthropic API request failed: {(int)response.StatusCode} ({response.ReasonPhrase}). Details: {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var anthropicResponse = JsonSerializer.Deserialize<AnthropicResponse>(responseContent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        return anthropicResponse?.Content?[0]?.Text ?? string.Empty;
    }

    private async Task<string> SendOpenAIRequestAsync(string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var chatClient = _openAIClient.GetChatClient(model);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var completion = await chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            Temperature = 0.1f,
            MaxOutputTokenCount = 4096,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        }, cancellationToken);

        return completion.Value.Content[0].Text;
    }

    private async Task<string> GetAuthTokenAsync(CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            return apiKey;
        }

        var credential = new DefaultAzureCredential();
        var tokenResult = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
            cancellationToken);
        return tokenResult.Token;
    }

    private sealed record AnthropicRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("system")] string? System,
        [property: JsonPropertyName("messages")] AnthropicMessage[] Messages
    );

    private sealed record AnthropicMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    private sealed record AnthropicResponse(
        [property: JsonPropertyName("content")] AnthropicContent[]? Content
    );

    private sealed record AnthropicContent(
        [property: JsonPropertyName("text")] string? Text
    );

    private CommentClassificationResult ParseClassificationResponse(string jsonResponse, List<APIViewComment> originalComments)
    {
        var result = new CommentClassificationResult();

        try
        {
            // Remove markdown code block markers if present (Claude returns JSON wrapped in ```json ... ```)
            var cleanedJson = jsonResponse.Trim();
            if (cleanedJson.StartsWith("```json"))
            {
                cleanedJson = cleanedJson.Substring("```json".Length).Trim();
            }
            if (cleanedJson.StartsWith("```"))
            {
                cleanedJson = cleanedJson.Substring("```".Length).Trim();
            }
            if (cleanedJson.EndsWith("```"))
            {
                cleanedJson = cleanedJson.Substring(0, cleanedJson.Length - 3).Trim();
            }
            
            using var doc = JsonDocument.Parse(cleanedJson);
            var classifications = doc.RootElement.GetProperty("classifications");

            foreach (var classificationElement in classifications.EnumerateArray())
            {
                var commentId = classificationElement.GetProperty("commentId").GetString() ?? string.Empty;
                var categoryStr = classificationElement.GetProperty("category").GetString() ?? "DISCUSSION_ONLY";
                var reasoning = classificationElement.GetProperty("reasoning").GetString() ?? string.Empty;
                
                var confidence = classificationElement.TryGetProperty("confidence", out var confElement) 
                    ? confElement.GetDouble() 
                    : 0.0;

                var category = categoryStr switch
                {
                    "TSP_APPLICABLE" => CommentCategory.TspApplicable,
                    "HANDWRITTEN_REQUIRED" => CommentCategory.HandwrittenRequired,
                    _ => CommentCategory.DiscussionOnly
                };

                var originalComment = originalComments.FirstOrDefault(c => c.CommentId == commentId);
                var classified = new ClassifiedComment
                {
                    CommentId = commentId,
                    CommentText = originalComment?.CommentText ?? string.Empty,
                    Category = category,
                    Reasoning = reasoning,
                    Confidence = confidence
                };

                switch (category)
                {
                    case CommentCategory.TspApplicable:
                        result.TspApplicable.Add(classified);
                        break;
                    case CommentCategory.HandwrittenRequired:
                        result.HandwrittenRequired.Add(classified);
                        break;
                    case CommentCategory.DiscussionOnly:
                        result.DiscussionOnly.Add(classified);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse classification response");
        }

        return result;
    }

    private string LoadTypeSpecGuidelines()
    {
        // Condensed version of key TypeSpec customization guidelines
        return @"
### Core Decorators:

**@@access(target, Access.public | Access.internal)**: Control visibility
**@@clientName(target, ""NewName"")**: Rename types, operations, parameters
**@@clientLocation(operation, ""ClientName"")**: Move operations to different clients
**@@clientDoc(target, ""documentation"", DocumentationMode.append | .replace)**: Add/override docs
**@@clientNamespace(target, ""Namespace.Name"")**: Change package/namespace
**@@override(original, customOperation)**: Custom method signatures

### Common Patterns:
- Always use PascalCase or camelCase for renames (SDKs apply language conventions)
- Use scope parameter for language-specific: `@@clientName(foo, ""bar"", ""python"")`
- Exclude languages with negation: `@@clientName(foo, ""bar"", ""!csharp"")`

### Limitations:
- Cannot add custom business logic
- Cannot implement retry/auth mechanisms
- Cannot add helper methods beyond spec operations
- Cannot modify protocol-level behavior (HTTP, serialization)";
    }

    private void WriteDebugOutput(CommentClassificationResult result)
    {
        try
        {
            var allClassifications = new List<object>();
            
            foreach (var comment in result.TspApplicable)
            {
                allClassifications.Add(new
                {
                    commentId = comment.CommentId,
                    category = "TSP_APPLICABLE",
                    confidence = comment.Confidence,
                    reasoning = comment.Reasoning,
                    commentText = comment.CommentText
                });
            }
            
            foreach (var comment in result.HandwrittenRequired)
            {
                allClassifications.Add(new
                {
                    commentId = comment.CommentId,
                    category = "HANDWRITTEN_REQUIRED",
                    confidence = comment.Confidence,
                    reasoning = comment.Reasoning,
                    commentText = comment.CommentText
                });
            }
            
            foreach (var comment in result.DiscussionOnly)
            {
                allClassifications.Add(new
                {
                    commentId = comment.CommentId,
                    category = "DISCUSSION_ONLY",
                    confidence = comment.Confidence,
                    reasoning = comment.Reasoning,
                    commentText = comment.CommentText
                });
            }
            
            var sortedClassifications = allClassifications
                .OrderByDescending(c => ((dynamic)c).confidence)
                .ToList();
            
            var debugOutput = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                summary = new
                {
                    tspApplicable = result.TspApplicable.Count,
                    handwrittenRequired = result.HandwrittenRequired.Count,
                    discussionOnly = result.DiscussionOnly.Count,
                    total = result.TspApplicable.Count + result.HandwrittenRequired.Count + result.DiscussionOnly.Count
                },
                classifications = sortedClassifications
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(debugOutput, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            
            var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "comment-classifications-debug.json");
            File.WriteAllText(outputPath, json);
            
            _logger.LogInformation("Debug output written to: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write debug output file");
        }
    }
}
