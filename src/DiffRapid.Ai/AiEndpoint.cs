using System;

namespace DiffRapid.Ai;

/// <summary>The chat completions protocol used by an endpoint.</summary>
public enum AiEndpointKind
{
    /// <summary>OpenAI and compatible servers using bearer authentication.</summary>
    OpenAi,

    /// <summary>Azure OpenAI deployments using an api-key header.</summary>
    AzureOpenAi,
}

/// <summary>User-supplied settings; no endpoint or model is configured by default.</summary>
public sealed record AiEndpoint
{
    /// <summary>The server root, API base path, or full chat completions URL.</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>The model name understood by the server.</summary>
    public string Model { get; init; } = "";

    /// <summary>An optional credential, excluded from the record's textual representation.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The server's URL and authentication convention.</summary>
    public AiEndpointKind Kind { get; init; }

    /// <summary>The deployment name required for Azure OpenAI.</summary>
    public string? Deployment { get; init; }

    /// <summary>The API version required for Azure OpenAI.</summary>
    public string? ApiVersion { get; init; }

    /// <summary>The maximum duration of the entire request, including streamed content.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The approximate input budget, estimated at four characters per token.</summary>
    public int MaxInputTokens { get; init; } = 8192;

    /// <summary>Returns settings that contain neither credentials nor a potentially sensitive URL.</summary>
    public override string ToString() => $"AiEndpoint {{ Kind = {Kind}, MaxInputTokens = {MaxInputTokens} }}";

    internal int InputCharacterLimit()
    {
        if (MaxInputTokens <= 0 || MaxInputTokens > int.MaxValue / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInputTokens), "MaxInputTokens must be positive and fit a four-character estimate.");
        }

        return MaxInputTokens * 4;
    }
}
