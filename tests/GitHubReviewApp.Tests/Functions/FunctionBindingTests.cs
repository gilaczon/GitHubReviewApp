namespace GitHubReviewApp.Tests.Functions;

/// <summary>
/// The Functions host wires triggers from attributes, and no unit test runs the host. These tests read the
/// attributes with reflection so a typo in a queue name or route cannot ship unnoticed.
/// </summary>
public class FunctionBindingTests
{
    private static MethodInfo RunMethod(Type type)
    {
        return type.GetMethod("RunAsync")!;
    }

    private static T Attribute<T>(MemberInfo member) where T : Attribute
    {
        return member.GetCustomAttribute<T>()!;
    }

    private static T ParameterAttribute<T>(MethodInfo method, int index) where T : Attribute
    {
        return method.GetParameters()[index].GetCustomAttribute<T>()!;
    }

    [Fact]
    public void Assembly_GivenTheDeployedFunctions_Should_DeclareExactlyTheExpectedFunctionsWithUniqueNames()
    {
        // Arrange
        var appAssembly = typeof(ReviewProcessor).Assembly;

        // Act
        var functionNames = appAssembly.GetTypes()
            .SelectMany(type => type.GetMethods())
            .Select(method => method.GetCustomAttribute<FunctionAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name)
            .ToList();

        // Assert
        functionNames.Should().BeEquivalentTo("WebhookReceiver", "ReviewProcessor", "ReviewPoisonHandler");
        functionNames.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void WebhookReceiver_GivenItsTrigger_Should_BeAnAnonymousPostEndpointAtWebhookGithub()
    {
        // Arrange

        // Act
        var trigger = ParameterAttribute<HttpTriggerAttribute>(RunMethod(typeof(WebhookReceiver)), 0);

        // Assert — anonymous on purpose: GitHub cannot send a function key, the HMAC signature is the authentication
        trigger.AuthLevel.Should().Be(AuthorizationLevel.Anonymous);
        trigger.Methods.Should().Equal("post");
        trigger.Route.Should().Be("webhook/github");
    }

    [Fact]
    public void WebhookOutput_GivenItsBindings_Should_WriteToTheReviewQueueAndReturnAnHttpResponse()
    {
        // Arrange
        var queueProperty = typeof(WebhookOutput).GetProperty(nameof(WebhookOutput.QueueMessage))!;
        var responseProperty = typeof(WebhookOutput).GetProperty(nameof(WebhookOutput.HttpResponse))!;

        // Act
        var queueOutput = Attribute<QueueOutputAttribute>(queueProperty);
        var httpResult = responseProperty.GetCustomAttribute<HttpResultAttribute>();

        // Assert
        queueOutput.QueueName.Should().Be("ai-review-queue");
        queueOutput.Connection.Should().Be("ReviewQueueConnection");
        httpResult.Should().NotBeNull("without [HttpResult] the host would not send the response GitHub is waiting for");
    }

    [Fact]
    public void ReviewProcessor_GivenItsTrigger_Should_ReadFromTheSameQueueTheWebhookWritesTo()
    {
        // Arrange
        var trigger = ParameterAttribute<QueueTriggerAttribute>(RunMethod(typeof(ReviewProcessor)), 0);

        // Act
        var output = Attribute<QueueOutputAttribute>(typeof(WebhookOutput).GetProperty(nameof(WebhookOutput.QueueMessage))!);

        // Assert
        trigger.QueueName.Should().Be("ai-review-queue");
        trigger.Connection.Should().Be("ReviewQueueConnection");
        trigger.QueueName.Should().Be(output.QueueName);
        trigger.Connection.Should().Be(output.Connection);
    }

    [Fact]
    public void ReviewPoisonHandler_GivenItsTrigger_Should_ReadFromThePoisonQueueTheHostCreatesForTheReviewQueue()
    {
        // Arrange
        var trigger = ParameterAttribute<QueueTriggerAttribute>(RunMethod(typeof(ReviewPoisonHandler)), 0);

        // Act
        var reviewQueue = ParameterAttribute<QueueTriggerAttribute>(RunMethod(typeof(ReviewProcessor)), 0);

        // Assert — the host names it "<queue>-poison"
        trigger.QueueName.Should().Be("ai-review-queue-poison");
        trigger.QueueName.Should().Be(reviewQueue.QueueName + "-poison");
        trigger.Connection.Should().Be(reviewQueue.Connection);
    }

    [Theory]
    [InlineData(typeof(ReviewProcessor))]
    [InlineData(typeof(ReviewPoisonHandler))]
    public void QueueFunctions_GivenTheirSignatures_Should_AcceptACancellationTokenSoShutdownCanInterruptThem(Type functionType)
    {
        // Arrange

        // Act
        var parameters = RunMethod(functionType).GetParameters();

        // Assert
        parameters.Should().Contain(parameter => parameter.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void HostJson_GivenTheShippedConfiguration_Should_RetryAFewTimesBeforeMovingAMessageToThePoisonQueue()
    {
        // Arrange
        var host = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "host.json"))).RootElement;

        // Act
        var queues = host.GetProperty("extensions").GetProperty("queues");

        // Assert
        queues.GetProperty("maxDequeueCount").GetInt32().Should().BeInRange(2, 10);
        TimeSpan.Parse(queues.GetProperty("visibilityTimeout").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void HostJson_GivenTheShippedConfiguration_Should_BoundHowManyReviewsRunAtOnce()
    {
        // Arrange
        var host = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "host.json"))).RootElement;

