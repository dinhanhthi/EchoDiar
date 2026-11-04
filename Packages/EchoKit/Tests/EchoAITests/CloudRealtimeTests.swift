@testable import EchoAI
import EchoCore
import Foundation
import Testing

/// A fake long-lived key; it must never show up in a URL, an outgoing message or an error kind.
private let key = "sk-live-SECRET-0000"

/// Guards the realtime contract of the 8 providers (stt-protocols.md part B): where the key travels, the audio
/// format and rate asked for, the consent destination and the socket host check (table 1), and how recorded server
/// messages parse into partials and finals with stream-relative times and speakers (table 2). No network: only the
/// pure adapters, the catalog and the refused-URL guard of `RealtimeConnection.connect` run.
struct CloudRealtimeTests {
    // MARK: - Table 1: request and security contract

    struct Contract: Sendable, CustomTestStringConvertible {
        var preset: String
        /// Handshake headers (Gladia: none, the key goes only in the init call's header).
        var headers: [String: String]
        var sampleRate: Int
        var streamsSpeakers: Bool
        /// Audio as base64 in a JSON text frame (else raw binary frames).
        var base64Frames: Bool
        /// `query:<name>`, `start:<json.path>` (first start message) or `init:<json.path>` (init body) -> value.
        var format: [String: String]
        var consentKey: String
        var host: String
        /// A provider error carrying the key in its free text, and the kind it must reduce to.
        var error: String
        var errorKind: String

        var testDescription: String { preset }
    }

