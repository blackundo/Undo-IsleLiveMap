namespace TheIsleOverlay.Localization;

public sealed class LocalizationCoordinator
{
    private readonly TranslationDownloadClient _client;
    private readonly TranslationPackageInstaller _installer;
    private readonly ITheIsleInstallationLocator _installationLocator;
    private readonly ITheIsleProcessProbe _processProbe;
    private readonly string _stateRoot;
    private readonly string _languageConfigPath;

    public LocalizationCoordinator(
        TranslationDownloadClient client,
        TranslationPackageInstaller installer,
        ITheIsleInstallationLocator? installationLocator = null,
        ITheIsleProcessProbe? processProbe = null,
        string? stateRoot = null,
        string? languageConfigPath = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _installationLocator = installationLocator ?? new SteamTheIsleInstallationLocator();
        _processProbe = processProbe ?? new TheIsleProcessProbe();
        _stateRoot = stateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KLongDev",
            "IsleLiveMap",
            "Localization");
        _languageConfigPath = languageConfigPath ?? GameLanguageSettings.DefaultConfigPath;
    }

    public GameLocale ReadActiveLocale()
    {
        if (!File.Exists(_languageConfigPath))
        {
            return GameLocale.English;
        }

        return GameLanguageSettings.Read(File.ReadAllText(_languageConfigPath));
    }

    public async Task ApplyLocaleAsync(
        GameLocale locale,
        bool waitForGameExit,
        CancellationToken cancellationToken = default)
    {
        await EnsureGameStoppedAsync(waitForGameExit, cancellationToken);
        if (locale == GameLocale.English)
        {
            await GameLanguageSettings.SetAsync(
                _languageConfigPath,
                GameLocale.English,
                cancellationToken);
            return;
        }

        var installation = _installationLocator.Locate()
            ?? throw new DirectoryNotFoundException("Không tìm thấy The Isle qua Steam Registry.");
        var manifest = await _client.GetManifestAsync(installation.BuildId, cancellationToken);
        var archive = await _client.DownloadArchiveAsync(manifest, cancellationToken);

        // The game could start while the package was downloading. Never write
        // to its directory without checking again immediately before install.
        await EnsureGameStoppedAsync(waitForGameExit, cancellationToken);
        await _installer.InstallAsync(
            manifest,
            archive,
            installation.GameRoot,
            _stateRoot,
            installation.BuildId,
            GameLocale.Vietnamese,
            cancellationToken);
        await GameLanguageSettings.SetAsync(
            _languageConfigPath,
            GameLocale.Vietnamese,
            cancellationToken);
    }

    private async Task EnsureGameStoppedAsync(
        bool waitForGameExit,
        CancellationToken cancellationToken)
    {
        if (!_processProbe.IsGameOrBootstrapRunning())
        {
            return;
        }

        if (!waitForGameExit)
        {
            throw new InvalidOperationException("Hãy tắt The Isle; ứng dụng sẽ tiếp tục khi game đã thoát.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (_processProbe.IsGameOrBootstrapRunning()
               && await timer.WaitForNextTickAsync(cancellationToken))
        {
        }
    }
}
