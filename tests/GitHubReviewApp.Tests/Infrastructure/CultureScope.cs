namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Runs the code under test as a host with a different locale would, and puts the culture back afterwards. The culture of the running thread and
/// of the tasks it starts is what changes, so tests that run beside it are not affected.
/// </summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previousCulture;
    private readonly CultureInfo _previousUiCulture;

    public CultureScope(CultureInfo culture)
    {
        _previousCulture = CultureInfo.CurrentCulture;
        _previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>A culture that writes numbers the way a German host does: a dot between thousands, a comma for decimals.</summary>
    public static CultureInfo DotThousandsCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberGroupSeparator = ".";
        culture.NumberFormat.NumberDecimalSeparator = ",";

        return culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
    }
}
