using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DiffRapid.Ai;
using DiffRapid.Cli;

namespace DiffRapid.Core.Tests;

public sealed class AiTests
{
    private static readonly AiEndpoint Endpoint = new() { BaseUrl = "https://example.test/v1", Model = "test-model" };
    private static readonly AiPrompt Prompt = new("Explain the changes.", "-old\n+new\n");

    [Theory]
    [InlineData("https://example.test", "/v1/chat/completions")]
    [InlineData("https://example.test/", "/v1/chat/completions")]
    [InlineData("https://example.test/v1/", "/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "/v1/chat/completions")]
    [InlineData("https://example.test/proxy/v1", "/proxy/v1/chat/completions")]
    [InlineData("https://example.test/v1/chat/completions", "/v1/chat/completions")]
    public async Task OpenAiRequestUsesConfiguredUrlModelAndBearer(string address, string expectedPath)
    {
        using var client = new ChatClient(Endpoint with { BaseUrl = address, ApiKey = "test-credential" }, new FakeHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);
            Assert.Equal("", request.RequestUri.Query);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-credential"), request.Headers.Authorization);
            Assert.False(request.Headers.Contains("api-key"));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
            Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal(Prompt.SystemMessage, messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
            Assert.Equal(Prompt.UserMessage, messages[1].GetProperty("content").GetString());
            return Reply("A concise summary.");
        }));
        Assert.Equal("A concise summary.", await client.CompleteAsync(Prompt));
    }

