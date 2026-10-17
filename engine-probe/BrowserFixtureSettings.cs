using System.IO;
using Deskweave.AgentWorkspaces;

/// <summary>Existing private-browser probes opt in explicitly; never change the owner's settings.</summary>
internal sealed class BrowserFixtureSettings : IDisposable
{
    readonly IDisposable? _scope;
    readonly string? _file;
    readonly bool _before;

    internal BrowserFixtureSettings()
    {
        string production = Deskweave.Product.ProductContext.Local("settings.json");
        if (string.Equals(AppSettingsStore.File, production, StringComparison.OrdinalIgnoreCase))
        {
            _file = Path.Combine(Path.GetTempPath(), "ars-browser-fixture-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_file, "{}");
            _scope = AppSettingsStore.UseFileForTests(_file);
        }
        _before = AppSettingsStore.Current.SeparateBrowserProfile;
        AppSettingsStore.Update(settings => settings with { SeparateBrowserProfile = true });
    }

    public void Dispose()
    {
        AppSettingsStore.Update(settings => settings with { SeparateBrowserProfile = _before });
        _scope?.Dispose();
        if (_file is not null) File.Delete(_file);
    }
}
