@testable import EchoTranscribe
import Testing

/// Guards the paused-recording release: the live model is freed once it went unused for the idle time, and the
/// next window loads it again.
struct IdleModelTests {
    @Test func freesTheModelAfterTheIdleTimeAndLoadsItAgainOnUse() async throws {
        let start = ContinuousClock.now
        let log = Log()
        let model = IdleModel(1, idle: .seconds(60), now: start, load: { log.events.append("load"); return 2 },
                              unload: { log.events.append("unload \($0)") })

        #expect(await model.releaseIfIdle(now: start + .seconds(59)) == false)
        #expect(await model.releaseIfIdle(now: start + .seconds(60)))
        #expect(try await model.get(now: start + .seconds(90)) == 2)
        #expect(await model.releaseIfIdle(now: start + .seconds(149)) == false)
        await model.release()
        #expect(log.events == ["unload 1", "load", "unload 2"])
    }

    @Test func neverFreesTheModelWithoutAnIdleTime() async {
        let start = ContinuousClock.now
        let model = IdleModel(1, idle: nil, now: start, load: { 2 }, unload: { _ in })
        #expect(await model.releaseIfIdle(now: start + .seconds(86_400)) == false)
    }

    private final class Log {
        var events: [String] = []
    }
}
