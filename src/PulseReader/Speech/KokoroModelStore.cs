using PulseReader.Setup;

namespace PulseReader.Speech;

internal sealed class KokoroModelStore
{
    private readonly ModelSetupService _setup;

    public KokoroModelStore(string? localApplicationDataPath = null)
        : this(new ModelSetupService(localApplicationDataPath)) { }

    internal KokoroModelStore(ModelSetupService setup)
    {
        _setup = setup;
        ModelPath = _setup.KokoroPath;
    }

    public string ModelPath { get; }

    public async Task<string> GetModelPathAsync(CancellationToken cancellationToken)
    {
        await _setup.EnsureKokoroAsync(null, cancellationToken).ConfigureAwait(false);
        return ModelPath;
    }
}
