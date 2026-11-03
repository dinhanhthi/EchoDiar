import EchoAI
import Foundation
import Testing

/// Guards the AI job queue policy: one job per conversation (nil = the library chat) while other keys run, and only
/// in-app jobs after the earliest-started one wait as "Queued" until it ends.
struct AIJobTableTests {
    @Test func busyKeyRefusesSecondJobWhileOtherKeysStart() {
        var table = AIJobTable<Int>()
        let a = UUID()
        let b = UUID()

        let startedA = table.start(key: a, kind: .summary, usesInAppModel: false, payload: 1)
        let startedAAgain = table.start(key: a, kind: .question, usesInAppModel: false, payload: 2)
        let startedB = table.start(key: b, kind: .question, usesInAppModel: true, payload: 3)
        let startedLibrary = table.start(key: nil, kind: .libraryQuestion, usesInAppModel: false, payload: 4)

        #expect(startedA)
        #expect(!startedAAgain)
        #expect(startedB)
        #expect(startedLibrary)
        #expect(table.busyIDs == [a, b])
        #expect(table.entry(for: a)?.payload == 1)
        #expect(table.entry(for: nil)?.kind == .libraryQuestion)
    }

    @Test func laterInAppJobIsQueuedUntilEarlierInAppJobFinishes() {
        var table = AIJobTable<Int>()
        let first = UUID()
        let second = UUID()
        let cloud = UUID()

        table.start(key: first, kind: .summary, usesInAppModel: true, payload: 0)
        table.start(key: cloud, kind: .question, usesInAppModel: false, payload: 0)
        table.start(key: second, kind: .summary, usesInAppModel: true, payload: 0)

        #expect(!table.isQueued(first))
        #expect(!table.isQueued(cloud))
        #expect(table.isQueued(second))

        table.finish(key: first)
        #expect(!table.isQueued(second))
        #expect(table.hasInAppJob)
    }
}
