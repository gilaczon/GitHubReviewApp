namespace GitHubReviewApp.Configuration;

/// <summary>
/// Holds the runtime settings (design 5.11) and follows the configuration as it reloads. It is not <c>IOptionsMonitor</c> with validation, because
/// that keeps no last good value: an invalid value on a reload would make every later read throw. Here every reload is parsed and validated into
/// an immutable snapshot that replaces the current one only when all three values are valid. Otherwise the last good snapshot stays (at startup,
/// the defaults) and a warning names the key. A value is never written to the log.
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

            var isSwitchValid = TryReadSwitch(section, nameof(RuntimeSettings.IsReviewEnabled), defaults.IsReviewEnabled, out var isReviewEnabled);
            var isSonnetModelValid = TryReadModelId(section, nameof(RuntimeSettings.SonnetModel), defaults.SonnetModel, out var sonnetModel);
            var isOpusModelValid = TryReadModelId(section, nameof(RuntimeSettings.OpusModel), defaults.OpusModel, out var opusModel);

            if (isSwitchValid && isSonnetModelValid && isOpusModelValid)
            {
                Volatile.Write(ref _current, new RuntimeSettings(isReviewEnabled, sonnetModel, opusModel));
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

    private bool TryReadSwitch(IConfigurationSection section, string key, bool isDefaultEnabled, out bool isEnabled)
    {
        var text = section[key];

        if (text is null)
        {
            isEnabled = isDefaultEnabled;
            return true;
        }

        if (bool.TryParse(text, out isEnabled))
        {
            return true;
        }

        WarnInvalid(key, "true or false");
        return false;
    }

    private bool TryReadModelId(IConfigurationSection section, string key, string defaultValue, out string modelId)
    {
        var text = section[key];

        if (text is null)
        {
            modelId = defaultValue;
            return true;
        }

        modelId = text;

        if (text.Length <= MaxModelIdLength && _modelIdPattern.IsMatch(text))
        {
            return true;
        }

        WarnInvalid(key, $"a model id such as claude-sonnet-5-5 (lowercase letters, digits, dots and hyphens, at most {MaxModelIdLength} characters)");
        return false;
    }

    private void WarnInvalid(string key, string expected)
    {
        _logger.LogWarning(
            "Ignoring the runtime settings: {SectionName}:{Key} is not valid, it must be {Expected}. The last good values stay in use.",
            SectionName, key, expected);
    }
}
