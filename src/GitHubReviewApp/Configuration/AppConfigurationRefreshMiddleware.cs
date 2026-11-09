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
            // Not awaited, so a refresh never delays the invocation, and started without the invocation's execution context, as the official
            // middleware does (SuppressFlow and Task.Run): with it, the refresh's HTTP spans would parent to the host's invocation activity,
            // which is never exported, and show as orphans. TryRefreshAsync does not throw.
            using (ExecutionContext.SuppressFlow())
            {
                foreach (var refresher in provider.Refreshers)
                {
                    _ = Task.Run(() => refresher.TryRefreshAsync());
                }
            }
        }

        await next(context);
    }
}
