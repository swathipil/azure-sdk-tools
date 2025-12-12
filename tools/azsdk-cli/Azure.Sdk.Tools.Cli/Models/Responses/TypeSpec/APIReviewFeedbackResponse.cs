// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Text;
using System.Text.Json.Serialization;

namespace Azure.Sdk.Tools.Cli.Models.Responses.TypeSpec;

public class APIReviewFeedbackResponse : TypeSpecBaseResponse
{
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("comments_processed")]
    public int CommentsProcessed { get; set; }

    [JsonPropertyName("comments_applied")]
    public int CommentsApplied { get; set; }

    [JsonPropertyName("comments_skipped")]
    public int CommentsSkipped { get; set; }

    [JsonPropertyName("client_tsp_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientTspPath { get; set; }

    [JsonPropertyName("validation_passed")]
    public bool ValidationPassed { get; set; }

    [JsonPropertyName("branch_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BranchName { get; set; }

    [JsonPropertyName("changes_summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ChangesSummary { get; set; }

    [JsonPropertyName("next_steps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public new List<string>? NextSteps { get; set; }

    [JsonPropertyName("handwritten_required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<HandwrittenComment>? HandwrittenRequired { get; set; }

    [JsonPropertyName("discussion_only")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DiscussionComment>? DiscussionOnly { get; set; }

    protected override string Format()
    {
        var output = new StringBuilder();

        if (!string.IsNullOrEmpty(Message))
        {
            output.AppendLine(Message);
            output.AppendLine();
        }

        output.AppendLine("=== API Review Feedback Summary ===");
        output.AppendLine($"Comments Processed: {CommentsProcessed}");
        output.AppendLine($"Comments Applied: {CommentsApplied}");
        output.AppendLine($"Comments Skipped: {CommentsSkipped}");
        output.AppendLine($"Validation Passed: {ValidationPassed}");

        if (!string.IsNullOrEmpty(ClientTspPath))
        {
            output.AppendLine($"Client.tsp Path: {ClientTspPath}");
        }

        if (!string.IsNullOrEmpty(BranchName))
        {
            output.AppendLine($"Branch: {BranchName}");
        }

        if (ChangesSummary != null && ChangesSummary.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("Changes Applied:");
            foreach (var change in ChangesSummary)
            {
                output.AppendLine($"  • {change}");
            }
        }

        if (HandwrittenRequired != null && HandwrittenRequired.Count > 0)
        {
            output.AppendLine();
            output.AppendLine($"=== Handwritten Code Required ({HandwrittenRequired.Count}) ===");
            foreach (var comment in HandwrittenRequired)
            {
                output.AppendLine($"  • {comment.CommentText}");
                output.AppendLine($"    Reason: {comment.Reasoning}");
            }
        }

        if (DiscussionOnly != null && DiscussionOnly.Count > 0)
        {
            output.AppendLine();
            output.AppendLine($"=== Discussion/Informational ({DiscussionOnly.Count}) ===");
            foreach (var comment in DiscussionOnly.Take(5))
            {
                output.AppendLine($"  • {comment.CommentText}");
            }
            if (DiscussionOnly.Count > 5)
            {
                output.AppendLine($"  ... and {DiscussionOnly.Count - 5} more");
            }
        }

        if (NextSteps != null && NextSteps.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("Next Steps:");
            for (int i = 0; i < NextSteps.Count; i++)
            {
                output.AppendLine($"  {i + 1}. {NextSteps[i]}");
            }
        }

        return output.ToString();
    }
}

public class HandwrittenComment
{
    [JsonPropertyName("comment_text")]
    public string CommentText { get; set; } = string.Empty;

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = string.Empty;

    [JsonPropertyName("element_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ElementId { get; set; }
}

public class DiscussionComment
{
    [JsonPropertyName("comment_text")]
    public string CommentText { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }
}
