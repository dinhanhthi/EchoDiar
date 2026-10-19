import EchoCore
import Foundation
import Testing

/// Guards the entry search: every field of a conversation is searchable, ignoring case and accents.
/// Each term below appears in exactly one field, so a match proves that field is indexed.
struct ConversationSearchTests {
    private let document = ConversationSearch.document(
        title: "Weekly sync",
        transcript: Transcript(
            segments: [
                .init(start: 0, end: 2, channel: .mic, speaker: Transcript.Speaker.me, text: "Hello everyone"),
                .init(start: 2, end: 4, channel: .system, speaker: "s1", text: "Xin chào từ Việt Nam"),
            ],
            speakerNames: ["s1": "Lan"]),
        summary: "The budget was approved.",
        chat: ChatLog(messages: [
            .init(role: .user, text: "Who owns the roadmap?"),
            .init(role: .assistant, text: "The design guild owns it."),
        ]))

    /// Title, renamed speaker, its default name, summary, chat question and answer, transcript without accents;
    /// a blank term matches everything.
    @Test(arguments: ["WEEKLY", "lan", "Speaker 1", "budget", "roadmap", "guild", "viet", "  "])
    func matchesEveryFieldIgnoringCaseAndAccents(term: String) {
        #expect(document.matches(term))
    }

    @Test func termFoundNowhereDoesNotMatch() {
        #expect(!document.matches("kangaroo"))
    }
}
