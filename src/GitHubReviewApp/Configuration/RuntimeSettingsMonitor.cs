namespace GitHubReviewApp.Configuration;

/// <summary>
/// Holds the runtime settings (design 5.11) and follows the configuration as it reloads. It is not <c>IOptionsMonitor</c> with validation, because
/// that keeps no last good value: an invalid value on a reload would make every later read throw. Here every reload is parsed and validated into
/// an immutable snapshot, and each key is judged on its own: a valid value is applied, a missing key takes its default, and an invalid value keeps
/// that key's last good value (at startup, its default) with a warning that names the key. One bad key therefore never holds back another, so a
/// valid switch turning reviews off is never blocked by a model id. A value is never written to the log.
/// </summary>
public sealed class RuntimeSettingsMonitor : IRuntimeSettings, IDisposable
{
    public const string SectionName = "Runtime";

    private const int MaxModelIdLength = 100;

    private static readonly Regex _modelIdPattern = new(@"^claude-[a-z0-9.-]+\z", RegexOptions.CultureInvariant);

    private readonly IConfiguration _configuration;
    private readonly ILogger<RuntimeSettingsMonitor> _logger;
    private readonly IDisposable _reloadRegistration;
    private RuntimeSettings _current = RuntimeSettings.Default;

    public RuntimeSettingsMonitor(IConfiguration configuration, ILogger<RuntimeSettingsMonitor> logger)
    {
        _configuration = configuration;
        _logger = logger;

        Refresh();
        _reloadRegistration = ChangeToken.OnChange(_configuration.GetReloadToken, Refresh);
    }

    public RuntimeSettings Current => Volatile.Read(ref _current);

    public void Dispose()
    {
        _reloadRegistration.Dispose();
    }

    /// <summary>
    /// Runs inside the configuration's change callback, so whatever it throws reaches whoever called Reload(), which in production is App
    /// Configuration's refresh. It therefore catches everything.
    /// </summary>
    private void Refresh()
    {
        try
        {
            var section = _configuration.GetSection(SectionName);
            var defaults = RuntimeSettings.Default;
            var lastGood = Current;

            var snapshot = new RuntimeSettings(
                ReadSwitch(section, nameof(RuntimeSettings.IsReviewEnabled), defaults.IsReviewEnabled, lastGood.IsReviewEnabled),
                ReadModelId(section, nameof(RuntimeSettings.SonnetModel), defaults.SonnetModel, lastGood.SonnetModel),
                ReadModelId(section, nameof(RuntimeSettings.OpusModel), defaults.OpusModel, lastGood.OpusModel));

            if (snapshot != lastGood)
            {
                Volatile.Write(ref _current, snapshot);
            }
        }
        catch (Exception exception)
        {
            // The type only: a message from a configuration provider could quote a value.
            _logger.LogWarning(
                "The runtime settings could not be read ({ExceptionType}); the last good values stay in use.",
                exception.GetType().Name);
        }
    }

    private bool ReadSwitch(IConfigurationSection section, string key, bool isDefaultEnabled, bool isLastGoodEnabled)
    {
        var text = section[key];

        if (text is null)
        {
            return isDefaultEnabled;
        }

        if (bool.TryParse(text, out var isEnabled))
        {
            return isEnabled;
        }

        WarnInvalid(key, "true or false");
        return isLastGoodEnabled;
    }

    private string ReadModelId(IConfigurationSection section, string key, string defaultValue, string lastGoodValue)
    {
        var text = section[key];

        if (text is null)
        {
            return defaultValue;
        }

        if (text.Length <= MaxModelIdLength && _modelIdPattern.IsMatch(text))
        {
            return text;
        }

        WarnInvalid(key, $"a model id such as claude-sonnet-5-5 (lowercase letters, digits, dots and hyphens, at most {MaxModelIdLength} characters)");
        return lastGoodValue;
    }

    private void WarnInvalid(string key, string expected)
    {
        _logger.LogWarning(
            "Ignoring {SectionName}:{Key}: it is not valid, it must be {Expected}. The last good value of this key stays in use.",
            SectionName, key, expected);
    }
}
