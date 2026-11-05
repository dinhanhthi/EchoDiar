using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using EchoDiar.AI;
using EchoDiar.AI.Cloud;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.AI;

// Guards the realtime contract of the 8 providers (stt-protocols.md part B): where the key travels, the audio format
// and rate asked for, the consent destination and the socket host check (table 1), and how recorded server messages
// parse into partials and finals with stream-relative times and speakers (table 2) (CloudRealtimeTests.swift). No
// network: only the pure adapters, the catalog and the refusal guards of RealtimeConnection run.
public sealed class CloudRealtimeTests
{
    /// <summary>A fake long-lived key; it must never show up in a URL, an outgoing message or an error kind.</summary>
    private const string Key = "sk-live-SECRET-0000";

    // Table 1: request and security contract.

    /// <param name="Headers">Handshake headers (Gladia: none, the key goes only in the init call's header).</param>
    /// <param name="Base64Frames">Audio as base64 in a JSON text frame (else raw binary frames).</param>
    /// <param name="Format"><c>query:&lt;name&gt;</c>, <c>start:&lt;json.path&gt;</c> (first start message) or
    /// <c>init:&lt;json.path&gt;</c> (init body) -> value.</param>
    /// <param name="Error">A provider error carrying the key in its free text, and the kind it must reduce to.</param>
    private sealed record Contract(
        Dictionary<string, string> Headers, int SampleRate, bool StreamsSpeakers, bool Base64Frames,
        Dictionary<string, string> Format, string ConsentKey, string Host, string Error, string ErrorKind);

    private static readonly Dictionary<string, Contract> Contracts = new()
    {
        ["elevenlabs"] = new(
            new() { ["xi-api-key"] = Key }, 16_000, false, true,
            new() { ["query:audio_format"] = "pcm_16000", ["query:model_id"] = "scribe_v2_realtime", ["query:include_timestamps"] = "true" },
            "elevenlabs@https://api.elevenlabs.io", "api.elevenlabs.io",
            $$"""{"message_type":"auth_error","error":"Invalid API key {{Key}}"}""", "auth_error"),
        ["deepgram"] = new(
            new() { ["Authorization"] = $"Token {Key}" }, 16_000, true, false,
            new() { ["query:encoding"] = "linear16", ["query:sample_rate"] = "16000", ["query:channels"] = "1", ["query:model"] = "nova-3" },
            "deepgram@https://api.deepgram.com", "api.deepgram.com",
            $$"""{"type":"Error","description":"bad key {{Key}}","message":"{{Key}}"}""", "Error"),
        ["assemblyai"] = new(
            new() { ["Authorization"] = Key }, 16_000, true, false,
            new() { ["query:encoding"] = "pcm_s16le", ["query:sample_rate"] = "16000", ["query:speech_model"] = "universal-3-6-pro" },
            "assemblyai@https://streaming.assemblyai.com", "streaming.assemblyai.com",
            $$"""{"type":"Error","error_code":1008,"error":"Invalid key {{Key}}"}""", "1008"),
        ["openai"] = new(
            new() { ["Authorization"] = $"Bearer {Key}" }, 24_000, false, true,
            new()
            {
                ["query:intent"] = "transcription", ["start:type"] = "session.update",
                ["start:session.audio.input.format.type"] = "audio/pcm", ["start:session.audio.input.format.rate"] = "24000",
                ["start:session.audio.input.transcription.model"] = "gpt-live-transcribe",
            },
            "openai@https://api.openai.com", "api.openai.com",
            $$$"""{"type":"error","error":{"type":"invalid_request_error","code":"invalid_api_key","message":"Incorrect API key provided: {{{Key}}}"}}""",
            "invalid_api_key"),
        ["xai"] = new(
            new() { ["Authorization"] = $"Bearer {Key}" }, 16_000, true, false,
            new() { ["query:encoding"] = "pcm", ["query:sample_rate"] = "16000", ["query:model"] = "grok-voice-transcribe-2.0" },
            "xai@https://api.x.ai", "api.x.ai",
            $$"""{"type":"error","message":"Invalid key {{Key}}"}""", "error"),
        ["speechmatics"] = new(
            new() { ["Authorization"] = $"Bearer {Key}" }, 16_000, true, false,
            new()
            {
                ["start:message"] = "StartRecognition", ["start:audio_format.type"] = "raw",
                ["start:audio_format.encoding"] = "pcm_s16le", ["start:audio_format.sample_rate"] = "16000",
                ["start:transcription_config.model"] = "enhanced",
            },
            "speechmatics@https://eu.rt.speechmatics.com", "eu.rt.speechmatics.com",
            $$"""{"message":"Error","type":"not_authorised","reason":"Key {{Key}} is not valid"}""", "not_authorised"),
        ["soniox"] = new(
            new() { ["Authorization"] = $"Bearer {Key}" }, 16_000, true, false,
            new()
            {
                ["start:audio_format"] = "pcm_s16le", ["start:sample_rate"] = "16000", ["start:num_channels"] = "1",
                ["start:model"] = "stt-rt-v6",
            },
            "soniox@https://stt-rt.soniox.com", "stt-rt.soniox.com",
            $$"""{"tokens":[],"error_code":401,"error_type":"unauthenticated","error_message":"Invalid key {{Key}}"}""", "unauthenticated"),
        ["gladia"] = new(
            [], 16_000, false, false,
            new()
            {
                ["init:encoding"] = "wav/pcm", ["init:sample_rate"] = "16000", ["init:bit_depth"] = "16",
                ["init:channels"] = "1", ["init:model"] = "solaria-1",
            },
            "gladia@https://api.gladia.io", "api.gladia.io",
            $$"""{"status_code":401,"exception":"Unauthorized","message":"Invalid key {{Key}}"}""", "Unauthorized"),
    };

