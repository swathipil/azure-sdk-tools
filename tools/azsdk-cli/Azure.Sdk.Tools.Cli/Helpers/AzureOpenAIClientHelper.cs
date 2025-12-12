// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.AI.OpenAI;
using Azure.Core;
using OpenAI;
using System.ClientModel;

namespace Azure.Sdk.Tools.Cli.Helpers;

/// <summary>
/// Helper class to create OpenAI clients configured for Azure endpoints with authentication
/// </summary>
public static class AzureOpenAIClientHelper
{
    /// <summary>
    /// Creates an OpenAI client configured for Azure OpenAI with API key or TokenCredential (Entra ID) authentication
    /// </summary>
    /// <param name="endpoint">Azure OpenAI endpoint</param>
    /// <param name="credential">Azure TokenCredential for Entra ID authentication (used if no API key is available)</param>
    /// <returns>Configured OpenAIClient instance</returns>
    public static OpenAIClient CreateAzureOpenAIClient(Uri endpoint, TokenCredential credential)
    {
        // Check for API key from environment variable first
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Use API key authentication with Azure OpenAI
            return new AzureOpenAIClient(endpoint, new ApiKeyCredential(apiKey));
        }

        // Fall back to Entra ID authentication (DefaultAzureCredential)
        return new AzureOpenAIClient(endpoint, credential);
    }

    /// <summary>
    /// Creates an OpenAI client configured for Anthropic Claude models deployed on Azure AI Foundry (formerly Azure Foundry)
    /// as serverless API deployments
    /// </summary>
    /// <param name="endpoint">Azure AI Foundry serverless API endpoint for Claude model</param>
    /// <param name="credential">Azure TokenCredential for Entra ID authentication (used if no API key is available)</param>
    /// <returns>Configured OpenAIClient instance for Claude model</returns>
    /// <remarks>
    /// Anthropic Claude models can be deployed as serverless APIs on Azure AI Foundry via Azure Marketplace.
    /// The endpoint URL format is typically: https://[deployment-name].[region].models.ai.azure.com/
    /// You can use ANTHROPIC_API_KEY or AZURE_API_KEY environment variables for authentication.
    /// For more information, see: https://learn.microsoft.com/azure/ai-studio/how-to/deploy-models-serverless
    /// </remarks>
    public static OpenAIClient CreateAnthropicClaudeClient(Uri endpoint, TokenCredential credential)
    {
        // Check for Anthropic API key or Azure API key from environment variables
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") 
                     ?? Environment.GetEnvironmentVariable("AZURE_API_KEY");
        
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Use API key authentication
            // Claude models deployed as serverless APIs on Azure use the Azure OpenAI client with API key
            return new AzureOpenAIClient(endpoint, new ApiKeyCredential(apiKey));
        }

        // Fall back to Entra ID authentication (DefaultAzureCredential)
        return new AzureOpenAIClient(endpoint, credential);
    }
}
