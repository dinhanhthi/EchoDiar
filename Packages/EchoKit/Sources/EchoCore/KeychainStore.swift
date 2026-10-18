import Foundation
import Security

public struct KeychainError: Error, LocalizedError {
    public let status: OSStatus

    public var errorDescription: String? {
        "Keychain error \(status): \(SecCopyErrorMessageString(status, nil) as String? ?? "unknown")."
    }
}

/// Provider API keys as generic passwords, service `dinh.thi.EchoDiar.provider.<id>`. Keys live only here,
/// never in settings.json; nothing in this file logs or returns a key except `apiKey(forProvider:)`.
/// Uses the login keychain (no data-protection keychain: a Developer ID build has no application identifier),
/// so `security find-generic-password -s <service>` finds the items.
public enum KeychainStore {
    private static let account = "apiKey"

    public static func service(forProvider id: String) -> String {
        "dinh.thi.EchoDiar.provider.\(id)"
    }

    /// The saved key, or nil when there is none.
    public static func apiKey(forProvider id: String) throws -> String? {
        var query = baseQuery(id)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else { throw KeychainError(status: status) }
        return String(decoding: data, as: UTF8.self)
    }

    /// Whether a key is saved, without reading it (attribute-only queries never show an access prompt).
    public static func hasAPIKey(forProvider id: String) throws -> Bool {
        var query = baseQuery(id)
        query[kSecReturnAttributes as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        let status = SecItemCopyMatching(query as CFDictionary, nil)
        if status == errSecItemNotFound { return false }
        guard status == errSecSuccess else { throw KeychainError(status: status) }
        return true
    }

    /// Saves `key`, replacing an older one; an empty key deletes it.
    public static func setAPIKey(_ key: String, forProvider id: String) throws {
        guard !key.isEmpty else { return try deleteAPIKey(forProvider: id) }
        let data = Data(key.utf8)
        var query = baseQuery(id)
        query[kSecValueData as String] = data
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        var status = SecItemAdd(query as CFDictionary, nil)
        if status == errSecDuplicateItem {
            status = SecItemUpdate(baseQuery(id) as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        }
        guard status == errSecSuccess else { throw KeychainError(status: status) }
    }

    public static func deleteAPIKey(forProvider id: String) throws {
        let status = SecItemDelete(baseQuery(id) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw KeychainError(status: status) }
    }

    private static func baseQuery(_ id: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service(forProvider: id),
            kSecAttrAccount as String: account,
        ]
    }
}