    public static TheoryData<string> ContractNames => new(Contracts.Keys);

    /// <summary>A Gladia <c>POST /v2/live</c> answer, in the documented shape: the wss URL carries only the session token.</summary>
    private static readonly byte[] GladiaAnswer = Encoding.UTF8.GetBytes(
        """{"id":"4a39145c-2844-4557-8f34-34883f7be7d9","created_at":"2024-10-10T14:35:28.100Z","url":"wss://api.gladia.io/v2/live?token=4a39145c-2844-4557-8f34-34883f7be7d9"}""");

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void KeyTravelsOnlyInHeaders(string preset)
    {
        var row = Contracts[preset];
        var adapter = Adapter(preset);

        Assert.Equal(row.Headers.Count, adapter.Headers.Count);
        foreach (var (name, value) in row.Headers) Assert.Equal(value, adapter.Headers.GetValueOrDefault(name));
        var url = SocketUrl(adapter, preset);
        Assert.DoesNotContain(Key, url.AbsoluteUri);
        Assert.DoesNotContain(Key, Uri.UnescapeDataString(url.Query));

        // Frame audio first, so commit-style finalize messages (ElevenLabs, OpenAI) are not empty.
        var framed = adapter.Frame(CloudRealtime.Pcm16(Enumerable.Repeat(0.1f, adapter.SampleRate / 5).ToArray()));
        List<RealtimeMessage> outgoing = [.. adapter.StartMessages(), framed, .. adapter.Finalize(0.2), .. adapter.CloseMessages()];
        if (adapter.KeepAlive is { } keepAlive) outgoing.Add(keepAlive.Message);
        foreach (var message in outgoing) Assert.DoesNotContain(Key, Encoding.UTF8.GetString(CloudRealtime.Payload(message)));

        using var request = adapter.SessionRequest();
        if (preset == "gladia")
        {
            Assert.NotNull(request);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.gladia.io/v2/live", request.RequestUri!.AbsoluteUri);
            Assert.Equal(Key, request.Headers.TryGetValues("x-gladia-key", out var values) ? string.Join(",", values) : null);
            Assert.Equal("wss://api.gladia.io/v2/live?token=4a39145c-2844-4557-8f34-34883f7be7d9", url.AbsoluteUri);
        }
        else
        {
            Assert.Null(request);
        }
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public async Task AsksForTheVerifiedAudioFormat(string preset)
    {
        var row = Contracts[preset];
        var adapter = Adapter(preset);

        Assert.Equal(row.SampleRate, adapter.SampleRate);
        Assert.Equal(row.StreamsSpeakers, adapter.StreamsSpeakers);
        var pcm = CloudRealtime.Pcm16([0.25f, -0.25f]);
        switch (adapter.Frame(pcm))
        {
            case RealtimeMessage.Binary binary:
                Assert.False(row.Base64Frames);
                Assert.Equal(pcm, binary.Data);
                break;
            case RealtimeMessage.Text text:
                Assert.True(row.Base64Frames);
                Assert.Contains(Convert.ToBase64String(pcm), text.Value);
                break;
        }

        var query = Query(SocketUrl(adapter, preset));
        var start = adapter.StartMessages() is [var first, ..] ? JsonNode.Parse(CloudRealtime.Payload(first)) : null;
        using var request = adapter.SessionRequest();
        var body = request?.Content is { } content
            ? JsonNode.Parse(await content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            : null;
        foreach (var (spec, expected) in row.Format)
        {
            var (source, path) = (spec[..spec.IndexOf(':')], spec[(spec.IndexOf(':') + 1)..]);
            var value = source switch
            {
                "query" => query.GetValueOrDefault(path),
                "start" => Lookup(start, path),
                _ => Lookup(body, path),
            };
            Assert.True(expected == value, $"{spec}: {value}");
        }
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void SocketHostConsentAndErrorsAreGuarded(string preset)
    {
        var row = Contracts[preset];
        var adapter = Adapter(preset);

        var destination = CloudRealtime.Destination(Preset(preset));
        Assert.NotNull(destination);
        Assert.Equal(row.ConsentKey, destination.Value.ConsentKey);
        Assert.Equal(row.Host, destination.Value.Host);

        Assert.True(CloudRealtime.AllowsSocket(SocketUrl(adapter, preset), adapter));
        foreach (var refused in new[]
                 {
                     $"ws://{row.Host}/v1", $"https://{row.Host}/v1", "wss://evil.example/v1", $"wss://{row.Host}.evil.example/v1",
                     $"wss://{row.Host}:8443/v1", $"wss://user:pw@{row.Host}/v1",
                 })
            Assert.False(CloudRealtime.AllowsSocket(new Uri(refused), adapter), refused);

        var events = adapter.Parse(new RealtimeMessage.Text(row.Error));
        Assert.Equal([new RealtimeEvent.Failed(row.ErrorKind)], events);
        Assert.DoesNotContain(Key, string.Join(" ", events));
    }

    /// <summary>An init answer is external: one pointing at another host is refused before any socket opens.</summary>
    [Fact]
    public async Task GladiaSocketOnAnotherHostIsRefused()
    {
        var adapter = Adapter("gladia");
        var evil = Encoding.UTF8.GetBytes("""{"id":"x","created_at":"2024-10-10T14:35:28.100Z","url":"wss://evil.example/v2/live?token=x"}""");

        var url = adapter.SocketUrl(evil);
        Assert.False(CloudRealtime.AllowsSocket(url, adapter));
        var refused = await Assert.ThrowsAsync<LLMException>(() =>
            RealtimeConnection.ConnectAsync(adapter, url, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(LLMErrorKind.Provider, refused.Kind);
        Assert.DoesNotContain("evil", refused.Message);
        Assert.DoesNotContain(Key, refused.Message);
    }

    /// <summary>A missing or invalid init answer fails with a fixed text: neither the answer, a URL nor the key.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not json wss://evil.example/?token=x")]
    [InlineData("""{"id":"x","created_at":"2024-10-10T14:35:28.100Z"}""")]
    public void GladiaWithoutASessionUrlFailsWithAFixedText(string? answer)
    {
        var adapter = Adapter("gladia");

        var error = Assert.Throws<LLMException>(() => adapter.SocketUrl(answer is null ? null : Encoding.UTF8.GetBytes(answer)));
        Assert.Equal("Gladia did not start a live session.", error.Message);
        Assert.DoesNotContain(Key, error.Message);
    }

    [Fact]
    public void CatalogCoversOnlyTheRealtimeProviders()
    {
        foreach (var preset in Contracts.Keys)
        {
            var config = Preset(preset);
            var models = CloudRealtime.Models(config);
            Assert.True(models.Count > 0, preset);
            Assert.Equal(models[0], CloudRealtime.DefaultModel(config));
            Assert.Equal(models[0], CloudRealtime.Model(config));
            config.RealtimeModel = "  custom-model \n";
            Assert.Equal("custom-model", CloudRealtime.Model(config));
        }
        foreach (var id in new[] { "mistral", "groq", "gemini", "anthropic", "ollama" })
        {
            var config = Preset(id);
            Assert.False(CloudRealtime.Supports(config), id);
            Assert.Null(CloudRealtime.Destination(config));
            Assert.Null(CloudRealtime.Make(config, Key, null, diarize: false));
        }
    }

    // Table 2: recorded messages parse.

    private sealed record Step(string Message, params RealtimeEvent[] Events);

    /// <param name="CommitAt">Stream seconds to commit before the steps (OpenAI: the app's commit gives a turn its span).</param>
    private sealed record Recording(string Preset, double? CommitAt, params Step[] Steps);

    private static readonly Dictionary<string, Recording> Recordings = new()
    {
        ["elevenlabs"] = new("elevenlabs", null,
            // Schema-derived (B.1 has no verbatim messages): constructed from documented fields.
            new("""{"message_type": "partial_transcript", "text": "hello wor"}""", new RealtimeEvent.Partial("hello wor")),
            // Schema-derived: constructed from documented fields.
            new("""
                {"message_type": "committed_transcript_with_timestamps", "text": "hello world", "language_code": "en",
                 "words": [{"text": "hello", "start": 0.12, "end": 0.48, "type": "word", "logprob": -0.05},
                           {"text": " ", "start": 0.48, "end": 0.52, "type": "spacing", "logprob": 0.0},
                           {"text": "world", "start": 0.52, "end": 0.9, "type": "word", "logprob": -0.1}]}
                """, Final(0.12, 0.9, "hello world", language: "en"))),
        // Constructed from documented fields: realtime words have no speaker, so each sentence is its own final (the
        // diarizer after Stop labels them one by one).
        ["elevenlabs sentences"] = new("elevenlabs", null,
            new Step("""
                {"message_type": "committed_transcript_with_timestamps", "text": "Hi there. Fine.", "language_code": "en",
                 "words": [{"text": "Hi", "start": 0.1, "end": 0.3, "type": "word"},
                           {"text": " ", "start": 0.3, "end": 0.35, "type": "spacing"},
                           {"text": "there.", "start": 0.35, "end": 0.7, "type": "word"},
                           {"text": " ", "start": 0.7, "end": 1.2, "type": "spacing"},
                           {"text": "Fine.", "start": 1.3, "end": 1.6, "type": "word"}]}
                """, Final(0.1, 0.7, "Hi there.", language: "en"), Final(1.3, 1.6, "Fine.", language: "en"))),
        ["deepgram"] = new("deepgram", null,
            // Verbatim (interim-results.md): no `type`, times from `start` + `duration`.
            new("""
                {"channel_index": [0, 1], "duration": 1.039875, "start": 0, "is_final": false,
                 "channel": {"alternatives": [{"transcript": "another big", "confidence": 0.9600255, "words": [
                   {"word": "another", "start": 0.2971154, "end": 0.7971154, "confidence": 0.9588303},
                   {"word": "big", "start": 0.85173076, "end": 1.039875, "confidence": 0.9600255}]}]}}
                """, new RealtimeEvent.Partial("another big", 0, 1.039875)),
            // Verbatim (live-streaming-audio.md, trimmed to 2 words): the final spans its words.
            new("""
                {"type": "Results", "channel_index": [0, 1], "duration": 1.98, "start": 5.99, "is_final": true,
                 "speech_final": true, "channel": {"alternatives": [{"transcript": "Tell me more about this.",
                   "confidence": 0.99964225, "words": [
                   {"word": "tell", "start": 6.0699997, "end": 6.3499994, "confidence": 0.99782443, "punctuated_word": "Tell"},
                   {"word": "me", "start": 6.3499994, "end": 6.6299996, "confidence": 0.9998324, "punctuated_word": "me"}]}]},
                 "metadata": {"request_id": "52cc0efe-fa77-4aa7-b79c-0dda09de2f14"}, "from_finalize": false}
                """, Final(6.0699997, 6.6299996, "Tell me more about this.")),
            // Verbatim (utterance-end.md): `utterance_end_ms` asks for it; its array `channel` gives nothing.
            new("""{"channel":[0,1],"last_word_end":2.395,"type":"UtteranceEnd"}"""),
            // Verbatim (speech-started.md): also an array `channel`.
            new("""{"type":"SpeechStarted","channel":[0,1],"timestamp":9.54}"""),
            // Constructed from documented fields around the verbatim diarized word (diarization.md).
            new("""
                {"type": "Results", "start": 15.2, "duration": 0.2, "is_final": true, "channel": {"alternatives": [
                  {"transcript": "hello", "words": [
                    {"word":"hello","start":15.259043,"end":15.338787,"confidence":0.9721591,"speaker":0}]}]}}
                """, Final(15.259043, 15.338787, "hello", speaker: "0"))),
        ["assemblyai"] = new("assemblyai", null,
            // Verbatim (message-sequence.md): word times in ms.
            new("""
                {"turn_order": 0, "turn_is_formatted": false, "end_of_turn": false, "transcript": "My name is",
                 "end_of_turn_confidence": 0, "words": [
                   {"start": 1216, "end": 1627, "text": "My", "confidence": 0.956314, "word_is_final": false},
                   {"start": 1668, "end": 2490, "text": "name", "confidence": 0.999393, "word_is_final": false},
                   {"start": 2531, "end": 3067, "text": "is", "confidence": 0.753325, "word_is_final": false}],
                 "utterance": "", "type": "Turn"}
                """, new RealtimeEvent.Partial("My name is", 1.216, 3.067)),
            // Verbatim (message-sequence.md): final = end_of_turn and turn_is_formatted.
            new("""
                {"turn_order": 0, "turn_is_formatted": true, "end_of_turn": true, "transcript": "My name is Sonny.",
                 "end_of_turn_confidence": 1, "words": [
                   {"start": 1216, "end": 1635, "text": "My", "confidence": 0.956583, "word_is_final": true},
                   {"start": 1676, "end": 2515, "text": "name", "confidence": 0.999199, "word_is_final": true},
                   {"start": 2556, "end": 2975, "text": "is", "confidence": 0.999535, "word_is_final": true},
                   {"start": 3016, "end": 4155, "text": "Sonny.", "confidence": 0.316031, "word_is_final": true}],
                 "utterance": "My name is Sonny.", "type": "Turn"}
                """, Final(1.216, 4.155, "My name is Sonny.")),
            // Constructed from documented fields (label-speakers-and-separate-channels.md): an early "PENDING" label is
            // no speaker yet.
            new("""
                {"type":"Turn","turn_order":1,"end_of_turn":false,"turn_is_formatted":false,"transcript":"Different",
                 "speaker_label":"PENDING","words":[{"text":"Different","speaker":"PENDING","word_is_final":false,"start":145,"end":290}]}
                """, new RealtimeEvent.Partial("Different", 0.145, 0.29)),
            // Constructed from documented fields (same page): the settled label.
            new("""
                {"type":"Turn","turn_order":1,"end_of_turn":true,"turn_is_formatted":true,"transcript":"Different.",
                 "speaker_label":"A","speaker_confidence":0.972431,
                 "words":[{"text":"Different.","speaker":"A","word_is_final":true,"start":145,"end":290,"confidence":0.844642}]}
                """, Final(0.145, 0.29, "Different.", speaker: "A"))),
        ["openai"] = new("openai", 1.5,
            // Constructed from documented fields (server-events.md): the commit's acknowledgement.
            new("""{"type":"input_audio_buffer.committed","event_id":"event_1","item_id":"item_CCXGQ4e1ht4cOraEYcuR2"}"""),
            // Verbatim (server-events.md): deltas have no times.
            new("""
                {"type": "conversation.item.input_audio_transcription.delta", "event_id": "event_CCXGRxsAimPAs8kS2Wc7Z",
                 "item_id": "item_CCXGQ4e1ht4cOraEYcuR2", "content_index": 0, "delta": "Hey", "obfuscation": "aLxx0jTEciOGe"}
                """, new RealtimeEvent.Partial("Hey")),
            // Verbatim (server-events.md): the final takes the commit's span.
            new("""
                {"type": "conversation.item.input_audio_transcription.completed", "event_id": "event_CCXGRvtUVrax5SJAnNOWZ",
                 "item_id": "item_CCXGQ4e1ht4cOraEYcuR2", "content_index": 0, "transcript": "Hey, can you hear me?",
                 "usage": {"type": "tokens", "total_tokens": 22, "input_tokens": 13, "output_tokens": 9}}
                """, Final(0, 1.5, "Hey, can you hear me?"))),
        ["xai"] = new("xai", null,
            // Verbatim (ws.json).
            new("""{"type":"transcript.created","id":"83f2f6fd-1cd1-4747-bc52-cebddc961c32"}""", new RealtimeEvent.Started()),
            // Constructed from documented fields (ws.json `transcript.partial` with is_final false).
            new("""
                {"type":"transcript.partial","text":"The balance","words":[{"text":"The","start":0.24,"end":0.48},
                 {"text":"balance","start":0.48,"end":0.96}],"is_final":false,"speech_final":false,"start":0.0,"duration":0.96}
                """, new RealtimeEvent.Partial("The balance", 0, 0.96)),
            // Verbatim (ws.json): a chunk final.
            new("""
                {"type":"transcript.partial","text":"The balance is $167,983.15.","words":[
                 {"text":"The","start":0.24,"end":0.48,"confidence":0.95},{"text":"balance","start":0.48,"end":0.96,"confidence":0.92},
                 {"text":"is","start":0.96,"end":1.12,"confidence":0.98},{"text":"$167,983.15.","start":1.12,"end":3.2,"confidence":0.89}],
                 "is_final":true,"speech_final":false,"start":0.0,"duration":3.2}
                """, Final(0.24, 3.2, "The balance is $167,983.15.")),
            // Constructed from documented fields: the utterance final repeats its chunk finals' words; only the new one
            // is emitted, with its speaker.
            new("""
                {"type":"transcript.partial","text":"The balance is $167,983.15. Thanks.","words":[
                 {"text":"The","start":0.24,"end":0.48},{"text":"balance","start":0.48,"end":0.96},
                 {"text":"is","start":0.96,"end":1.12},{"text":"$167,983.15.","start":1.12,"end":3.2},
                 {"text":"Thanks.","start":3.4,"end":3.9,"speaker":1}],"is_final":true,"speech_final":true,"start":0.0,"duration":3.9}
                """, Final(3.4, 3.9, "Thanks.", speaker: "1")),
            // Constructed from documented fields: an utterance final with only repeated words adds nothing.
            new("""
                {"type":"transcript.partial","text":"Thanks.","words":[{"text":"Thanks.","start":3.4,"end":3.9,"speaker":1}],
                 "is_final":true,"speech_final":true,"start":3.4,"duration":0.5}
                """),
            // Verbatim (ws.json).
            new("""{"type":"transcript.done","text":"","words":[],"duration":6.43}""", new RealtimeEvent.Ended())),
        // Constructed from documented fields: a chunk final of two sentences without speakers is two finals.
        ["xai sentences"] = new("xai", null,
            new Step("""
                {"type":"transcript.partial","text":"Hi there. Fine.","words":[
                 {"text":"Hi","start":0.1,"end":0.3},{"text":"there.","start":0.35,"end":0.7},{"text":"Fine.","start":1.3,"end":1.6}],
                 "is_final":true,"speech_final":false,"start":0.0,"duration":1.6}
                """, Final(0.1, 0.7, "Hi there."), Final(1.3, 1.6, "Fine."))),
        ["speechmatics"] = new("speechmatics", null,
            // Schema-derived (partials have the final's structure): constructed from documented fields.
            new("""
                {"message": "AddPartialTranscript", "format": "2.9",
                 "metadata": {"transcript": "full tell", "start_time": 0.11, "end_time": 0.62},
                 "results": [
                   {"type": "word", "start_time": 0.11, "end_time": 0.4, "alternatives": [{"content": "full", "confidence": 0.0, "speaker": "S1"}]},
                   {"type": "word", "start_time": 0.41, "end_time": 0.62, "alternatives": [{"content": "tell", "confidence": 0.0, "speaker": "S1"}]}]}
                """, new RealtimeEvent.Partial("full tell", 0.11, 0.62, "S1")),
            // Verbatim (cpu-speech-to-text via context7), without diarization.
            new("""
                {"message": "AddTranscript", "format": "2.9",
                 "metadata": {"transcript": "full tell radar", "start_time": 0.11, "end_time": 1.07},
                 "results": [
                   {"type": "word", "start_time": 0.11, "end_time": 0.4, "alternatives": [{"content": "full", "confidence": 0.7}]},
                   {"type": "word", "start_time": 0.41, "end_time": 0.62, "alternatives": [{"content": "tell", "confidence": 0.6}]},
                   {"type": "word", "start_time": 0.65, "end_time": 1.07, "alternatives": [{"content": "radar", "confidence": 1.0}]}]}
                """, new RealtimeEvent.Partial("full tell radar", 0.11, 1.07)),
            // Constructed from documented fields: with diarization each alternative has its speaker. The speaker change
            // ends the held words' segment.
            new("""
                {"message": "AddTranscript", "format": "2.9",
                 "metadata": {"transcript": "radar", "start_time": 1.2, "end_time": 1.6},
                 "results": [{"type": "word", "start_time": 1.2, "end_time": 1.6,
                              "alternatives": [{"content": "radar", "confidence": 1.0, "speaker": "S2"}]}]}
                """, Final(0.11, 1.07, "full tell radar"), new RealtimeEvent.Partial("radar", 1.2, 1.6, "S2")),
            // Constructed from documented fields (realtime WebSocket reference): `ForceEndOfUtterance`'s answer, metadata
            // without a transcript, flushes the held words.
            new("""{"message":"EndOfUtterance","format":"2.9","metadata":{"start_time":1.6,"end_time":1.6},"forced":true}""",
                Final(1.2, 1.6, "radar", speaker: "S2")),
            // Constructed from documented fields.
            new("""{"message":"EndOfTranscript"}""", new RealtimeEvent.Ended())),
        // Constructed from documented fields: an `AddTranscript` carries a few words; they are held across messages until
        // a sentence ends (punctuation attached across messages), and the rest is flushed before `Ended`.
        ["speechmatics sentences"] = new("speechmatics", null,
            new("""
                {"message": "AddTranscript", "metadata": {"transcript": "Hello", "start_time": 0.1, "end_time": 0.5},
                 "results": [{"type": "word", "start_time": 0.1, "end_time": 0.5, "alternatives": [{"content": "Hello", "speaker": "S1"}]}]}
                """, new RealtimeEvent.Partial("Hello", 0.1, 0.5, "S1")),
            new("""
                {"message": "AddTranscript", "metadata": {"transcript": ". How", "start_time": 0.5, "end_time": 1.2},
                 "results": [
                   {"type": "punctuation", "start_time": 0.5, "end_time": 0.5, "is_eos": true, "attaches_to": "previous",
                    "alternatives": [{"content": ".", "speaker": "S1"}]},
                   {"type": "word", "start_time": 0.9, "end_time": 1.2, "alternatives": [{"content": "How", "speaker": "S1"}]}]}
                """, Final(0.1, 0.5, "Hello.", speaker: "S1"), new RealtimeEvent.Partial("How", 0.9, 1.2, "S1")),
            new("""
                {"message": "AddPartialTranscript", "metadata": {"transcript": "are you", "start_time": 1.2, "end_time": 1.8},
                 "results": [{"type": "word", "start_time": 1.2, "end_time": 1.5, "alternatives": [{"content": "are", "speaker": "S1"}]}]}
                """, new RealtimeEvent.Partial("How are you", 0.9, 1.8, "S1")),
            new("""{"message":"EndOfTranscript"}""", Final(0.9, 1.2, "How", speaker: "S1"), new RealtimeEvent.Ended())),
        // Verbatim (real-time-transcription, "Example token evolution"): this example has no token times.
        ["soniox partial"] = new("soniox", null,
            new Step("""
                {"tokens": [{"text": "How", "is_final": true}, {"text": " ", "is_final": true},
                            {"text": "are", "is_final": false}, {"text": " ", "is_final": false}, {"text": "you", "is_final": false}]}
                """, new RealtimeEvent.Partial("How are you", 0, 0))),
        ["soniox final"] = new("soniox", null,
            // Verbatim (websocket-api.mdx): final tokens (ms) are held until the utterance ends.
            new("""
                {"tokens": [{"text": "Hello", "start_ms": 600, "end_ms": 760, "confidence": 0.97, "is_final": true, "speaker": "1"}],
                 "final_audio_proc_ms": 760, "total_audio_proc_ms": 880}
                """, new RealtimeEvent.Partial("Hello", 0.6, 0.76, "1")),
            // Constructed from documented fields: the manual-finalize marker flushes them.
            new("""{"tokens": [{"text": "<fin>", "is_final": true}], "final_audio_proc_ms": 900, "total_audio_proc_ms": 900}""",
                Final(0.6, 0.76, "Hello", speaker: "1")),
            // Verbatim (websocket-api.mdx): end of stream.
            new("""{"tokens":[],"final_audio_proc_ms":1560,"total_audio_proc_ms":1680,"finished":true}""", new RealtimeEvent.Ended())),
        ["gladia"] = new("gladia", null,
            // Derived from the final (partial-transcripts.md: same shape, is_final false): constructed from documented
            // fields.
            new("""
                {"type":"transcript","session_id":"de0a341d-c69f-4e15-a649-7b3f49e211f0","created_at":"2024-10-10T14:35:28.100Z",
                 "data":{"id":"00_00000000","is_final":false,"utterance":{"text":" Hello","start":0.188,"end":0.735,"language":"en",
                 "confidence":0.9,"channel":0,"words":[{"word":" Hello","start":0.188,"end":0.735,"confidence":0.9}]}}}
                """, new RealtimeEvent.Partial("Hello", 0.188, 0.735)),
            // Verbatim (live-stt/migration-from-v1.md, V2 tab).
            new("""
                {"type": "transcript", "session_id": "de0a341d-c69f-4e15-a649-7b3f49e211f0", "created_at": "2024-10-10T14:35:28.387Z",
                 "data": {"id": "00_00000000", "is_final": true, "utterance": {"text": " Hello world", "start": 0.188, "end": 1.284,
                   "language": "en", "confidence": 1, "channel": 0, "words": [
                     {"word": " Hello", "start": 0.188, "end": 0.735, "confidence": 1},
                     {"word": " world", "start": 0.736, "end": 1.284, "confidence": 1}]}}}
                """, Final(0.188, 1.284, "Hello world", language: "en"))),
    };

    public static TheoryData<string> RecordingNames => new(Recordings.Keys);

    [Theory]
    [MemberData(nameof(RecordingNames))]
    public void RecordedMessagesParse(string name)
    {
        var row = Recordings[name];
        var adapter = Adapter(row.Preset);
        if (row.CommitAt is { } commitAt) Assert.NotEmpty(adapter.Finalize(commitAt));
        for (var index = 0; index < row.Steps.Length; index++)
        {
            var events = adapter.Parse(new RealtimeMessage.Text(row.Steps[index].Message));
            Assert.True(row.Steps[index].Events.SequenceEqual(events), $"step {index}: {string.Join(", ", events)}");
        }
    }

    // Stateful outgoing messages.

    /// <summary>
    /// Frame 1.5 s -> commit -> committed{item_id} -> completed is a final over the commit's span; closing commits the rest
    /// and ends once its transcript is in.
    /// </summary>
    [Fact]
    public void OpenAITurnsTakeTheirCommitSpans()
    {
        var adapter = Adapter("openai");
        var second = CloudRealtime.Pcm16(new float[24_000]);
        RealtimeMessage[] commit = [new RealtimeMessage.Text("""{"type":"input_audio_buffer.commit"}""")];

        adapter.Frame([.. second, .. second[..24_000]]);
        Assert.Equal(commit, adapter.Finalize(1.5));
        Assert.Empty(adapter.Finalize(1.55)); // under 100 ms since the last commit
        Assert.Empty(Parse(adapter, """{"type":"input_audio_buffer.committed","item_id":"a"}"""));
        Assert.Equal(
            [Final(0, 1.5, "One.")],
            Parse(adapter, """{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":" One. "}"""));

        adapter.Frame([.. second, .. second[..24_000]]);
        Assert.Equal(commit, adapter.CloseMessages());
        Assert.Empty(Parse(adapter, """{"type":"input_audio_buffer.committed","item_id":"b"}"""));
        Assert.Equal(
            [Final(1.5, 3, "Two."), new RealtimeEvent.Ended()],
            Parse(adapter, """{"type":"conversation.item.input_audio_transcription.completed","item_id":"b","transcript":"Two."}"""));
    }

    /// <summary>
    /// OpenAI sends no end message: pause (commit, completed), then Stop with no new audio commits nothing, so the stream
    /// is drained at once instead of waiting for an <c>Ended</c> that never comes.
    /// </summary>
    [Fact]
    public void OpenAICloseWithNothingLeftIsDrained()
    {
        var adapter = Adapter("openai");
        adapter.Frame(CloudRealtime.Pcm16(new float[24_000]));
        Assert.NotEmpty(adapter.Finalize(1));
        Parse(adapter, """{"type":"input_audio_buffer.committed","item_id":"a"}""");
        Parse(adapter, """{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":"One."}""");
        Assert.False(adapter.IsDrained);

        Assert.Empty(adapter.CloseMessages());
        Assert.True(adapter.IsDrained);
    }

    /// <summary>OpenAI closing while a commit waits for its transcript is drained only once that transcript is in.</summary>
    [Fact]
    public void OpenAICloseWaitsForThePendingTranscript()
    {
        var adapter = Adapter("openai");
        adapter.Frame(CloudRealtime.Pcm16(new float[24_000]));
        Assert.NotEmpty(adapter.Finalize(1));
        Assert.Empty(adapter.CloseMessages());
        Assert.False(adapter.IsDrained);

        Parse(adapter, """{"type":"input_audio_buffer.committed","item_id":"a"}""");
        Assert.Equal(
            [Final(0, 1, "One."), new RealtimeEvent.Ended()],
            Parse(adapter, """{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":"One."}"""));
        Assert.True(adapter.IsDrained);
    }

    /// <summary>ElevenLabs sends no end message: the timed answer to the close commit ends the stream, even without words.</summary>
    [Theory]
    [InlineData("""{"message_type":"committed_transcript_with_timestamps","text":"hi","language_code":"en","words":[{"text":"hi","start":0.1,"end":0.3,"type":"word"}]}""")]
    [InlineData("""{"message_type":"committed_transcript_with_timestamps","text":"","words":[]}""")]
    public void ElevenLabsCloseCommitAnsweredEnds(string answer)
    {
        var adapter = Adapter("elevenlabs");
        adapter.Frame(CloudRealtime.Pcm16(Enumerable.Repeat(0.1f, 3_200).ToArray()));
        Assert.Single(adapter.CloseMessages());
        Assert.False(adapter.IsDrained);
        Assert.Empty(Parse(adapter, """{"message_type":"committed_transcript","text":"hi"}"""));
        Assert.False(adapter.IsDrained);

        var events = Parse(adapter, answer);
        Assert.IsType<RealtimeEvent.Ended>(events[^1]);
        Assert.All(events.Take(events.Count - 1), e => Assert.IsType<RealtimeEvent.Final>(e));
        Assert.True(adapter.IsDrained);
    }

    /// <summary>
    /// ElevenLabs closing with nothing uncommitted and no commit waiting is drained at once; a pause commit still waiting
    /// for its answer keeps it open.
    /// </summary>
    [Fact]
    public void ElevenLabsCloseWithNothingOutstandingIsDrained()
    {
        var fresh = Adapter("elevenlabs");
        Assert.Empty(fresh.CloseMessages());
        Assert.True(fresh.IsDrained);

        var paused = Adapter("elevenlabs");
        paused.Frame(CloudRealtime.Pcm16(Enumerable.Repeat(0.1f, 3_200).ToArray()));
        Assert.Single(paused.Finalize(0.2));
        Assert.Empty(paused.CloseMessages());
        Assert.False(paused.IsDrained);
        Assert.Equal([new RealtimeEvent.Ended()], Parse(paused, """{"message_type":"committed_transcript_with_timestamps","words":[]}"""));
        Assert.True(paused.IsDrained);
    }

    [Fact]
    public void SpeechmaticsEndOfStreamCountsTheFramesSent()
    {
        var adapter = Adapter("speechmatics");
        for (var i = 0; i < 3; i++) adapter.Frame(CloudRealtime.Pcm16([0f, 0.1f]));

        Assert.Equal([new RealtimeMessage.Text("""{"message":"EndOfStream","last_seq_no":3}""")], adapter.CloseMessages());
    }

    /// <summary>Deepgram closes a socket after 10 s without audio or a KeepAlive; its docs ask for one every 3-5 s.</summary>
    [Fact]
    public void DeepgramKeepsTheSocketAlive()
    {
        var keepAlive = Adapter("deepgram").KeepAlive;

        Assert.NotNull(keepAlive);
        Assert.Equal(new RealtimeMessage.Text("""{"type":"KeepAlive"}"""), keepAlive.Message);
        Assert.InRange(keepAlive.Interval, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    // PCM, resampler, error kinds.

    [Fact]
    public void Pcm16ClampsAndRounds()
    {
        var data = CloudRealtime.Pcm16([0, 1, -1, 2, -2, 0.5f, -0.5f, float.NaN, float.PositiveInfinity]);
        var values = Enumerable.Range(0, data.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2)));

        Assert.Equal(new short[] { 0, 32_767, -32_767, 32_767, -32_767, 16_384, -16_384, 0, 0 }, values);
        Assert.Equal(new byte[] { 0xFF, 0x7F }, CloudRealtime.Pcm16([1f]));
    }

    [Fact]
    public void ResamplerChunkedMatchesOneShot()
    {
        var ramp = Enumerable.Range(0, 1_600).Select(i => i / 1_600f).ToArray();
        var whole = new RealtimeResampler(16_000, 24_000).Process(ramp);

        var chunked = new RealtimeResampler(16_000, 24_000);
        var parts = new List<float>();
        var index = 0;
        foreach (var size in new[] { 1, 7, 160, 333, 99, 800, 200 })
        {
            parts.AddRange(chunked.Process(ramp.AsSpan(index, size)));
            index += size;
        }

        Assert.Equal(ramp.Length, index);
        Assert.Equal(whole, parts);
        Assert.InRange(whole.Length - ramp.Length * 3 / 2, -2, 2);
        foreach (var k in new[] { 0, 1, 2, 3, 1_000, whole.Length - 1 })
            Assert.True(Math.Abs(whole[k] - k * 2f / 3 / 1_600) < 1e-5, $"sample {k}");
    }

    [Theory]
    [InlineData(null, "error")]
    [InlineData("", "error")]
    [InlineData("  ", "error")]
    [InlineData("!?<>", "error")]
    [InlineData("auth_error", "auth_error")]
    [InlineData("3007", "3007")]
    [InlineData("v2.live-session_x", "v2.live-session_x")]
    [InlineData("not authorised: <b>Bad</b>", "notauthorisedbBadb")]
    [InlineData("Ünicödé-1", "nicd-1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ErrorKindIsSanitized(string? raw, string kind)
    {
        Assert.Equal(kind, CloudRealtime.ErrorKind(raw));
    }

    // Helpers.

    private static ProviderConfig Preset(string id) => ProviderConfig.Presets().Single(p => p.Id == id);

    /// <summary>The adapter the catalog builds for a preset, for Vietnamese with diarization on (Speechmatics needs a language).</summary>
    private static IRealtimeAdapter Adapter(string id) =>
        CloudRealtime.Make(Preset(id), Key, "vi", diarize: true) ?? throw new InvalidOperationException($"no adapter for {id}");

    private static Uri SocketUrl(IRealtimeAdapter adapter, string preset) =>
        adapter.SocketUrl(preset == "gladia" ? GladiaAnswer : null);

    private static IReadOnlyList<RealtimeEvent> Parse(IRealtimeAdapter adapter, string message) =>
        adapter.Parse(new RealtimeMessage.Text(message));

    private static RealtimeEvent Final(double start, double end, string text, string? speaker = null, string? language = null) =>
        new RealtimeEvent.Final(new CloudSegment(start, end, text, speaker, language));

    /// <summary>The URL's query items, names and values percent-decoded.</summary>
    private static Dictionary<string, string> Query(Uri url) =>
        url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "");

    /// <summary>The value at a dotted path of decoded JSON, as text (numbers as their digits).</summary>
    private static string? Lookup(JsonNode? node, string path)
    {
        foreach (var key in path.Split('.')) node = (node as JsonObject)?[key];
        return node is JsonValue value ? value.ToString() : null;
    }
}
