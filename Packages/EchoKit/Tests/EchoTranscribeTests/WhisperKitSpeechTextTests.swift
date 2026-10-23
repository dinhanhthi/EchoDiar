@testable import EchoTranscribe
import Foundation
import Testing
import WhisperKit

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

    /// Guards the "Thank you." hallucinations: a segment over near silence is dropped, one over speech is kept, and a
    /// NaN timestamp counts as silent instead of trapping.
    @Test func dropsSegmentsOverNearSilence() {
        let rate = 16_000
        let quiet = [Float](repeating: 0.001, count: 2 * rate)
        let speech = (0..<(2 * rate)).map { Float(0.1 * sin(2 * Double.pi * 220 * Double($0) / Double(rate))) }
        #expect(WhisperKitEngine.isSilent(quiet, from: 0, to: 2, sampleRate: rate))
        #expect(!WhisperKitEngine.isSilent(quiet + speech, from: 1.5, to: 5, sampleRate: rate))
        #expect(WhisperKitEngine.isSilent(quiet + speech, from: .nan, to: 5, sampleRate: rate))
    }

    /// Guards the subtitle-credit list: Whisper's credits are dropped even where Silero hears speech, or with no
    /// detector, and the meeting's speech is kept, also when it mentions subtitles mid-sentence.
    @Test func dropsSubtitleCreditsAndKeepsSpeech() {
        let rate = 16_000
        let texts = [
            "Sous-titrage Société Radio-Canada", "Sous-titres réalisés par la communauté d'Amara.org",
            "Thanks for watching!", "Hãy đăng ký kênh để ủng hộ kênh nhé!", "- Sous-titrage ST' 501",
            "Subtítulos por la comunidad de Amara.org", "字幕由志愿者社区提供", "Bonjour, on commence la réunion.",
            "Can we enable subtitles by default?", "On doit prévoir le sous-titrage de la vidéo.", "字幕由谁提供？",
        ]
        let speech = (0..<(2 * texts.count * rate)).map {
            Float(0.1 * sin(2 * Double.pi * 220 * Double($0) / Double(rate)))
        }
        let segments = texts.enumerated().map { index, text in
            TranscriptionSegment(start: Float(2 * index), end: Float(2 * index + 2), text: " \(text)")
        }
        let results = [TranscriptionResult(
            text: texts.joined(separator: " "), segments: segments, language: "fr", timings: TranscriptionTimings())]
        let voiced = [Float](repeating: 0.9, count: 8 * texts.count)
        for voicing in [voiced, nil] {
            let kept = WhisperKitEngine.speechSegments(
                results, in: speech, offset: 0, detectLanguage: false, voicing: voicing)
            #expect(kept.map(\.text) == [
                "Bonjour, on commence la réunion.", "Can we enable subtitles by default?",
                "On doit prévoir le sous-titrage de la vidéo.", "字幕由谁提供？",
            ])
        }
    }

    /// Guards the "Merci." hallucinations on a noisy mic: the noise passes `isSilent`, so only Silero's voicing drops
    /// the segment, also where breath or keys reach 0.88 on a short window (measured); a spoken "Merci." (frames at
    /// 1.0) or no detector (nil voicing) keeps it. The same voicing gates the whole window before it is decoded, and a
    /// NaN timestamp has no speech instead of trapping.
    @Test func dropsSegmentsSileroHearsNoSpeechIn() {
        let rate = 16_000
        let noise = (0..<(2 * rate)).map { _ in Float.random(in: -0.05...0.05) }
        #expect(!WhisperKitEngine.isSilent(noise, from: 0, to: 2, sampleRate: rate))
        let results = [TranscriptionResult(
            text: " Merci.", segments: [TranscriptionSegment(start: 0, end: 2, text: " Merci.")], language: "fr",
            timings: TranscriptionTimings())]
        let unvoiced = [Float](repeating: 0.1, count: 8)
        var breath = unvoiced
        breath[3] = 0.88
        breath[4] = 0.56
        var voiced = unvoiced
        voiced[3] = 1
        #expect(!WhisperKitEngine.hasSpeech(breath))
        #expect(WhisperKitEngine.hasSpeech(voiced))
        #expect(WhisperKitEngine.hasSpeech(nil))
        #expect(!WhisperKitEngine.hasSpeech(voiced, from: .nan, to: 2))

        for voicing in [unvoiced, breath] {
            #expect(WhisperKitEngine.speechSegments(
                results, in: noise, offset: 0, detectLanguage: false, voicing: voicing).isEmpty)
        }
        for voicing in [voiced, nil] {
            let kept = WhisperKitEngine.speechSegments(
                results, in: noise, offset: 0, detectLanguage: false, voicing: voicing)
            #expect(kept.map(\.text) == ["Merci."])
        }
    }

    /// Guards that the Silero model ships in `Bundle.module`: without it the speech gate is silently off.
    @Test func bundledVoiceDetectorLoads() async throws {
        let detector = try #require(await WhisperKitEngine.loadVoiceDetector())
        let voicing = try #require(await WhisperKitEngine.voicing(detector, [Float](repeating: 0, count: 16_000)))
        #expect(voicing.count == 4)
        #expect(voicing.allSatisfy { $0 < WhisperKitEngine.speechProbability })
    }
}
