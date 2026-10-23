using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EchoDiar.Core.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace EchoDiar.Platform.SystemServices;

/// <summary>
/// Provider API keys in Windows Credential Manager (Swift <c>KeychainStore</c>): generic credentials named
/// <c>dinh.thi.EchoDiar.provider.&lt;id&gt;</c>, user name <c>apiKey</c>, the key as a UTF-8 blob of at most
/// <see cref="MaxKeyBytes"/> bytes, persisted for this user on this machine (not roamed).
/// <c>cmdkey /list:dinh.thi.EchoDiar.provider.*</c> lists them.
/// <para>
/// Provider ids come from settings.json, so only <c>[A-Za-z0-9._-]</c> is allowed and the target name is capped at
/// CRED_MAX_GENERIC_TARGET_NAME_LENGTH; anything else throws <see cref="ArgumentException"/>. Credential Manager
/// compares target names case-insensitively, so ids differing only in case share one key.
/// </para>
/// <para>
/// Errors are <see cref="Win32Exception"/> with the Win32 code, or <see cref="ArgumentException"/>; none carries key
/// text. The native buffers are zeroed after use; the returned string itself cannot be.
/// </para>
/// </summary>
public sealed class CredentialStore : ISecretStore
{
    public const string TargetPrefix = "dinh.thi.EchoDiar.provider.";
    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE (5*512).</summary>
    public const int MaxKeyBytes = (int)PInvoke.CRED_MAX_CREDENTIAL_BLOB_SIZE;

    private const string UserName = "apiKey";
    private const int ErrorNotFound = 1168; // ERROR_NOT_FOUND

    /// <summary>The credential's target name; throws <see cref="ArgumentException"/> for an unsafe id.</summary>
    public static string TargetName(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        if (providerId.Length == 0 || TargetPrefix.Length + providerId.Length > PInvoke.CRED_MAX_GENERIC_TARGET_NAME_LENGTH)
            throw new ArgumentException("The provider id is empty or too long.", nameof(providerId));
        foreach (var c in providerId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                throw new ArgumentException("The provider id may only contain A-Z, a-z, 0-9, '.', '_' and '-'.", nameof(providerId));
        }
        return TargetPrefix + providerId;
    }

    public unsafe string? ApiKey(string providerId)
    {
        var target = TargetName(providerId);
        if (!PInvoke.CredRead(target, CRED_TYPE.CRED_TYPE_GENERIC, out var credential))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var blob = new Span<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize);
            var key = Encoding.UTF8.GetString(blob);
            CryptographicOperations.ZeroMemory(blob);
            return key;
        }
        finally
        {
            PInvoke.CredFree(credential);
        }
    }

    public unsafe void SetApiKey(string providerId, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var target = TargetName(providerId);
        if (key.Length == 0)
        {
            DeleteApiKey(providerId);
            return;
        }
        var size = Encoding.UTF8.GetByteCount(key);
        if (size > MaxKeyBytes)
            throw new ArgumentException($"The API key is longer than {MaxKeyBytes} bytes.", nameof(key));

        // Pinned, so the GC never leaves a copy of the key behind before it is zeroed.
        var blob = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        try
        {
            Encoding.UTF8.GetBytes(key, blob);
            fixed (char* targetName = target)
            fixed (char* userName = UserName)
            fixed (byte* data = blob)
            {
                var credential = new CREDENTIALW
                {
                    Type = CRED_TYPE.CRED_TYPE_GENERIC,
                    TargetName = new PWSTR(targetName),
                    UserName = new PWSTR(userName),
                    CredentialBlob = data,
                    CredentialBlobSize = (uint)size,
                    Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
                };
                if (!PInvoke.CredWrite(credential, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    public void DeleteApiKey(string providerId)
    {
        var target = TargetName(providerId);
        if (PInvoke.CredDelete(target, CRED_TYPE.CRED_TYPE_GENERIC)) return;
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNotFound) throw new Win32Exception(error);
    }
}
