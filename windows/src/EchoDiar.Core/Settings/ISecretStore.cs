namespace EchoDiar.Core.Settings;

/// <summary>
/// Provider API keys, one per <see cref="ProviderConfig.Id"/> (Swift <c>KeychainStore</c>). Keys live only here, never
/// in settings.json. The Windows store is Platform's <c>CredentialStore</c> (Credential Manager); tests use a fake.
/// Errors never carry key text.
/// </summary>
public interface ISecretStore
{
    /// <summary>The saved key, or null when there is none.</summary>
    string? ApiKey(string providerId);

    /// <summary>Saves <paramref name="key"/>, replacing an older one; an empty key deletes it.</summary>
    void SetApiKey(string providerId, string key);

    /// <summary>Deletes the key; nothing saved is not an error.</summary>
    void DeleteApiKey(string providerId);
}
