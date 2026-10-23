using EchoDiar.Core;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;

namespace EchoDiar.Platform.Tests;

// Against the runner's real Credential Manager: every test uses its own random id and deletes it in finally.
public sealed class CredentialStoreTests
{
    private readonly CredentialStore _store = new();

    private static string RandomId() => "test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void AKeyIsWrittenReadReplacedAndDeleted()
    {
        var id = RandomId();
        try
        {
            _store.SetApiKey(id, "sk-first-é漢");
            Assert.Equal("sk-first-é漢", _store.ApiKey(id));

            _store.SetApiKey(id, "sk-second");
            Assert.Equal("sk-second", _store.ApiKey(id));

            _store.DeleteApiKey(id);
            Assert.Null(_store.ApiKey(id));

            _store.SetApiKey(id, "sk-third");
            _store.SetApiKey(id, ""); // an empty key deletes, like Swift
            Assert.Null(_store.ApiKey(id));
        }
        finally
        {
            _store.DeleteApiKey(id);
        }
    }

    [Fact]
    public void AMissingIdReadsAsNullAndDeletesWithoutError()
    {
        var id = RandomId();
        Assert.Null(_store.ApiKey(id));
        _store.DeleteApiKey(id);
    }

    [Fact]
    public void AKeyOverTheBlobLimitIsRefusedWithoutItsText()
    {
        var id = RandomId();
        try
        {
            var atLimit = new string('k', CredentialStore.MaxKeyBytes);
            _store.SetApiKey(id, atLimit);
            Assert.Equal(atLimit, _store.ApiKey(id));
            _store.DeleteApiKey(id);

            // 1297 chars but 2562 UTF-8 bytes: the limit is in bytes.
            var marker = Guid.NewGuid().ToString("N");
            var secret = marker + new string('é', 1265);
            Assert.True(secret.Length < CredentialStore.MaxKeyBytes);
            var error = Assert.Throws<ArgumentException>(() => _store.SetApiKey(id, secret));
            Assert.DoesNotContain(marker, error.ToString());
            Assert.Null(_store.ApiKey(id));
        }
        finally
        {
            _store.DeleteApiKey(id);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..\\other")]
    [InlineData("*")]
    [InlineData("openai*")]
    [InlineData("a b")]
    [InlineData("a\0b")]
    [InlineData("a:b")]
    [InlineData("é")]
    public void AnUnsafeIdIsRefused(string id)
    {
        Assert.Throws<ArgumentException>(() => _store.ApiKey(id));
        Assert.Throws<ArgumentException>(() => _store.SetApiKey(id, "sk-unsafe"));
        Assert.Throws<ArgumentException>(() => _store.DeleteApiKey(id));
    }

    [Fact]
    public void AnIdOverTheTargetNameLimitIsRefused()
    {
        var id = new string('a', 32767 - CredentialStore.TargetPrefix.Length + 1);
        Assert.Throws<ArgumentException>(() => _store.SetApiKey(id, "sk-long"));
        Assert.Throws<ArgumentException>(() => _store.ApiKey(id));
    }

    [Fact]
    public void PresetAndCustomIdsAreAccepted()
    {
        Assert.Equal("dinh.thi.EchoDiar.provider.openai", CredentialStore.TargetName("openai"));
        var custom = ProviderConfig.Custom().Id;
        Assert.Equal(CredentialStore.TargetPrefix + custom, CredentialStore.TargetName(custom));
    }

    [Fact]
    public void SettingsJsonNeverHoldsTheKeyAfterSavingAProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "EchoDiar-cred-" + Guid.NewGuid().ToString("N"));
        var provider = ProviderConfig.Custom("Test", "https://example.invalid/v1");
        var key = "sk-settings-" + Guid.NewGuid().ToString("N");
        try
        {
            _store.SetApiKey(provider.Id, key);
            var settings = new AppSettings { Providers = [provider] };
            var settingsStore = new SettingsStore(new AppPaths(root));
            settingsStore.Save(settings);

            var json = File.ReadAllText(settingsStore.FilePath);
            Assert.Contains(provider.Id, json);
            Assert.DoesNotContain(key, json);
            Assert.Equal(key, _store.ApiKey(settingsStore.Load().Providers.Single().Id));
        }
        finally
        {
            _store.DeleteApiKey(provider.Id);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
