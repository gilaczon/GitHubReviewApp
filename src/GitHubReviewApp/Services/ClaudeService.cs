namespace GitHubReviewApp.Services;

public class ClaudeService : IClaudeService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<ClaudeService> _logger;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-sonnet-4-6";
    private const string ApiKeySecretName = "AnthropicApiKey";
    private const int MaxTokens = 4096;

    private static readonly string _systemPrompt = """
        You are an expert code reviewer. Review the pull request diff provided and give clear, actionable feedback.

        Focus on:
        - Bugs and correctness issues
        - Security vulnerabilities (OWASP Top 10, injection, auth flaws)
        - Performance concerns
        - Code quality and maintainability
        - Missing error handling at system boundaries

        Rules:
        - Be specific — reference file names and line numbers where relevant.
        - Be concise — skip obvious praise; highlight what actually matters.
        - If the change looks good overall, say so briefly before any minor notes.
        - Format your response as GitHub-flavoured Markdown.
        - Do not repeat the diff back.
        """;

    public ClaudeService(HttpClient httpClient, IConfiguration config, ILogger<ClaudeService> logger)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.anthropic.com");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _config = config;
        _logger = logger;
    }

    public async Task<string> ReviewDiffAsync(string prTitle, string diff)
    {
        using var activity = ActivitySources.ClaudeService.StartActivity("chat", ActivityKind.Client);
        activity?.SetTag("gen_ai.system",             "anthropic");
        activity?.SetTag("gen_ai.operation.name",     "chat");
        activity?.SetTag("gen_ai.request.model",      Model);
        activity?.SetTag("gen_ai.request.max_tokens", MaxTokens);

        var apiKey = _config[ApiKeySecretName]
            ?? throw new InvalidOperationException($"Secret '{ApiKeySecretName}' is not configured.");

        var userMessage = $"## PR: {prTitle}\n\n```diff\n{diff}\n```";

        var requestBody = new ClaudeRequest
        {
            Model = Model,
            MaxTokens = MaxTokens,
            System = _systemPrompt,
            Messages = [new ClaudeMessage { Role = "user", Content = userMessage }]
        };

        var json = JsonSerializer.Serialize(requestBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Calling Claude API for review.");
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>()
            ?? throw new InvalidOperationException("Empty Claude API response.");

        if (result.Usage is { } usage)
        {
            activity?.SetTag("gen_ai.usage.input_tokens",  usage.InputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
            AppMeters.InputTokens.Add(usage.InputTokens);
            AppMeters.OutputTokens.Add(usage.OutputTokens);
        }

        return result.Content.FirstOrDefault()?.Text
            ?? throw new InvalidOperationException("No text content in Claude response.");
    }

    // ── Minimal Claude API request/response models ──────────────────────────

    private class ClaudeRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; }

        [JsonPropertyName("system")]
        public string System { get; init; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<ClaudeMessage> Messages { get; init; } = [];
    }

    private class ClaudeMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;
    }

    private class ClaudeResponse
    {
        [JsonPropertyName("content")]
        public List<ClaudeContent> Content { get; init; } = [];

        [JsonPropertyName("usage")]
        public ClaudeUsage? Usage { get; init; }
    }

    private class ClaudeContent
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = string.Empty;
    }

    private class ClaudeUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; init; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; init; }
    }
}