        // Act
        var queues = host.GetProperty("extensions").GetProperty("queues");
        var batchSize = queues.GetProperty("batchSize").GetInt32();
        var newBatchThreshold = queues.GetProperty("newBatchThreshold").GetInt32();

        // Assert — each review holds up to 180 KB of diff and two model requests at once. The trigger fetches a new batch once
        // newBatchThreshold messages are left, so up to batchSize + newBatchThreshold reviews run at once on one instance: 4 + 2 = 6 as shipped,
        // which is what docs/operations.md says and what the model limiter's default of 12 slots is sized for. Changing either number is a
        // decision to make on purpose, with that document and the limiter in view.
        batchSize.Should().Be(4);
        newBatchThreshold.Should().Be(2);
        (2 * (batchSize + newBatchThreshold)).Should().BeLessThanOrEqualTo(new ReviewOptions().MaxConcurrentModelRequests, "each review holds two model slots");
    }

    [Fact]
    public void HostJson_GivenTheShippedConfiguration_Should_LetTheHostWaitLongerThanTheAppsOwnLimitForAReview()
    {
        // Arrange — when the host's own timeout fires it restarts the worker, and that takes every other review on the instance with it. The app ends
        // a review that runs too long by itself and retries it with the finished parts kept, which only helps if it does so first. A plan that
        // caps the timeout lower (the legacy Consumption plan allows ten minutes) must refuse to start rather than cut reviews off.
        var host = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "host.json"))).RootElement;

        // Act
        var hostTimeout = TimeSpan.Parse(host.GetProperty("functionTimeout").GetString()!, CultureInfo.InvariantCulture);
        var appLimit = TimeSpan.FromSeconds(new ReviewOptions().MaxReviewDurationSeconds);

        // Assert
        hostTimeout.Should().BeGreaterThanOrEqualTo(appLimit + TimeSpan.FromMinutes(5), "the unwinding of a cancelled review and its last GitHub calls need room");
    }

    [Fact]
    public void ReviewQueueMessage_GivenAJsonRoundTrip_Should_ComeBackUnchanged()
    {
        // Arrange
        var message = new ReviewQueueMessage("my-org", "my-service", 42, "feat: x", "basesha", "headsha", 999888777L);

        // Act
        var json = JsonSerializer.Serialize(message);
        var roundTripped = JsonSerializer.Deserialize<ReviewQueueMessage>(json);

        // Assert
        roundTripped.Should().Be(message);
    }
}
