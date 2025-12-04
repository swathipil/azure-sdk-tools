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
