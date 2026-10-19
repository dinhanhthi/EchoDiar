import EchoAI
import EchoCore
import Foundation
import Testing

/// Guards the hand-seedable `chat.json` format and the clickable `[mm:ss]` / `[h:mm:ss]` citations.
struct AIStorageTests {
    @Test
    func chatLogRoundTripsAndToleratesMissingFields() throws {
        let log = ChatLog(messages: [
            ChatLog.Message(role: .user, text: "What was decided?", timestamp: Date(timeIntervalSince1970: 1_767_268_800)),
            ChatLog.Message(role: .assistant, text: "Ship on Friday [00:05].", provider: "anthropic",
                            model: "claude-sonnet-5-5", timestamp: Date(timeIntervalSince1970: 1_767_268_805)),
        ])
        #expect(try ChatLog.decode(log.encoded()) == log)

        let seeded = Data("""
            {"messages": [{"role": "user", "text": "Hi"},
                          {"role": "assistant", "text": "Hello [1:02:03]", "timestamp": "2026-01-01T12:00:05Z"}]}
            """.utf8)
        let decoded = try ChatLog.decode(seeded)
        #expect(decoded.version == 1)
        #expect(decoded.messages.map(\.role) == [.user, .assistant])
        #expect(decoded.messages[0].provider == nil && decoded.messages[0].timestamp == nil)
        #expect(decoded.messages[1].timestamp == Date(timeIntervalSince1970: 1_767_268_805))
    }

    @Test
    func citationsParseToSeconds() {
        #expect(TranscriptCitation.seconds("[00:05]") == 5)
        #expect(TranscriptCitation.seconds("[75:30]") == 4_530)
        #expect(TranscriptCitation.seconds("[1:02:03]") == 3_723)
        #expect(TranscriptCitation.seconds("[00:75]") == nil)
        #expect(TranscriptCitation.seconds("00:05") == nil)

        let linked = TranscriptCitation.linked("At [00:05] and [1:02:03]; see [10:00](https://x.y).", scheme: "cite")
        #expect(linked == "At [00:05](cite:5) and [1:02:03](cite:3723); see [10:00](https://x.y).")
    }
}