    static let contracts: [Contract] = [
        Contract(
            preset: "elevenlabs", headers: ["xi-api-key": key], sampleRate: 16_000, streamsSpeakers: false,
            base64Frames: true,
            format: ["query:audio_format": "pcm_16000", "query:model_id": "scribe_v2_realtime",
                     "query:include_timestamps": "true"],
            consentKey: "elevenlabs@https://api.elevenlabs.io", host: "api.elevenlabs.io",
            error: #"{"message_type":"auth_error","error":"Invalid API key \#(key)"}"#, errorKind: "auth_error"),
        Contract(
            preset: "deepgram", headers: ["Authorization": "Token \(key)"], sampleRate: 16_000, streamsSpeakers: true,
            base64Frames: false,
            format: ["query:encoding": "linear16", "query:sample_rate": "16000", "query:channels": "1",
                     "query:model": "nova-3"],
            consentKey: "deepgram@https://api.deepgram.com", host: "api.deepgram.com",
            error: #"{"type":"Error","description":"bad key \#(key)","message":"\#(key)"}"#, errorKind: "Error"),
        Contract(
            preset: "assemblyai", headers: ["Authorization": key], sampleRate: 16_000, streamsSpeakers: true,
            base64Frames: false,
            format: ["query:encoding": "pcm_s16le", "query:sample_rate": "16000",
                     "query:speech_model": "universal-3-6-pro"],
            consentKey: "assemblyai@https://streaming.assemblyai.com", host: "streaming.assemblyai.com",
            error: #"{"type":"Error","error_code":1008,"error":"Invalid key \#(key)"}"#, errorKind: "1008"),
        Contract(
            preset: "openai", headers: ["Authorization": "Bearer \(key)"], sampleRate: 24_000, streamsSpeakers: false,
            base64Frames: true,
            format: ["query:intent": "transcription", "start:type": "session.update",
                     "start:session.audio.input.format.type": "audio/pcm",
                     "start:session.audio.input.format.rate": "24000",
                     "start:session.audio.input.transcription.model": "gpt-live-transcribe"],
            consentKey: "openai@https://api.openai.com", host: "api.openai.com",
            error: #"{"type":"error","error":{"type":"invalid_request_error","code":"invalid_api_key","message":"Incorrect API key provided: \#(key)"}}"#,
            errorKind: "invalid_api_key"),
        Contract(
            preset: "xai", headers: ["Authorization": "Bearer \(key)"], sampleRate: 16_000, streamsSpeakers: true,
            base64Frames: false,
            format: ["query:encoding": "pcm", "query:sample_rate": "16000",
                     "query:model": "grok-voice-transcribe-2.0"],
            consentKey: "xai@https://api.x.ai", host: "api.x.ai",
            error: #"{"type":"error","message":"Invalid key \#(key)"}"#, errorKind: "error"),
        Contract(
            preset: "speechmatics", headers: ["Authorization": "Bearer \(key)"], sampleRate: 16_000,
            streamsSpeakers: true, base64Frames: false,
            format: ["start:message": "StartRecognition", "start:audio_format.type": "raw",
                     "start:audio_format.encoding": "pcm_s16le", "start:audio_format.sample_rate": "16000",
                     "start:transcription_config.model": "enhanced"],
            consentKey: "speechmatics@https://eu.rt.speechmatics.com", host: "eu.rt.speechmatics.com",
            error: #"{"message":"Error","type":"not_authorised","reason":"Key \#(key) is not valid"}"#,
            errorKind: "not_authorised"),
        Contract(
            preset: "soniox", headers: ["Authorization": "Bearer \(key)"], sampleRate: 16_000, streamsSpeakers: true,
            base64Frames: false,
            format: ["start:audio_format": "pcm_s16le", "start:sample_rate": "16000", "start:num_channels": "1",
                     "start:model": "stt-rt-v6"],
            consentKey: "soniox@https://stt-rt.soniox.com", host: "stt-rt.soniox.com",
            error: #"{"tokens":[],"error_code":401,"error_type":"unauthenticated","error_message":"Invalid key \#(key)"}"#,
            errorKind: "unauthenticated"),
        Contract(
            preset: "gladia", headers: [:], sampleRate: 16_000, streamsSpeakers: false, base64Frames: false,
            format: ["init:encoding": "wav/pcm", "init:sample_rate": "16000", "init:bit_depth": "16",
                     "init:channels": "1", "init:model": "solaria-1"],
            consentKey: "gladia@https://api.gladia.io", host: "api.gladia.io",
            error: #"{"status_code":401,"exception":"Unauthorized","message":"Invalid key \#(key)"}"#,
            errorKind: "Unauthorized"),
    ]

    /// A Gladia `POST /v2/live` answer, in the documented shape: the wss URL carries only the session token.
    static let gladiaAnswer = Data(#"""
    {"id":"4a39145c-2844-4557-8f34-34883f7be7d9","created_at":"2024-10-10T14:35:28.100Z","url":"wss://api.gladia.io/v2/live?token=4a39145c-2844-4557-8f34-34883f7be7d9"}
    """#.utf8)

    @Test(arguments: contracts)
    func keyTravelsOnlyInHeaders(_ row: Contract) throws {
        var adapter = try adapter(row.preset)

        #expect(adapter.headers == row.headers)
        let url = try socketURL(adapter, row.preset)
        #expect(!url.absoluteString.contains(key))
        let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(!query.contains { $0.value?.contains(key) == true || $0.name.contains(key) })

        // Frame audio first, so commit-style finalize messages (ElevenLabs, OpenAI) are not empty.
        let framed = adapter.frame(CloudRealtime.pcm16([Float](repeating: 0.1, count: adapter.sampleRate / 5)))
        let outgoing = try adapter.startMessages() + [framed] + adapter.finalize(at: 0.2) + adapter.closeMessages()
            + (adapter.keepAlive.map { [$0.message] } ?? [])
        for message in outgoing {
            #expect(!String(decoding: CloudRealtime.payload(message), as: UTF8.self).contains(key))
        }

        if row.preset == "gladia" {
            let request = try #require(try adapter.sessionRequest())
            #expect(request.httpMethod == "POST")
            #expect(request.url?.absoluteString == "https://api.gladia.io/v2/live")
            #expect(request.value(forHTTPHeaderField: "x-gladia-key") == key)
            #expect(url.absoluteString == "wss://api.gladia.io/v2/live?token=4a39145c-2844-4557-8f34-34883f7be7d9")
        } else {
            #expect(try adapter.sessionRequest() == nil)
        }
    }

    @Test(arguments: contracts)
    func asksForTheVerifiedAudioFormat(_ row: Contract) throws {
        var adapter = try adapter(row.preset)

        #expect(adapter.sampleRate == row.sampleRate)
        #expect(adapter.streamsSpeakers == row.streamsSpeakers)
        let pcm = CloudRealtime.pcm16([0.25, -0.25])
        switch adapter.frame(pcm) {
        case .data(let data):
            #expect(!row.base64Frames && data == pcm)
        case .text(let text):
            #expect(row.base64Frames && text.contains(pcm.base64EncodedString()))
        }

        let url = try socketURL(adapter, row.preset)
        let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        let start = try adapter.startMessages().first.map { try jsonObject(CloudRealtime.payload($0)) }
        let body = try adapter.sessionRequest()?.httpBody.map(jsonObject)
        for (spec, expected) in row.format {
            let (source, path) = (spec.prefix { $0 != ":" }, String(spec.drop { $0 != ":" }.dropFirst()))
            let value: String? = switch source {
            case "query": query.first { $0.name == path }?.value
            case "start": start.flatMap { lookup(path, in: $0) }
            default: body.flatMap { lookup(path, in: $0) }
            }
            #expect(value == expected, "\(spec)")
        }
    }

    @Test(arguments: contracts)
    func socketHostConsentAndErrorsAreGuarded(_ row: Contract) throws {
        var adapter = try adapter(row.preset)
        let config = try preset(row.preset)

        let destination = try #require(CloudRealtime.destination(for: config))
        #expect(destination.consentKey == row.consentKey)
        #expect(destination.host == row.host)

        let url = try socketURL(adapter, row.preset)
        #expect(allows(url, adapter))
        for refused in ["ws://\(row.host)/v1", "https://\(row.host)/v1", "wss://evil.example/v1",
                        "wss://\(row.host).evil.example/v1", "wss://\(row.host):8443/v1"] {
            #expect(!allows(try #require(URL(string: refused)), adapter), "\(refused)")
        }

        let events = try adapter.parse(.text(row.error))
        #expect(events == [.failed(kind: row.errorKind)])
        #expect(!"\(events)".contains(key))
    }

    /// An init answer is external: one pointing at another host is refused before any socket opens.
    @Test func gladiaSocketOnAnotherHostIsRefused() async throws {
        let adapter = try adapter("gladia")
        let evil = Data(#"{"id":"x","created_at":"2024-10-10T14:35:28.100Z","url":"wss://evil.example/v2/live?token=x"}"#.utf8)

        let url = try adapter.socketURL(sessionAnswer: evil)
        #expect(!allows(url, adapter))
        await #expect(throws: (any Error).self) { _ = try await RealtimeConnection.connect(adapter, url: url) }
        #expect(throws: (any Error).self) { _ = try adapter.socketURL(sessionAnswer: nil) }
    }

    @Test func catalogCoversOnlyTheRealtimeProviders() throws {
        for row in Self.contracts {
            var config = try preset(row.preset)
            let models = CloudRealtime.models(for: config)
            #expect(!models.isEmpty, "\(row.preset)")
            #expect(CloudRealtime.defaultModel(for: config) == models.first)
            #expect(CloudRealtime.model(for: config) == models.first)
            config.realtimeModel = "  custom-model \n"
            #expect(CloudRealtime.model(for: config) == "custom-model")
        }
        for id in ["mistral", "groq", "gemini", "anthropic", "ollama"] {
            let config = try preset(id)
            #expect(!CloudRealtime.supports(config), "\(id)")
            #expect(CloudRealtime.destination(for: config) == nil)
            #expect(CloudRealtime.make(config: config, apiKey: key, language: nil, diarize: false) == nil)
        }
    }

    // MARK: - Table 2: recorded messages parse

    struct Step: Sendable {
        var message: String
        var events: [RealtimeEvent]
    }

    struct Recording: Sendable, CustomTestStringConvertible {
        var name: String
        var preset: String
        /// Stream seconds to commit before the steps (OpenAI: the app's commit gives a turn its span).
        var commitAt: Double?
        var steps: [Step]

        var testDescription: String { name }
    }

    static let recordings: [Recording] = [
        Recording(name: "elevenlabs", preset: "elevenlabs", steps: [
            // Schema-derived (B.1 has no verbatim messages): constructed from documented fields.
            Step(message: #"{"message_type": "partial_transcript", "text": "hello wor"}"#,
                 events: [.partial(text: "hello wor", start: nil, end: nil, speaker: nil)]),
            // Schema-derived: constructed from documented fields.
            Step(message: #"""
            {"message_type": "committed_transcript_with_timestamps", "text": "hello world", "language_code": "en",
             "words": [{"text": "hello", "start": 0.12, "end": 0.48, "type": "word", "logprob": -0.05},
                       {"text": " ", "start": 0.48, "end": 0.52, "type": "spacing", "logprob": 0.0},
                       {"text": "world", "start": 0.52, "end": 0.9, "type": "word", "logprob": -0.1}]}
            """#, events: [.final(CloudSegment(start: 0.12, end: 0.9, text: "hello world", language: "en"))]),
        ]),
        Recording(name: "deepgram", preset: "deepgram", steps: [
            // Verbatim (interim-results.md): no `type`, times from `start` + `duration`.
            Step(message: #"""
            {"channel_index": [0, 1], "duration": 1.039875, "start": 0, "is_final": false,
             "channel": {"alternatives": [{"transcript": "another big", "confidence": 0.9600255, "words": [
               {"word": "another", "start": 0.2971154, "end": 0.7971154, "confidence": 0.9588303},
               {"word": "big", "start": 0.85173076, "end": 1.039875, "confidence": 0.9600255}]}]}}
            """#, events: [.partial(text: "another big", start: 0, end: 1.039875, speaker: nil)]),
            // Verbatim (live-streaming-audio.md, trimmed to 2 words): the final spans its words.
            Step(message: #"""
            {"type": "Results", "channel_index": [0, 1], "duration": 1.98, "start": 5.99, "is_final": true,
             "speech_final": true, "channel": {"alternatives": [{"transcript": "Tell me more about this.",
               "confidence": 0.99964225, "words": [
               {"word": "tell", "start": 6.0699997, "end": 6.3499994, "confidence": 0.99782443, "punctuated_word": "Tell"},
               {"word": "me", "start": 6.3499994, "end": 6.6299996, "confidence": 0.9998324, "punctuated_word": "me"}]}]},
             "metadata": {"request_id": "52cc0efe-fa77-4aa7-b79c-0dda09de2f14"}, "from_finalize": false}
            """#, events: [.final(CloudSegment(start: 6.0699997, end: 6.6299996, text: "Tell me more about this."))]),
            // Verbatim (utterance-end.md): `utterance_end_ms` asks for it; its array `channel` gives nothing.
            Step(message: #"{"channel":[0,1],"last_word_end":2.395,"type":"UtteranceEnd"}"#, events: []),
            // Verbatim (speech-started.md): also an array `channel`.
            Step(message: #"{"type":"SpeechStarted","channel":[0,1],"timestamp":9.54}"#, events: []),
            // Constructed from documented fields around the verbatim diarized word (diarization.md).
            Step(message: #"""
            {"type": "Results", "start": 15.2, "duration": 0.2, "is_final": true, "channel": {"alternatives": [
              {"transcript": "hello", "words": [
                {"word":"hello","start":15.259043,"end":15.338787,"confidence":0.9721591,"speaker":0}]}]}}
            """#, events: [.final(CloudSegment(start: 15.259043, end: 15.338787, text: "hello", speaker: "0"))]),
        ]),
        Recording(name: "assemblyai", preset: "assemblyai", steps: [
            // Verbatim (message-sequence.md): word times in ms.
            Step(message: #"""
            {"turn_order": 0, "turn_is_formatted": false, "end_of_turn": false, "transcript": "My name is",
             "end_of_turn_confidence": 0, "words": [
               {"start": 1216, "end": 1627, "text": "My", "confidence": 0.956314, "word_is_final": false},
               {"start": 1668, "end": 2490, "text": "name", "confidence": 0.999393, "word_is_final": false},
               {"start": 2531, "end": 3067, "text": "is", "confidence": 0.753325, "word_is_final": false}],
             "utterance": "", "type": "Turn"}
            """#, events: [.partial(text: "My name is", start: 1.216, end: 3.067, speaker: nil)]),
            // Verbatim (message-sequence.md): final = end_of_turn and turn_is_formatted.
            Step(message: #"""
            {"turn_order": 0, "turn_is_formatted": true, "end_of_turn": true, "transcript": "My name is Sonny.",
             "end_of_turn_confidence": 1, "words": [
               {"start": 1216, "end": 1635, "text": "My", "confidence": 0.956583, "word_is_final": true},
               {"start": 1676, "end": 2515, "text": "name", "confidence": 0.999199, "word_is_final": true},
               {"start": 2556, "end": 2975, "text": "is", "confidence": 0.999535, "word_is_final": true},
               {"start": 3016, "end": 4155, "text": "Sonny.", "confidence": 0.316031, "word_is_final": true}],
             "utterance": "My name is Sonny.", "type": "Turn"}
            """#, events: [.final(CloudSegment(start: 1.216, end: 4.155, text: "My name is Sonny."))]),
            // Constructed from documented fields (label-speakers-and-separate-channels.md): an early "PENDING"
            // label is no speaker yet.
            Step(message: #"""
            {"type":"Turn","turn_order":1,"end_of_turn":false,"turn_is_formatted":false,"transcript":"Different",
             "speaker_label":"PENDING","words":[{"text":"Different","speaker":"PENDING","word_is_final":false,"start":145,"end":290}]}
            """#, events: [.partial(text: "Different", start: 0.145, end: 0.29, speaker: nil)]),
            // Constructed from documented fields (same page): the settled label.
            Step(message: #"""
            {"type":"Turn","turn_order":1,"end_of_turn":true,"turn_is_formatted":true,"transcript":"Different.",
             "speaker_label":"A","speaker_confidence":0.972431,
             "words":[{"text":"Different.","speaker":"A","word_is_final":true,"start":145,"end":290,"confidence":0.844642}]}
            """#, events: [.final(CloudSegment(start: 0.145, end: 0.29, text: "Different.", speaker: "A"))]),
        ]),
        Recording(name: "openai", preset: "openai", commitAt: 1.5, steps: [
            // Constructed from documented fields (server-events.md): the commit's acknowledgement.
            Step(message: #"{"type":"input_audio_buffer.committed","event_id":"event_1","item_id":"item_CCXGQ4e1ht4cOraEYcuR2"}"#,
                 events: []),
            // Verbatim (server-events.md): deltas have no times.
            Step(message: #"""
            {"type": "conversation.item.input_audio_transcription.delta", "event_id": "event_CCXGRxsAimPAs8kS2Wc7Z",
             "item_id": "item_CCXGQ4e1ht4cOraEYcuR2", "content_index": 0, "delta": "Hey", "obfuscation": "aLxx0jTEciOGe"}
            """#, events: [.partial(text: "Hey", start: nil, end: nil, speaker: nil)]),
            // Verbatim (server-events.md): the final takes the commit's span.
            Step(message: #"""
            {"type": "conversation.item.input_audio_transcription.completed", "event_id": "event_CCXGRvtUVrax5SJAnNOWZ",
             "item_id": "item_CCXGQ4e1ht4cOraEYcuR2", "content_index": 0, "transcript": "Hey, can you hear me?",
             "usage": {"type": "tokens", "total_tokens": 22, "input_tokens": 13, "output_tokens": 9}}
            """#, events: [.final(CloudSegment(start: 0, end: 1.5, text: "Hey, can you hear me?"))]),
        ]),
        Recording(name: "xai", preset: "xai", steps: [
            // Verbatim (ws.json).
            Step(message: #"{"type":"transcript.created","id":"83f2f6fd-1cd1-4747-bc52-cebddc961c32"}"#,
                 events: [.started(expiresAt: nil)]),
            // Constructed from documented fields (ws.json `transcript.partial` with is_final false).
            Step(message: #"""
            {"type":"transcript.partial","text":"The balance","words":[{"text":"The","start":0.24,"end":0.48},
             {"text":"balance","start":0.48,"end":0.96}],"is_final":false,"speech_final":false,"start":0.0,"duration":0.96}
            """#, events: [.partial(text: "The balance", start: 0, end: 0.96, speaker: nil)]),
            // Verbatim (ws.json): a chunk final.
            Step(message: #"""
            {"type":"transcript.partial","text":"The balance is $167,983.15.","words":[
             {"text":"The","start":0.24,"end":0.48,"confidence":0.95},{"text":"balance","start":0.48,"end":0.96,"confidence":0.92},
             {"text":"is","start":0.96,"end":1.12,"confidence":0.98},{"text":"$167,983.15.","start":1.12,"end":3.2,"confidence":0.89}],
             "is_final":true,"speech_final":false,"start":0.0,"duration":3.2}
            """#, events: [.final(CloudSegment(start: 0.24, end: 3.2, text: "The balance is $167,983.15."))]),
            // Constructed from documented fields: the utterance final repeats its chunk finals' words; only the new
            // one is emitted, with its speaker.
            Step(message: #"""
            {"type":"transcript.partial","text":"The balance is $167,983.15. Thanks.","words":[
             {"text":"The","start":0.24,"end":0.48},{"text":"balance","start":0.48,"end":0.96},
             {"text":"is","start":0.96,"end":1.12},{"text":"$167,983.15.","start":1.12,"end":3.2},
             {"text":"Thanks.","start":3.4,"end":3.9,"speaker":1}],"is_final":true,"speech_final":true,"start":0.0,"duration":3.9}
            """#, events: [.final(CloudSegment(start: 3.4, end: 3.9, text: "Thanks.", speaker: "1"))]),
            // Constructed from documented fields: an utterance final with only repeated words adds nothing.
            Step(message: #"""
            {"type":"transcript.partial","text":"Thanks.","words":[{"text":"Thanks.","start":3.4,"end":3.9,"speaker":1}],
             "is_final":true,"speech_final":true,"start":3.4,"duration":0.5}
            """#, events: []),
            // Verbatim (ws.json).
            Step(message: #"{"type":"transcript.done","text":"","words":[],"duration":6.43}"#, events: [.ended]),
        ]),
        Recording(name: "speechmatics", preset: "speechmatics", steps: [
            // Schema-derived (partials have the final's structure): constructed from documented fields.
            Step(message: #"""
            {"message": "AddPartialTranscript", "format": "2.9",
             "metadata": {"transcript": "full tell", "start_time": 0.11, "end_time": 0.62},
             "results": [
               {"type": "word", "start_time": 0.11, "end_time": 0.4, "alternatives": [{"content": "full", "confidence": 0.0, "speaker": "S1"}]},
               {"type": "word", "start_time": 0.41, "end_time": 0.62, "alternatives": [{"content": "tell", "confidence": 0.0, "speaker": "S1"}]}]}
            """#, events: [.partial(text: "full tell", start: 0.11, end: 0.62, speaker: "S1")]),
            // Verbatim (cpu-speech-to-text via context7), without diarization.
            Step(message: #"""
            {"message": "AddTranscript", "format": "2.9",
             "metadata": {"transcript": "full tell radar", "start_time": 0.11, "end_time": 1.07},
             "results": [
               {"type": "word", "start_time": 0.11, "end_time": 0.4, "alternatives": [{"content": "full", "confidence": 0.7}]},
               {"type": "word", "start_time": 0.41, "end_time": 0.62, "alternatives": [{"content": "tell", "confidence": 0.6}]},
               {"type": "word", "start_time": 0.65, "end_time": 1.07, "alternatives": [{"content": "radar", "confidence": 1.0}]}]}
            """#, events: [.final(CloudSegment(start: 0.11, end: 1.07, text: "full tell radar"))]),
            // Constructed from documented fields: with diarization each alternative has its speaker.
            Step(message: #"""
            {"message": "AddTranscript", "format": "2.9",
             "metadata": {"transcript": "radar", "start_time": 1.2, "end_time": 1.6},
             "results": [{"type": "word", "start_time": 1.2, "end_time": 1.6,
                          "alternatives": [{"content": "radar", "confidence": 1.0, "speaker": "S2"}]}]}
            """#, events: [.final(CloudSegment(start: 1.2, end: 1.6, text: "radar", speaker: "S2"))]),
            // Constructed from documented fields (realtime WebSocket reference): `ForceEndOfUtterance`'s answer,
            // metadata without a transcript, gives nothing.
            Step(message: #"{"message":"EndOfUtterance","format":"2.9","metadata":{"start_time":1.6,"end_time":1.6},"forced":true}"#,
                 events: []),
            // Constructed from documented fields.
            Step(message: #"{"message":"EndOfTranscript"}"#, events: [.ended]),
        ]),
        // Verbatim (real-time-transcription, "Example token evolution"): this example has no token times.
        Recording(name: "soniox partial", preset: "soniox", steps: [
            Step(message: #"""
            {"tokens": [{"text": "How", "is_final": true}, {"text": " ", "is_final": true},
                        {"text": "are", "is_final": false}, {"text": " ", "is_final": false}, {"text": "you", "is_final": false}]}
            """#, events: [.partial(text: "How are you", start: 0, end: 0, speaker: nil)]),
        ]),
        Recording(name: "soniox final", preset: "soniox", steps: [
            // Verbatim (websocket-api.mdx): final tokens (ms) are held until the utterance ends.
            Step(message: #"""
            {"tokens": [{"text": "Hello", "start_ms": 600, "end_ms": 760, "confidence": 0.97, "is_final": true, "speaker": "1"}],
             "final_audio_proc_ms": 760, "total_audio_proc_ms": 880}
            """#, events: [.partial(text: "Hello", start: 0.6, end: 0.76, speaker: "1")]),
            // Constructed from documented fields: the manual-finalize marker flushes them.
            Step(message: #"{"tokens": [{"text": "<fin>", "is_final": true}], "final_audio_proc_ms": 900, "total_audio_proc_ms": 900}"#,
                 events: [.final(CloudSegment(start: 0.6, end: 0.76, text: "Hello", speaker: "1"))]),
            // Verbatim (websocket-api.mdx): end of stream.
            Step(message: #"{"tokens":[],"final_audio_proc_ms":1560,"total_audio_proc_ms":1680,"finished":true}"#,
                 events: [.ended]),
        ]),
        Recording(name: "gladia", preset: "gladia", steps: [
            // Derived from the final (partial-transcripts.md: same shape, is_final false): constructed from
            // documented fields.
            Step(message: #"""
            {"type":"transcript","session_id":"de0a341d-c69f-4e15-a649-7b3f49e211f0","created_at":"2024-10-10T14:35:28.100Z",
             "data":{"id":"00_00000000","is_final":false,"utterance":{"text":" Hello","start":0.188,"end":0.735,"language":"en",
             "confidence":0.9,"channel":0,"words":[{"word":" Hello","start":0.188,"end":0.735,"confidence":0.9}]}}}
            """#, events: [.partial(text: "Hello", start: 0.188, end: 0.735, speaker: nil)]),
            // Verbatim (live-stt/migration-from-v1.md, V2 tab).
            Step(message: #"""
            {"type": "transcript", "session_id": "de0a341d-c69f-4e15-a649-7b3f49e211f0", "created_at": "2024-10-10T14:35:28.387Z",
             "data": {"id": "00_00000000", "is_final": true, "utterance": {"text": " Hello world", "start": 0.188, "end": 1.284,
               "language": "en", "confidence": 1, "channel": 0, "words": [
                 {"word": " Hello", "start": 0.188, "end": 0.735, "confidence": 1},
                 {"word": " world", "start": 0.736, "end": 1.284, "confidence": 1}]}}}
            """#, events: [.final(CloudSegment(start: 0.188, end: 1.284, text: "Hello world", language: "en"))]),
        ]),
    ]

    @Test(arguments: recordings)
    func recordedMessagesParse(_ row: Recording) throws {
        var adapter = try adapter(row.preset)
        if let commitAt = row.commitAt { #expect(!adapter.finalize(at: commitAt).isEmpty) }
        for (index, step) in row.steps.enumerated() {
            #expect(try adapter.parse(.text(step.message)) == step.events, "step \(index)")
        }
    }

    // MARK: - Stateful outgoing messages

    /// Frame 1.5 s -> commit -> committed{item_id} -> completed is a final over the commit's span; closing commits
    /// the rest and ends once its transcript is in.
    @Test func openAITurnsTakeTheirCommitSpans() throws {
        var adapter = try adapter("openai")
        let second = CloudRealtime.pcm16([Float](repeating: 0, count: 24_000))

        _ = adapter.frame(second + second.prefix(24_000))
        #expect(adapter.finalize(at: 1.5) == [.text(#"{"type":"input_audio_buffer.commit"}"#)])
        #expect(adapter.finalize(at: 1.55).isEmpty)  // under 100 ms since the last commit
        #expect(try adapter.parse(.text(#"{"type":"input_audio_buffer.committed","item_id":"a"}"#)) == [])
        #expect(try adapter.parse(.text(
            #"{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":" One. "}"#))
            == [.final(CloudSegment(start: 0, end: 1.5, text: "One."))])

        _ = adapter.frame(second + second.prefix(24_000))
        #expect(adapter.closeMessages() == [.text(#"{"type":"input_audio_buffer.commit"}"#)])
        #expect(try adapter.parse(.text(#"{"type":"input_audio_buffer.committed","item_id":"b"}"#)) == [])
        #expect(try adapter.parse(.text(
            #"{"type":"conversation.item.input_audio_transcription.completed","item_id":"b","transcript":"Two."}"#))
            == [.final(CloudSegment(start: 1.5, end: 3, text: "Two.")), .ended])
    }

    /// OpenAI sends no end message: pause (commit, completed), then Stop with no new audio commits nothing, so the
    /// stream is drained at once instead of waiting for an `ended` that never comes.
    @Test func openAICloseWithNothingLeftIsDrained() throws {
        var adapter = try adapter("openai")
        _ = adapter.frame(CloudRealtime.pcm16([Float](repeating: 0, count: 24_000)))
        #expect(!adapter.finalize(at: 1).isEmpty)
        _ = try adapter.parse(.text(#"{"type":"input_audio_buffer.committed","item_id":"a"}"#))
        _ = try adapter.parse(.text(
            #"{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":"One."}"#))
        #expect(!adapter.isDrained)

        #expect(adapter.closeMessages().isEmpty)
        #expect(adapter.isDrained)
    }

    /// OpenAI closing while a commit waits for its transcript is drained only once that transcript is in.
    @Test func openAICloseWaitsForThePendingTranscript() throws {
        var adapter = try adapter("openai")
        _ = adapter.frame(CloudRealtime.pcm16([Float](repeating: 0, count: 24_000)))
        #expect(!adapter.finalize(at: 1).isEmpty)
        #expect(adapter.closeMessages().isEmpty)
        #expect(!adapter.isDrained)

        _ = try adapter.parse(.text(#"{"type":"input_audio_buffer.committed","item_id":"a"}"#))
        #expect(try adapter.parse(.text(
            #"{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":"One."}"#))
            == [.final(CloudSegment(start: 0, end: 1, text: "One.")), .ended])
        #expect(adapter.isDrained)
    }

    /// ElevenLabs sends no end message: the timed answer to the close commit ends the stream, even without words.
    @Test(arguments: [
        #"{"message_type":"committed_transcript_with_timestamps","text":"hi","language_code":"en","words":[{"text":"hi","start":0.1,"end":0.3,"type":"word"}]}"#,
        #"{"message_type":"committed_transcript_with_timestamps","text":"","words":[]}"#,
    ])
    func elevenLabsCloseCommitAnsweredEnds(_ answer: String) throws {
        var adapter = try adapter("elevenlabs")
        _ = adapter.frame(CloudRealtime.pcm16([Float](repeating: 0.1, count: 3_200)))
        #expect(adapter.closeMessages().count == 1)
        #expect(!adapter.isDrained)
        #expect(try adapter.parse(.text(#"{"message_type":"committed_transcript","text":"hi"}"#)) == [])
        #expect(!adapter.isDrained)

        let events = try adapter.parse(.text(answer))
        #expect(events.last == .ended)
        #expect(events.dropLast().allSatisfy { if case .final = $0 { true } else { false } })
        #expect(adapter.isDrained)
    }

    /// ElevenLabs closing with nothing uncommitted and no commit waiting is drained at once; a pause commit still
    /// waiting for its answer keeps it open.
    @Test func elevenLabsCloseWithNothingOutstandingIsDrained() throws {
        var fresh = try adapter("elevenlabs")
        #expect(fresh.closeMessages().isEmpty)
        #expect(fresh.isDrained)

        var paused = try adapter("elevenlabs")
        _ = paused.frame(CloudRealtime.pcm16([Float](repeating: 0.1, count: 3_200)))
        #expect(paused.finalize(at: 0.2).count == 1)
        #expect(paused.closeMessages().isEmpty)
        #expect(!paused.isDrained)
        #expect(try paused.parse(.text(#"{"message_type":"committed_transcript_with_timestamps","words":[]}"#))
            == [.ended])
        #expect(paused.isDrained)
    }

    @Test func speechmaticsEndOfStreamCountsTheFramesSent() throws {
        var adapter = try adapter("speechmatics")
        for _ in 0..<3 { _ = adapter.frame(CloudRealtime.pcm16([0, 0.1])) }

        #expect(adapter.closeMessages() == [.text(#"{"message":"EndOfStream","last_seq_no":3}"#)])
    }

    /// Deepgram closes a socket after 10 s without audio or a KeepAlive; its docs ask for one every 3-5 s.
    @Test func deepgramKeepsTheSocketAlive() throws {
        let keepAlive = try #require(try adapter("deepgram").keepAlive)

        #expect(keepAlive.message == .text(#"{"type":"KeepAlive"}"#))
        #expect(keepAlive.interval >= .seconds(3) && keepAlive.interval <= .seconds(5))
    }

    // MARK: - PCM, resampler, error kinds

    @Test func pcm16ClampsAndRounds() {
        let data = CloudRealtime.pcm16([0, 1, -1, 2, -2, 0.5, -0.5, .nan, .infinity])
        let values = stride(from: 0, to: data.count, by: 2).map {
            Int16(littleEndian: Int16(bitPattern: UInt16(data[$0]) | UInt16(data[$0 + 1]) << 8))
        }

        #expect(values == [0, 32_767, -32_767, 32_767, -32_767, 16_384, -16_384, 0, 0])
        #expect(CloudRealtime.pcm16([1]) == Data([0xFF, 0x7F]))
    }

    @Test func resamplerChunkedMatchesOneShot() {
        let ramp = (0..<1_600).map { Float($0) / 1_600 }
        var oneShot = RealtimeResampler(from: 16_000, to: 24_000)
        let whole = oneShot.process(ramp)

        var chunked = RealtimeResampler(from: 16_000, to: 24_000)
        var parts: [Float] = []
        var index = 0
        for size in [1, 7, 160, 333, 99, 800, 200] {
            parts += chunked.process(Array(ramp[index..<index + size]))
            index += size
        }

        #expect(index == ramp.count)
        #expect(parts == whole)
        #expect(abs(whole.count - ramp.count * 3 / 2) <= 2)
        for k in [0, 1, 2, 3, 1_000, whole.count - 1] {
            #expect(abs(whole[k] - Float(k) * 2 / 3 / 1_600) < 1e-5, "sample \(k)")
        }
    }

    @Test(arguments: [
        (nil, "error"), ("", "error"), ("  ", "error"), ("!?<>", "error"),
        ("auth_error", "auth_error"), ("3007", "3007"), ("v2.live-session_x", "v2.live-session_x"),
        ("not authorised: <b>Bad</b>", "notauthorisedbBadb"), ("Ünicödé-1", "nicd-1"),
        (String(repeating: "a", count: 50), String(repeating: "a", count: 40)),
    ] as [(String?, String)])
    func errorKindIsSanitized(raw: String?, kind: String) {
        #expect(CloudRealtime.errorKind(raw) == kind)
    }

    // MARK: - Helpers

    private func preset(_ id: String) throws -> ProviderConfig {
        try #require(ProviderConfig.presets.first { $0.id == id })
    }

    /// The adapter the catalog builds for a preset, for Vietnamese with diarization on (Speechmatics needs a
    /// language).
    private func adapter(_ id: String) throws -> any RealtimeAdapter {
        try #require(CloudRealtime.make(config: try preset(id), apiKey: key, language: "vi", diarize: true))
    }

    private func socketURL(_ adapter: any RealtimeAdapter, _ preset: String) throws -> URL {
        try adapter.socketURL(sessionAnswer: preset == "gladia" ? Self.gladiaAnswer : nil)
    }

    /// `CloudRealtime.allowsSocket` with the existential opened (the `#expect` macro cannot open it).
    private func allows(_ url: URL, _ adapter: any RealtimeAdapter) -> Bool {
        CloudRealtime.allowsSocket(url, for: adapter)
    }

    private func jsonObject(_ data: Data) throws -> Any {
        try JSONSerialization.jsonObject(with: data)
    }

    /// The value at a dotted path of decoded JSON, as text (numbers as their digits).
    private func lookup(_ path: String, in object: Any) -> String? {
        var current: Any? = object
        for key in path.split(separator: ".") { current = (current as? [String: Any])?[String(key)] }
        switch current {
        case let string as String: return string
        case let number as NSNumber: return number.stringValue
        default: return nil
        }
    }
}
