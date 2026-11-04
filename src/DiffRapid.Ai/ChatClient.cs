using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiffRapid.Ai;

/// <summary>Sends explicit chat requests to a configured endpoint, with redaction enabled by default.</summary>
public sealed class ChatClient : IDisposable
{
    private readonly AiEndpoint _endpoint;
    private readonly HttpClient _client;
    private readonly Uri _requestUri;

    /// <summary>Creates a client that owns the supplied handler, or a handler with redirects disabled.</summary>
    public ChatClient(AiEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _requestUri = BuildRequestUri(endpoint);
        endpoint.InputCharacterLimit();
        if (endpoint.Timeout <= TimeSpan.Zero || endpoint.Timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentException("The AI timeout must be positive and at most Int32.MaxValue milliseconds.");
        }

        if (string.IsNullOrWhiteSpace(endpoint.Model))
        {
            throw new ArgumentException("An AI model must be configured.");
        }

        if (endpoint.ApiKey is not null)
        {
            foreach (var character in endpoint.ApiKey)
            {
                if (character <= ' ' || character > '~')
                {
                    throw new ArgumentException("The configured AI key contains invalid HTTP header characters.");
                }
            }
        }

        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Returns a complete reply; no files or caller data are modified.</summary>
    public Task<string> CompleteAsync(AiPrompt prompt, bool redactSecrets = true, CancellationToken cancellationToken = default)
    {
        return SendAsync(prompt, null, redactSecrets, cancellationToken);
    }

    /// <summary>Delivers SSE text deltas as they arrive and returns the accumulated reply.</summary>
    public Task<string> StreamAsync(AiPrompt prompt, Action<string> onContent, bool redactSecrets = true,
        CancellationToken cancellationToken = default)
    {
        if (onContent is null)
        {
            throw new ArgumentNullException(nameof(onContent));
        }

        return SendAsync(prompt, onContent, redactSecrets, cancellationToken);
    }

    /// <summary>Releases the HTTP client and its handler.</summary>
    public void Dispose() => _client.Dispose();

    private async Task<string> SendAsync(AiPrompt prompt, Action<string>? onContent, bool redactSecrets,
        CancellationToken cancellationToken)
    {
        ValidatePrompt(prompt);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_endpoint.Timeout);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            using var request = CreateRequest(prompt, onContent is not null, redactSecrets, timeout.Token);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            // netstandard2.0's StreamReader has no cancellable ReadLineAsync overload.
            using var registration = timeout.Token.Register(stream.Dispose);
            var result = onContent is null
                ? await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false)
                : await ReadEventsAsync(stream, onContent, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception exception) when (timeout.IsCancellationRequested &&
            exception is OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AiException("The AI request timed out. Increase the configured timeout or try again.");
        }
        catch (HttpRequestException)
        {
            throw new AiException("Unable to reach the configured AI endpoint. Check the address and connection.");
        }
        catch (JsonException)
        {
            throw new AiException("The AI endpoint returned invalid chat completion JSON.");
        }
        catch (IOException exception) when (exception is not AiException)
        {
            throw new AiException("The AI response could not be read. Check the connection and try again.");
        }
    }

    private void ValidatePrompt(AiPrompt prompt)
    {
        if (prompt is null || prompt.SystemMessage is null || prompt.UserMessage is null)
        {
            throw new ArgumentNullException(nameof(prompt));
        }

        if ((long)prompt.SystemMessage.Length + prompt.UserMessage.Length > _endpoint.InputCharacterLimit())
        {
            throw new ArgumentException("AI input exceeds MaxInputTokens. Use DiffPromptBuilder to trim it before sending.");
        }
    }

    private HttpRequestMessage CreateRequest(AiPrompt prompt, bool stream, bool redactSecrets, CancellationToken cancellationToken)
    {
        var system = redactSecrets ? SecretRedactor.Redact(prompt.SystemMessage, cancellationToken) : prompt.SystemMessage;
        var user = redactSecrets ? SecretRedactor.Redact(prompt.UserMessage, cancellationToken) : prompt.UserMessage;
        if ((long)system.Length + user.Length > _endpoint.InputCharacterLimit())
        {
            throw new ArgumentException("Redacted AI input exceeds MaxInputTokens. Reduce the input or increase the budget before sending.");
        }

        var body = JsonSerializer.Serialize(new
        {
            model = _endpoint.Model,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            stream,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, _requestUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        if (!string.IsNullOrEmpty(_endpoint.ApiKey))
        {
            if (_endpoint.Kind == AiEndpointKind.AzureOpenAi)
            {
                request.Headers.Add("api-key", _endpoint.ApiKey);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);
            }
        }

        return request;
    }

    private static Uri BuildRequestUri(AiEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
        {
            throw new ArgumentException("No AI endpoint configured. Supply an endpoint explicitly before sending any content.");
        }

        if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != "http" && baseUri.Scheme != "https") ||
            baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0)
        {
            throw new ArgumentException("The AI endpoint must be an absolute HTTP(S) URL without credentials, a query, or a fragment.");
        }

        var address = baseUri.AbsoluteUri.TrimEnd('/');
        if (endpoint.Kind == AiEndpointKind.AzureOpenAi)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Deployment) || string.IsNullOrWhiteSpace(endpoint.ApiVersion))
            {
                throw new ArgumentException("Azure OpenAI requires a deployment and an API version.");
            }

            if (!address.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                address += address.EndsWith("/openai", StringComparison.OrdinalIgnoreCase) ? "" : "/openai";
                address += "/deployments/" + Uri.EscapeDataString(endpoint.Deployment!) + "/chat/completions";
            }

            address += "?api-version=" + Uri.EscapeDataString(endpoint.ApiVersion!);
        }
        else if (endpoint.Kind == AiEndpointKind.OpenAi)
        {
            if (!address.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                address += baseUri.AbsolutePath == "/" ? "/v1/chat/completions" : "/chat/completions";
            }
        }
        else
        {
            throw new ArgumentException("Unsupported AI endpoint kind.");
        }

        return new Uri(address);
    }

    private static void EnsureSuccess(HttpStatusCode statusCode)
    {
        if ((int)statusCode >= 200 && (int)statusCode < 300)
        {
            return;
        }

        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => "AI endpoint rejected authentication (401). Check the configured API key.",
            HttpStatusCode.Forbidden => "AI endpoint denied access (403). Check the key's permissions.",
            HttpStatusCode.NotFound => "AI endpoint or deployment was not found (404). Check the URL, deployment, and API version.",
            (HttpStatusCode)429 => "AI endpoint rate limit or quota exceeded (429). Wait before retrying and check the quota.",
            _ => $"AI endpoint returned HTTP {(int)statusCode}. Check the endpoint configuration or try again later.",
        };
        throw new AiException(message, statusCode);
    }

    private static async Task<string> ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (TryChoice(root, out var choice) && choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()!;
        }

        throw new AiException("The AI endpoint returned no text completion.");
    }

    private static async Task<string> ReadEventsAsync(Stream stream, Action<string> onContent, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var eventData = new StringBuilder();
        var output = new StringBuilder();
        var finished = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                if (eventData.Length > 0)
                {
                    var data = eventData.ToString().TrimEnd('\n');
                    eventData.Clear();
                    if (data == "[DONE]")
                    {
                        return output.ToString();
                    }

                    finished |= ReadEvent(data, output, onContent);
                }

                if (line is null)
                {
                    break;
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var start = line.Length > 5 && line[5] == ' ' ? 6 : 5;
                eventData.Append(line, start, line.Length - start).Append('\n');
            }
        }

        if (!finished)
        {
            throw new AiException("The AI stream ended before completion. The reply may be incomplete.");
        }

        return output.ToString();
    }

    private static bool ReadEvent(string data, StringBuilder output, Action<string> onContent)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
        {
            throw new AiException("The AI endpoint reported an invalid or failed stream event.");
        }

        if (!TryChoice(root, out var choice))
        {
            return false;
        }

        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
            delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString()!;
            output.Append(text);
            onContent(text);
        }

        return choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String;
    }

    private static bool TryChoice(JsonElement root, out JsonElement choice)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object)
        {
            choice = choices[0];
            return true;
        }

        choice = default;
        return false;
    }
}
