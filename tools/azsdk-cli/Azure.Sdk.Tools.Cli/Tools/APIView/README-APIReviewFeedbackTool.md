# API Review Feedback Tool

## Overview

The API Review Feedback Tool (`azsdk_apiview_apply_feedback`) automates the process of applying API review feedback from APIView to TypeSpec specifications. It fetches comments from APIView, parses them for applicable feedback, generates TypeSpec customizations following Azure guidelines, and updates the `client.tsp` file.

## Features

- **Automated Comment Retrieval**: Fetches comments directly from APIView using the review URL
- **Smart Filtering**: Identifies comments applicable to TypeSpec customizations (rename, access, etc.)
- **TypeSpec Generation**: Generates proper `client.tsp` customizations following [Azure TypeSpec client customization guidelines](https://github.com/Azure/azure-rest-api-specs/blob/main/eng/common/knowledge/customizing-client-tsp.md)
- **Validation**: Runs TypeSpec compilation to verify changes don't break the specification
- **Git Integration**: Tracks current branch and provides git workflow guidance
- **Dry Run Mode**: Preview changes before applying them

## Usage

### MCP Tool (from Copilot)

```typescript
azsdk_apiview_apply_feedback(
  apiViewUrl: "https://apiview.dev/review/{reviewId}?activeApiRevisionId={revisionId}",
  typeSpecProjectPath: "/path/to/specification/service/ServiceName",
  language: "python",
  targetBranch: "my-feature-branch",  // optional
  dryRun: false  // optional, default false
)
```

### CLI Command

```bash
azsdk apiview apply-feedback \
  "https://apiview.dev/review/{reviewId}?activeApiRevisionId={revisionId}" \
  "/path/to/specification/service/ServiceName" \
  --language python \
  --branch my-feature-branch \
  --dry-run
```

## Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `apiViewUrl` | string | Yes | Full APIView review URL including both reviewId and activeApiRevisionId |
| `typeSpecProjectPath` | string | Yes | Absolute path to TypeSpec project root (containing tspconfig.yaml) |
| `language` | string | Yes | Target language: python, csharp, java, javascript, go, rust |
| `targetBranch` | string | No | Branch to work in (for future PR creation) |
| `dryRun` | boolean | No | Preview changes without applying (default: false) |

## Response

```json
{
  "typespec_project": "/path/to/project",
  "package_type": "Dataplane",
  "message": "API review feedback applied successfully",
  "comments_processed": 15,
  "comments_applied": 8,
  "comments_skipped": 7,
  "client_tsp_path": "/path/to/project/client.tsp",
  "validation_passed": true,
  "branch_name": "main",
  "changes_summary": [
    "Added @@clientName decorator for operation...",
    "Added @@access decorator for internal operation..."
  ],
  "next_steps": [
    "Review changes in client.tsp",
    "Run 'git diff' to see all modifications",
    "Stage changes: git add client.tsp",
    "Commit changes: git commit -m \"Apply API review feedback\"",
    "Push changes: git push origin main"
  ]
}
```

## Workflow

The tool follows this process:

1. **Validate Inputs**
   - Verify APIView URL format
   - Check TypeSpec project exists and is valid
   - Validate language parameter

2. **Fetch Comments**
   - Extract revision ID from APIView URL
   - Call APIView API to retrieve all comments

3. **Filter Comments**
   - Parse comment text for spec-applicable keywords
   - Keywords: "rename", "clientname", "access", "@@clientName", "@@access", etc.
   - Filter by relevance to TypeSpec customizations

4. **Generate Customizations**
   - Parse each applicable comment
   - Generate appropriate TypeSpec decorators
   - Follow naming conventions (camelCase for TypeScript)
   - Apply language-specific scopes when needed

5. **Apply Changes**
   - Create `client.tsp` if it doesn't exist
   - Append customizations with proper imports
   - Preserve existing content

6. **Validate**
   - Run `npx tsp compile .` in project directory
   - Report compilation errors if any

7. **Provide Guidance**
   - Generate next steps based on validation results
   - Include git commands for review and commit

## Comment Filtering Criteria

The tool identifies comments applicable to spec modifications by looking for:

- **Renaming keywords**: "rename", "clientname", "client name"
- **Access keywords**: "access", "internal", "public"
- **Decorator references**: "@@clientName", "@@access", "@@clientLocation"
- **File references**: "client.tsp", "customization"

Comments without these keywords are skipped as they likely apply to SDK implementation rather than specs.

## Client.tsp Structure

If `client.tsp` doesn't exist, the tool creates it with:

```typescript
import "./main.tsp";
import "@azure-tools/typespec-client-generator-core";

using Azure.ClientGenerator.Core;

// Client customizations for API review feedback
```

Customizations are appended under a clear section header.

## Error Handling

The tool handles these error scenarios:

- **Invalid APIView URL**: Returns error with format requirements
- **Invalid TypeSpec project**: Checks for tspconfig.yaml and main.tsp
- **Invalid language**: Validates against supported languages list
- **APIView API failure**: Reports when comments can't be fetched
- **Validation failure**: Continues but reports errors in response

## Development Status

### Current Implementation

✅ Basic tool structure and MCP integration
✅ APIView comment retrieval
✅ Input validation
✅ Basic comment filtering
✅ client.tsp file creation and updating
✅ TypeSpec validation integration
✅ Git branch detection
✅ Comprehensive response with next steps

### TODO

⚠️ **Comment Parsing Enhancement**: Current implementation uses placeholder logic for parsing comments into specific TypeSpec decorators. Needs:
  - Pattern matching for rename suggestions → `@@clientName(target, "newName", "language")`
  - Access level detection → `@@access(target, Access.internal, "language")`
  - Element identification (operation names, model names, property names)
  - Proper conversion to camelCase/PascalCase per TypeSpec conventions

⚠️ **Scope Detection**: Determine when to apply language-specific scopes vs. all languages

⚠️ **PR Integration**: Extract PR information from APIView or add as parameter for commit/push automation

⚠️ **LLM Integration**: Consider using AI to parse natural language comments into structured customizations

## Examples

### Example 1: Python Rename

**APIView Comment**: "For Python, please rename the operation `get_user_info` to `get_user_information`"

**Generated Customization**:
```typescript
@@clientName(MyService.getUserInfo, "getUserInformation", "python")
```

### Example 2: Make Internal

**APIView Comment**: "This operation should be internal for all SDKs"

**Generated Customization**:
```typescript
@@access(MyService.internalOperation, Access.internal)
```

### Example 3: Language-Specific Access

**APIView Comment**: "Make this internal for C# only"

**Generated Customization**:
```typescript
@@access(MyService.operation, Access.internal, "csharp")
```

## Integration with Copilot Chat Mode

This tool is designed to work with the `api-review-feedback` chat mode in azure-rest-api-specs. The chat mode:

1. Guides users through gathering APIView URL and TypeSpec path
2. Calls this MCP tool to apply feedback
3. Helps resolve any validation errors
4. Assists with git workflow and PR creation

## Testing

Test the tool with:

```bash
# Dry run to preview changes
azsdk apiview apply-feedback \
  "https://apiview.dev/review/test?activeApiRevisionId=123" \
  "/path/to/typespec/project" \
  --language python \
  --dry-run

# Apply changes
azsdk apiview apply-feedback \
  "https://apiview.dev/review/test?activeApiRevisionId=123" \
  "/path/to/typespec/project" \
  --language python
```

## Contributing

To enhance the comment parsing logic:

1. Add pattern matching in `ParseCommentToCustomization()`
2. Update filtering criteria in `FilterApplicableComments()`
3. Add new `CustomizationType` enum values as needed
4. Update tests with new patterns

## Related Documentation

- [TypeSpec Client Customizations Reference](https://github.com/Azure/azure-rest-api-specs/blob/main/eng/common/knowledge/customizing-client-tsp.md)
- [APIView Documentation](https://apiview.dev)
- [TypeSpec Documentation](https://typespec.io)
- [Azure TypeSpec Guidelines](https://azure.github.io/typespec-azure/)
