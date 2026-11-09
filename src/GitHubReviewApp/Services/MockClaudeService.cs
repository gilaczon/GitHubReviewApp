namespace GitHubReviewApp.Services;

/// <summary>
/// Simulates Claude API responses for local testing without making real API calls.
/// Registered in place of <see cref="ClaudeService"/> in the Development environment.
/// </summary>
public class MockClaudeService : IClaudeService
{
    private static readonly TimeSpan _defaultLatency = TimeSpan.FromSeconds(1);

    private readonly ILogger<MockClaudeService> _logger;
    private readonly TimeSpan _simulatedLatency;

    public MockClaudeService(ILogger<MockClaudeService> logger, TimeSpan? simulatedLatency = null)
    {
        _logger = logger;
        _simulatedLatency = simulatedLatency ?? _defaultLatency;
    }

    public async Task<ClaudeRunResult> ReviewAsync(ClaudeRunRequest request, CancellationToken cancellationToken)
    {
        _logger.LogWarning("MockClaudeService is active — no real API call will be made.");

        // Simulate network + inference latency
        await Task.Delay(_simulatedLatency, cancellationToken);

        // Every check passes, so a local run shows a whole comment; no model ran, so nothing was spent.
        var rated = Checklist.Order.ToDictionary(id => id, id => new RatedCheck(id, CheckStatus.Pass, string.Empty, IsSerious: false));
        var answer = new RunAnswer(rated.AsReadOnly(), [], "Mock review: no model was called.", string.Empty);

        return new ClaudeRunResult(RunOutcome.Completed, answer, "mock", []);
    }
}
