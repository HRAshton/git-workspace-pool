using GitWorkspacePool.Infrastructure.Configuration;
using Xunit;

namespace GitWorkspacePool.Tests.Integration;

public sealed class AtomicConfigurationFileTests
{
    [Fact]
    public void SavePublishesCompleteSettingsAndPreservesPreviousVersion()
    {
        using Fixture fixture = new();
        AtomicConfigurationFile settings = new(fixture.Path);
        ConfigurationSnapshot initial = settings.Read();
        ConfigurationSnapshot saved = settings.Save("{\"value\":2}", initial.Fingerprint);
        Assert.Equal("{\"value\":2}", File.ReadAllText(fixture.Path));
        Assert.Equal("{\"value\":1}", File.ReadAllText(fixture.Path + ".previous"));
        Assert.Equal(saved.Fingerprint, settings.Read().Fingerprint);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.pending-*"));
    }

    [Fact]
    public void StaleSettingsEditorCannotOverwriteAnExternalEdit()
    {
        using Fixture fixture = new();
        AtomicConfigurationFile settings = new(fixture.Path);
        ConfigurationSnapshot initial = settings.Read();
        File.WriteAllText(fixture.Path, "{\"external\":true}");
        Assert.Throws<InvalidOperationException>(() => settings.Save("{\"value\":2}", initial.Fingerprint));
        Assert.Equal("{\"external\":true}", File.ReadAllText(fixture.Path));
        Assert.False(File.Exists(fixture.Path + ".previous"));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.pending-*"));
    }

    [Fact]
    public void FailedPublicationLeavesOriginalAndPendingSettingsAvailable()
    {
        using Fixture fixture = new();
        AtomicConfigurationFile settings = new(fixture.Path);
        ConfigurationSnapshot initial = settings.Read();
        Directory.CreateDirectory(fixture.Path + ".previous");
        Exception? failure = Record.Exception(() => settings.Save("{\"value\":2}", initial.Fingerprint));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("{\"value\":1}", File.ReadAllText(fixture.Path));
        string pending = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.pending-*"));
        Assert.Equal("{\"value\":2}", File.ReadAllText(pending));
    }

    [Fact]
    public void OversizedSettingsAreRefusedWithoutReplacingTheSource()
    {
        using Fixture fixture = new();
        AtomicConfigurationFile settings = new(fixture.Path);
        ConfigurationSnapshot initial = settings.Read();
        Assert.Throws<InvalidOperationException>(() => settings.Save(new string('x', 1024 * 1024 + 1), initial.Fingerprint));
        Assert.Equal("{\"value\":1}", File.ReadAllText(fixture.Path));
        File.WriteAllText(fixture.Path, new string('x', 1024 * 1024 + 1));
        Assert.Throws<InvalidOperationException>(() => settings.Read());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gwp-settings-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "settings.json");
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path, "{\"value\":1}");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
