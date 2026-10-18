@testable import EchoTranscribe
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
}