    [Theory]
    [InlineData("https://example.test", "/openai/deployments/my%20deployment/chat/completions")]
    [InlineData("https://example.test/openai/", "/openai/deployments/my%20deployment/chat/completions")]
    [InlineData("https://example.test/openai/deployments/fixed/chat/completions", "/openai/deployments/fixed/chat/completions")]
    public async Task AzureRequestUsesDeploymentVersionAndApiKey(string address, string expectedPath)
    {
        var endpoint = Endpoint with
        {
            BaseUrl = address,
            Kind = AiEndpointKind.AzureOpenAi,
            Deployment = "my deployment",
            ApiVersion = "2024-10-21",
            ApiKey = "azure-test-credential",
        };
        using var client = new ChatClient(endpoint, new FakeHandler((request, _) =>
        {
            Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);
            Assert.Equal("?api-version=2024-10-21", request.RequestUri.Query);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("azure-test-credential", Assert.Single(request.Headers.GetValues("api-key")));
            return Task.FromResult(Reply("Azure response."));
        }));
        Assert.Equal("Azure response.", await client.CompleteAsync(Prompt));
    }

    [Fact]
    public async Task CompatibleLocalServerCanOmitAuthentication()
    {
        using var client = new ChatClient(Endpoint, new FakeHandler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("api-key"));
            return Task.FromResult(Reply("Local response."));
        }));
        Assert.Equal("Local response.", await client.CompleteAsync(Prompt));
    }

    [Fact]
    public async Task StreamingParsesCommentsMultilineEventsRoleUsageAndCrLf()
    {
        const string events = ": keepalive\r\nevent: message\r\ndata: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\r\n\r\n" +
            "data: {\"choices\":[\r\ndata: {\"delta\":{\"content\":\"hé\"}}]}\r\n\r\n" +
            "data:{\"choices\":[{\"delta\":{\"content\":\"llo\\n\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{}}\n\ndata: [DONE]\n\n";
        using var client = new ChatClient(Endpoint, new FakeHandler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
            return Events(events);
        }));
        var chunks = new List<string>();
        Assert.Equal("héllo\n", await client.StreamAsync(Prompt, chunks.Add));
        Assert.Equal(new[] { "hé", "llo\n" }, chunks);
    }

    [Fact]
    public async Task StreamingDeliversTextBeforeTheServerFinishes()
    {
        using var stream = new GatedStream("data: {\"choices\":[{\"delta\":{\"content\":\"first\"}}]}\n\n");
        using var client = new ChatClient(Endpoint, new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        })));
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = client.StreamAsync(Prompt, text => received.TrySetResult(text), cancellationToken: cancellation.Token);
        Assert.Equal("first", await received.Task.WaitAsync(cancellation.Token));
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"done\"}}]}\n\ndata: [DONE]")]
    public async Task StreamingFlushesFinalEventAtEof(string events)
    {
        using var client = ClientReturning(Events(events));
        Assert.Equal("done", await client.StreamAsync(Prompt, _ => { }));
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [InlineData("data: {not json}\n\n")]
    [InlineData("data: {\"error\":{\"message\":\"sensitive-server-detail\"}}\n\n")]
    [InlineData("data: []\n\n")]
    public async Task InvalidOrInterruptedStreamsFailClearly(string events)
    {
        using var client = ClientReturning(Events(events));
        var exception = await Assert.ThrowsAsync<AiException>(() => client.StreamAsync(Prompt, _ => { }));
        Assert.DoesNotContain("sensitive-server-detail", exception.ToString());
    }

    [Theory]
    [InlineData(401, "authentication")]
    [InlineData(403, "permissions")]
    [InlineData(404, "deployment")]
    [InlineData(429, "quota")]
    [InlineData(500, "HTTP 500")]
    [InlineData(302, "HTTP 302")]
    public async Task ErrorsMapStatusWithoutExposingResponseBody(int status, string expected)
    {
        using var client = ClientReturning(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("sensitive-server-detail") });
        var exception = await Assert.ThrowsAsync<AiException>(() => client.CompleteAsync(Prompt));
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("sensitive-server-detail", exception.ToString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":null}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}")]
    public async Task InvalidNonStreamingRepliesFailClearly(string body)
    {
        using var client = ClientReturning(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        await Assert.ThrowsAsync<AiException>(() => client.CompleteAsync(Prompt));
    }

    [Fact]
    public async Task TransportErrorsDoNotExposeCredentialsOrUrls()
    {
        using var client = new ChatClient(Endpoint, new FakeHandler((_, _) => throw new HttpRequestException("test-credential in private-url")));
        var exception = await Assert.ThrowsAsync<AiException>(() => client.CompleteAsync(Prompt));
        Assert.Contains("connection", exception.Message);
        Assert.DoesNotContain("test-credential", exception.ToString());
        Assert.DoesNotContain("private-url", exception.ToString());
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationRemainDistinct()
    {
        static async Task<HttpResponseMessage> Wait(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Reply("unreachable");
        }

        using var timedClient = new ChatClient(Endpoint with { Timeout = TimeSpan.FromMilliseconds(30) }, new FakeHandler(Wait));
        var exception = await Assert.ThrowsAsync<AiException>(() => timedClient.CompleteAsync(Prompt));
        Assert.Contains("timed out", exception.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelledClient = new ChatClient(Endpoint, new FakeHandler(Wait));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledClient.CompleteAsync(Prompt, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task StreamingReportsReasoningWithoutAddingItToTheReply()
    {
        const string events =
            "data: {\"choices\":[{\"delta\":{\"content\":\"\",\"reasoning\":\"Let me think\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" more\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\n";
        using var client = ClientReturning(Events(events));
        var thinking = 0;
        Assert.Equal("answer", await client.StreamAsync(Prompt, _ => { }, () => thinking++));
        Assert.Equal(2, thinking);
    }

    [Fact]
    public async Task StreamingTimeoutLimitsSilenceNotTheWholeAnswer()
    {
        // Six events 60 ms apart take longer than the 150 ms timeout, but no gap between them does.
        var chunks = Enumerable.Range(0, 6)
            .Select(i => $"data: {{\"choices\":[{{\"delta\":{{\"reasoning\":\"step {i}\"}}}}]}}\n\n")
            .Append("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n");
        using var stream = new DripStream(chunks, TimeSpan.FromMilliseconds(60));
        using var client = new ChatClient(Endpoint with { Timeout = TimeSpan.FromMilliseconds(150) }, new FakeHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        Assert.Equal("ok", await client.StreamAsync(Prompt, _ => { }));
    }

    // Returns one chunk per read, each after a delay, like a server streaming a slow model.
    private sealed class DripStream(IEnumerable<string> chunks, TimeSpan delay) : Stream
    {
        private readonly Queue<byte[]> _chunks = new(chunks.Select(Encoding.UTF8.GetBytes));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_chunks.Count == 0)
            {
                return 0;
            }

            await Task.Delay(delay, cancellationToken);
            var chunk = _chunks.Dequeue();
            Array.Copy(chunk, 0, buffer, offset, chunk.Length);
            return chunk.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task TimeoutCoversAStalledResponseBody()
    {
        using var stream = new GatedStream("");
        using var client = new ChatClient(Endpoint with { Timeout = TimeSpan.FromMilliseconds(30) }, new FakeHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        var exception = await Assert.ThrowsAsync<AiException>(() => client.StreamAsync(Prompt, _ => { }));
        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public void InvalidEndpointSettingsAndCredentialsAreRejectedSafely()
    {
        AiEndpoint[] invalid =
        [
            Endpoint with { BaseUrl = "" },
            Endpoint with { BaseUrl = "not-a-url" },
            Endpoint with { BaseUrl = "file:///tmp/api" },
            Endpoint with { BaseUrl = "https://user:test-credential@example.test" },
            Endpoint with { BaseUrl = "https://example.test?key=test-credential" },
            Endpoint with { Model = "" },
            Endpoint with { Kind = AiEndpointKind.AzureOpenAi },
            Endpoint with { Kind = AiEndpointKind.AzureOpenAi, Deployment = "deployment" },
            Endpoint with { Kind = (AiEndpointKind)123 },
            Endpoint with { Timeout = TimeSpan.Zero },
            Endpoint with { MaxInputTokens = 0 },
            Endpoint with { MaxInputTokens = int.MaxValue },
            Endpoint with { ApiKey = "test-credential\r\nheader: value" },
        ];
        foreach (var endpoint in invalid)
        {
            var exception = Assert.ThrowsAny<ArgumentException>(() => new ChatClient(endpoint));
            Assert.DoesNotContain("test-credential", exception.ToString());
        }

        Assert.DoesNotContain("test-credential", (Endpoint with { ApiKey = "test-credential" }).ToString());
    }

    [Fact]
    public async Task OversizedRawPromptIsRejectedBeforeSending()
    {
        using var client = new ChatClient(Endpoint with { MaxInputTokens = 10 }, new FakeHandler((_, _) => throw new InvalidOperationException("Must not send.")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteAsync(new AiPrompt("system", new string('x', 100))));
    }

    [Fact]
    public async Task RedactionCannotExpandInputPastTheConfiguredBudget()
    {
        using var client = new ChatClient(Endpoint with { MaxInputTokens = 10 }, new FakeHandler((_, _) => throw new InvalidOperationException("Must not send.")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteAsync(new AiPrompt("", "token=x token=y token=z token=w")));
    }

    [Fact]
    public void DiffPromptsKeepWholeHunksAndReportCuts()
    {
        var endpoint = Endpoint with { MaxInputTokens = 240 };
        var builder = new DiffPromptBuilder(endpoint);
        var hugeHunk = "@@ -1 +1 @@\n-" + new string('x', 1200) + "\n+new\n";
        const string smallHunk = "@@ -20 +20 @@\n-before\n+after\n";
        var prompt = builder.ExplainDiff("--- a.cs\r\n+++ b.cs\r\n" + hugeHunk + smallHunk, "a.cs", "b.cs", "C#");
        Assert.Contains("risky changes first", prompt.SystemMessage);
        Assert.Contains("Language: C#", prompt.UserMessage);
        Assert.Contains(smallHunk, prompt.UserMessage);
        Assert.DoesNotContain("@@ -1 +1 @@", prompt.UserMessage);
        Assert.Contains("1 whole hunk(s)", prompt.UserMessage);
        Assert.True(prompt.SystemMessage.Length + prompt.UserMessage.Length <= endpoint.MaxInputTokens * 4);
    }

    [Fact]
    public void FolderPromptsRespectTopNAndBudget()
    {
        var endpoint = Endpoint with { MaxInputTokens = 300 };
        var builder = new DiffPromptBuilder(endpoint);
        AiFileDiff[] files = [new("a", "b", "@@ -1 +1 @@\n-old\n+new\n"), new("c", "d", "second-file-diff")];
        var prompt = builder.ExplainFolder("Two files changed.\n" + new string('x', 1500), files, topN: 1);
        Assert.Contains("Two files changed.", prompt.UserMessage);
        Assert.Contains("@@ -1 +1 @@\n-old\n+new", prompt.UserMessage);
        Assert.DoesNotContain("second-file-diff", prompt.UserMessage);
        Assert.Contains("1 file diff(s) omitted", prompt.UserMessage);
        Assert.True(prompt.SystemMessage.Length + prompt.UserMessage.Length <= endpoint.MaxInputTokens * 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.ExplainFolder("", files, -1));
    }

    [Fact]
    public void ResolutionPromptsContainAllVersionsAndBoundLongContext()
    {
        var endpoint = Endpoint with { MaxInputTokens = 220 };
        var builder = new DiffPromptBuilder(endpoint);
        var prompt = builder.ProposeResolution("original", "left", "right", new string('x', 5000));
        Assert.Contains("one line of reasoning", prompt.SystemMessage);
        Assert.Contains("Left version:\nleft", prompt.UserMessage);
        Assert.Contains("Right version:\nright", prompt.UserMessage);
        Assert.Contains("Base version:\noriginal", prompt.UserMessage);
        Assert.Contains("Input trimmed", prompt.UserMessage);
        Assert.True(prompt.SystemMessage.Length + prompt.UserMessage.Length <= endpoint.MaxInputTokens * 4);
        Assert.Contains("unavailable", builder.ProposeResolution(null, "left", "right", "context").UserMessage);
        Assert.Throws<ArgumentException>(() => new DiffPromptBuilder(endpoint with { MaxInputTokens = 1 }).ExplainDiff("", "a", "b"));
    }

    [Fact]
    public void PromptBuildingAndParsingHonorCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var builder = new DiffPromptBuilder(Endpoint);
        Assert.ThrowsAny<OperationCanceledException>(() => builder.ExplainDiff("diff", "a", "b", cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => builder.ExplainFolder("summary", [], cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => builder.ProposeResolution(null, "left", "right", "context", cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ResolutionParser.Parse("```\nline\n```\nreason", cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SecretRedactor.Redact("password=value", cancellation.Token));
    }

    [Theory]
    [InlineData("```\n  resolved\n\n```\nKeep both changes.")]
    [InlineData("```csharp\r\n  resolved\r\n\r\n```\r\nKeep both changes.\r\n")]
    [InlineData("~~~~text\n  resolved\n\n~~~~\nKeep both changes.")]
    [InlineData("Here is a proposal.\n```text\n  resolved\n\n```\nKeep both changes.")]
    public void ResolutionParserPreservesLinesAndAcceptsLanguageTags(string reply)
    {
        var resolution = ResolutionParser.Parse(reply);
        Assert.Equal(new[] { "  resolved", "" }, resolution.Lines);
        Assert.Equal("Keep both changes.", resolution.Reasoning);
    }

    [Theory]
    [InlineData("no fence")]
    [InlineData("```cs\nunterminated")]
    [InlineData("```\na\n```\nReason\n```\nb\n```\nReason")]
    [InlineData("```\na\n```\n")]
    [InlineData("```\na\n```\nReason\nAnother reason")]
    [InlineData("inline ```a``` reason")]
    public void ResolutionParserRejectsMissingAmbiguousOrMalformedBlocks(string reply)
    {
        Assert.Throws<FormatException>(() => ResolutionParser.Parse(reply));
    }

    [Fact]
    public void EmptyFencedBlockCanProposeDeletingTheConflict()
    {
        var resolution = ResolutionParser.Parse("```\n```\nDelete the obsolete lines.");
        Assert.Empty(resolution.Lines);
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("PASSWORD = 'two words'", "two words")]
    [InlineData("secret=hidden", "hidden")]
    [InlineData("apikey=keyvalue", "keyvalue")]
    [InlineData("api_key=keyvalue", "keyvalue")]
    [InlineData("\"apiKey\": \"keyvalue\"", "keyvalue")]
    [InlineData("token=abc.def.ghi", "abc.def.ghi")]
    [InlineData("access_token=accessvalue", "accessvalue")]
    [InlineData("client-secret=clientvalue", "clientvalue")]
    [InlineData("Authorization: Bearer abc.def/ghi+=", "abc.def/ghi+=")]
    [InlineData("Server=internal;Database=private;User Id=bob;Password=pass;", "internal")]
    [InlineData("ConnectionString=\"Server=host;Password=pass\"", "host")]
    [InlineData("Server=host;Password='quoted-secret';Database=example", "quoted-secret")]
    [InlineData("User Id=private-user;Password='secret';Server=internal", "private-user")]
    [InlineData("postgres://bob:pass@internal/db", "pass")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\r\nkeymaterial\r\n-----END RSA PRIVATE KEY-----", "keymaterial")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nkeymaterial", "keymaterial")]
    public void ObviousSecretsAreMasked(string text, string secret)
    {
        var result = SecretRedactor.Redact(text);
        Assert.DoesNotContain(secret, result);
        Assert.Contains("[REDACTED", result);
    }

    [Theory]
    [InlineData(
        "+    // Discount over 10 items; connection=Server=db;Password=hunter2",
        "+    // Discount over 10 items; connection=[REDACTED CONNECTION STRING]")]
    [InlineData(
        "    var c = \"Server=db;Database=app\";",
        "    var c = \"[REDACTED CONNECTION STRING]\";")]
    [InlineData(
        "- x = 1; // User Id=bob;Server=internal",
        "- x = 1; // [REDACTED CONNECTION STRING]")]
    public void ConnectionStringMaskKeepsTheRestOfTheLine(string text, string expected)
    {
        Assert.Equal(expected, SecretRedactor.Redact(text));
    }

    [Fact]
    public void RedactionPreservesOrdinaryCodeAndText()
    {
        const string text = "public int Count => 3;\n// compare two files\n";
        Assert.Equal(text, SecretRedactor.Redact(text));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientRedactsBothMessagesUnlessCallerOptsOut(bool redact)
    {
        using var client = new ChatClient(Endpoint, new FakeHandler(async (request, _) =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Equal(!redact, body.Contains("hidden-system-value", StringComparison.Ordinal));
            Assert.Equal(!redact, body.Contains("hidden-user-value", StringComparison.Ordinal));
            return Reply("Summary");
        }));
        await client.CompleteAsync(new AiPrompt("secret=hidden-system-value", "token=hidden-user-value"), redact);
    }

    [Theory]
    [InlineData("explain")]
    [InlineData("explain", "missing-left", "missing-right")]
    [InlineData("explain", "--conflicts", "missing")]
    [InlineData("explain", "--endpoint")]
    [InlineData("explain", "--endpoint=https://example.test", "--model")]
    [InlineData("explain", "--api-key=never-print-this")]
    public void CliReturnsExitTwoForMissingConfigurationOrInvalidOptions(params string[] arguments)
    {
        Assert.Equal(2, Program.Main(arguments));
    }

    [Fact]
    public async Task CliMissingEndpointDoesNotReadFilesOrSendAnything()
    {
        using var output = new StringWriter();
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Must not send."));
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => Program.RunExplainAsync(["explain", "missing-left", "missing-right"], output, handler));
        Assert.Contains("No AI endpoint configured", exception.Message);
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliExplainsDiffWithEnvironmentCredentialAndOptionalRedaction(bool noRedact)
    {
        using var files = new AiFiles();
        var left = files.Write("left.txt", "password=old-secret\n");
        var right = files.Write("right.txt", "password=new-secret\n");
        var variable = "DIFFRAPID_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-credential");
        try
        {
            var arguments = new List<string> { "explain", left, right, "--endpoint", Endpoint.BaseUrl, "--model=test-model", "--api-key-env", variable };
            if (noRedact)
            {
                arguments.Add("--no-redact");
            }

            using var output = new StringWriter();
            var calls = 0;
            var handler = new FakeHandler(async (request, _) =>
            {
                calls++;
                Assert.Equal("test-credential", request.Headers.Authorization!.Parameter);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var content = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                Assert.Contains("@@", content);
                Assert.Equal(noRedact, content.Contains("old-secret", StringComparison.Ordinal));
                Assert.Equal(noRedact, content.Contains("new-secret", StringComparison.Ordinal));
                return Reply("Changed the password. test-credential");
            });
            Assert.Equal(0, await Program.RunExplainAsync(arguments.ToArray(), output, handler));
            Assert.Equal(1, calls);
            Assert.Contains("Changed the password.", output.ToString());
            Assert.DoesNotContain("test-credential", output.ToString());
            Assert.Equal("password=old-secret\n", File.ReadAllText(left));
            Assert.Equal("password=new-secret\n", File.ReadAllText(right));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliProposesEachConflictWithoutWritingAndSupportsAzure(bool azure)
    {
        using var files = new AiFiles();
        const string text = "head\r\n<<<<<<< ours\r\nleft-one\r\n||||||| base\r\nbase-one\r\n=======\r\nright-one\r\n>>>>>>> theirs\r\nbetween\r\n<<<<<<< ours\r\nleft-two\r\n=======\r\nright-two\r\n>>>>>>> theirs\r\ntail\r\n";
        var path = files.Write("conflicts.txt", text);
        var calls = 0;
        var handler = new FakeHandler(async (request, _) =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var content = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.Contains(calls == 1 ? "base-one" : "Base version: unavailable", content);
            Assert.Contains(calls == 1 ? "head" : "tail", content);
            Assert.Contains(calls == 1 ? "left-one" : "left-two", content);
            Assert.DoesNotContain(calls == 1 ? "left-two" : "right-one", content);
            Assert.Equal(azure, request.RequestUri!.Query.Contains("api-version", StringComparison.Ordinal));
            return Reply("```text\nresolved\n```\nCombines the intent.");
        });
        string[] arguments = azure
            ? ["explain", "--conflicts", path, "--endpoint", "https://example.test", "--azure-deployment", "deployment", "--api-version", "2024-10-21"]
            : ["explain", "--conflicts", path, "--endpoint", Endpoint.BaseUrl, "--model", Endpoint.Model];
        using var output = new StringWriter();
        Assert.Equal(0, await Program.RunExplainAsync(arguments, output, handler));
        Assert.Equal(2, calls);
        Assert.Contains("Conflict 1", output.ToString());
        Assert.Contains("Conflict 2", output.ToString());
        Assert.Contains("resolved", output.ToString());
        Assert.Equal(text, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(files.DirectoryPath));
    }

    [Theory]
    [InlineData("plain file", false)]
    [InlineData("<<<<<<< ours\nleft\n=======\nright", true)]
    [InlineData("=======\n", true)]
    [InlineData("<<<<<<< ours\nleft\n=======\nright\n>>>>>>> theirs\n<<<<<<< broken", true)]
    public async Task CliDoesNotSendPlainOrMalformedConflictFiles(string text, bool malformed)
    {
        using var files = new AiFiles();
        var path = files.Write("input.txt", text);
        using var output = new StringWriter();
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Must not send."));
        var pending = Program.RunExplainAsync(["explain", "--conflicts", path, "--endpoint", Endpoint.BaseUrl, "--model", Endpoint.Model], output, handler);
        if (malformed)
        {
            await Assert.ThrowsAsync<FormatException>(() => pending);
        }
        else
        {
            Assert.Equal(0, await pending);
            Assert.Contains("No conflict markers", output.ToString());
        }

        Assert.Equal(text, File.ReadAllText(path));
    }

    private static ChatClient ClientReturning(HttpResponseMessage response) => new(Endpoint, new FakeHandler((_, _) => Task.FromResult(response)));

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Events(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/event-stream"),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class GatedStream(string initial) : Stream
    {
        private readonly MemoryStream _initial = new(Encoding.UTF8.GetBytes(initial));
        private readonly CancellationTokenSource _closed = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = _initial.Read(buffer, offset, count);
            if (read > 0)
            {
                return read;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closed.Cancel();
            }

            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AiFiles : IDisposable
    {
        public AiFiles()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "DiffRapid-Ai-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string Write(string name, string content)
        {
            var path = Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
