using PulseReader.Configuration;
using PulseReader.Speech;

namespace PulseReader.Setup;

internal static class OptionalModelStartupPolicy
{
    public static OptionalModelStartupResult Apply(
        AppConfiguration configuration,
        Func<ContextualPronunciationAvailability> inspectInstallation,
        Func<AppConfiguration, string?> saveConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(inspectInstallation);
        ArgumentNullException.ThrowIfNull(saveConfiguration);

        if (!configuration.ContextualPronunciationEnabled
            || inspectInstallation().State != ContextualPronunciationInstallationState.MissingOrInvalid)
        {
            return new(configuration, configuration.ContextualPronunciationEnabled, null);
        }

        var disabled = configuration with { ContextualPronunciationEnabled = false };
        var saveError = saveConfiguration(disabled);
        if (saveError is not null)
        {
            return new(configuration, false,
                "The optional pronunciation model is missing or invalid, but Pulse Reader could not save "
                + "the setting as off. It is unavailable for this session; the saved setting remains enabled. "
                + "Turn it off in Settings or restart after fixing access to the settings file. " + saveError);
        }

        return new(disabled, false,
            "The optional pronunciation model is missing or invalid, so its setting was turned off. "
            + "Re-enable it in Settings to download the model again.");
    }
}

internal sealed record OptionalModelStartupResult(
    AppConfiguration Configuration,
    bool RuntimeContextualPronunciationEnabled,
    string? Warning);
