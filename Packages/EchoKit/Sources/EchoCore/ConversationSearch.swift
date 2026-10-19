import Foundation

/// Full-text search over a conversation: title, speakers, transcript, summary and chat.
public enum ConversationSearch {
    /// The searchable text of one conversation, one string per field.
    public struct Document: Sendable {
        public var title: String
        /// Display and default names of every speaker, in first-appearance order.
        public var speakers: [String]
        public var transcript: String
        public var summary: String
        public var chat: String

        /// True when `term` is blank, or some field contains it (ignoring case and accents, locale-aware).
        public func matches(_ term: String) -> Bool {
            let term = term.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !term.isEmpty else { return true }
            return ([title, transcript, summary, chat] + speakers).contains { $0.localizedStandardContains(term) }
        }
    }

    /// Builds the document of one conversation; a missing transcript, summary or chat yields empty fields.
    public static func document(title: String, transcript: Transcript?, summary: String?, chat: ChatLog?) -> Document {
        let segments = transcript?.segments ?? []
        var speakers: [String] = []
        if let transcript {
            for speaker in segments.map(\.speaker) {
                for name in [transcript.displayName(for: speaker), Transcript.defaultName(for: speaker)]
                where !speakers.contains(name) {
                    speakers.append(name)
                }
            }
        }
        return Document(
            title: title,
            speakers: speakers,
            transcript: segments.map(\.text).joined(separator: "\n"),
            summary: summary ?? "",
            chat: (chat?.messages ?? []).map(\.text).joined(separator: "\n"))
    }
}
