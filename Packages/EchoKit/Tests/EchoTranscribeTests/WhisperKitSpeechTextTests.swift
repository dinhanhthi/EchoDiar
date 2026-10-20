@testable import EchoTranscribe
import Foundation
import Testing

/// Guards the `[BLANK_AUDIO]` fix: Whisper's non-speech output is dropped, speech with brackets is kept.
struct WhisperKitSpeechTextTests {
    @Test func dropsNonSpeechAnnotationsAndKeepsSpeechWithBrackets() {
        for raw in ["[BLANK_AUDIO]", " [ Silence ] ", "(music)", "*music*", "<|endoftext|>", "[BLANK_AUDIO] (Music)"] {
            #expect(WhisperKitEngine.speechText(raw) == "", "\(raw)")
        }

        #expect(WhisperKitEngine.speechText("Call f(x) and look at a[0] now.") == "Call f(x) and look at a[0] now.")
        #expect(WhisperKitEngine.speechText("(I think) we should go") == "(I think) we should go")
        #expect(WhisperKitEngine.speechText("See the notes [section 2]") == "See the notes [section 2]")
        #expect(WhisperKitEngine.speechText("<|en|> I said (quietly) hello") == "I said (quietly) hello")
        #expect(WhisperKitEngine.speechText("[Music] Hello there (laughs)") == "Hello there")
    }

    /// Guards the "Thank you." hallucinations: a segment over near silence is dropped, one over speech is kept.
    @Test func dropsSegmentsOverNearSilence() {
        let rate = 16_000
        let quiet = [Float](repeating: 0.001, count: 2 * rate)
        let speech = (0..<(2 * rate)).map { Float(0.1 * sin(2 * Double.pi * 220 * Double($0) / Double(rate))) }
        #expect(WhisperKitEngine.isSilent(quiet, from: 0, to: 2, sampleRate: rate))
        #expect(!WhisperKitEngine.isSilent(quiet + speech, from: 1.5, to: 5, sampleRate: rate))
    }
}
