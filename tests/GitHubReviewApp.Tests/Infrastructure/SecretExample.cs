namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>One made-up credential: the text it appears in, and the part of it that must not survive.</summary>
internal sealed record SecretExample(string Text, string Secret);
