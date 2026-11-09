// Reviews one GitHub pull request with the app's own review and posts the result to Slack. Nothing is written to the pull request.
// Usage: dotnet run --project tools/GitHubReviewApp.ReviewPreview -- <pull request link>

if (args.Length != 1 || !PullRequestLink.TryParse(args[0], out var link))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/GitHubReviewApp.ReviewPreview -- <pull request link>");
    Console.Error.WriteLine("       for example https://github.com/my-org/my-service/pull/7 or my-org/my-service#7");
    return 2;
}

var configuration = PreviewServices.BuildConfiguration();
var secretProblems = PreviewServices.SecretProblems(configuration);

if (secretProblems.Count > 0)
{
    foreach (var problem in secretProblems)
    {
        Console.Error.WriteLine(problem);
    }

    Console.Error.WriteLine("Set them in user secrets or environment variables: see tools/GitHubReviewApp.ReviewPreview/README.md. Nothing was reviewed.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    using var provider = PreviewServices.Build(configuration, new GhCliTokenSource());
    var result = await provider.GetRequiredService<PreviewRunner>().RunAsync(link, cancellation.Token);

    Console.WriteLine($"Done: {link.Url} {result.Status} {result.Verdict}. The result is in Slack.");
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled. Nothing was posted.");
    return 130;
}
catch (Exception exception)
{
    // The type and message only: no secret is ever part of either (the token and the webhook URL are never put in a message).
    Console.Error.WriteLine($"The preview failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
