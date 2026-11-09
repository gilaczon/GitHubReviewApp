namespace GitHubReviewApp.Configuration;

/// <summary>
/// App Configuration never refreshes on its own. In an isolated Functions worker, the refresh registered in <see cref="AppConfigurationSetup"/>
/// does nothing until something asks for it, and this is that: it asks every refresher on each invocation. A refresher does the work only once its
/// refresh interval has passed.
/// </summary>
public sealed class AppConfigurationRefreshMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var provider = context.InstanceServices.GetService<IConfigurationRefresherProvider>();

        if (provider is not null)
        {
            foreach (var refresher in provider.Refreshers)
            {
                // Not awaited, so a refresh never delays the invocation (the official middleware does the same). TryRefreshAsync does not throw.
                _ = refresher.TryRefreshAsync();
            }
        }

        await next(context);
    }
}
