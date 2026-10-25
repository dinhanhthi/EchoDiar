using EchoDiar.Transcribe.Engines;

namespace EchoDiar.Tests.Transcribe;

// Swift WhisperKitSpeechTextTests: the text and silence rules of WhisperEngine, without loading a model.
public sealed class WhisperSpeechTextTests
{
    // Guards the [BLANK_AUDIO] fix: Whisper's non-speech output is dropped, speech with brackets is kept.
    [Fact]
    public void DropsNonSpeechAnnotationsAndKeepsSpeechWithBrackets()
    {
        foreach (var raw in new[] { "[BLANK_AUDIO]", " [ Silence ] ", "(music)", "*music*", "<|endoftext|>", "[BLANK_AUDIO] (Music)" })
        {
            Assert.True(WhisperEngine.SpeechText(raw) == "", raw);
        }

        Assert.Equal("Call f(x) and look at a[0] now.", WhisperEngine.SpeechText("Call f(x) and look at a[0] now."));
        Assert.Equal("(I think) we should go", WhisperEngine.SpeechText("(I think) we should go"));
        Assert.Equal("See the notes [section 2]", WhisperEngine.SpeechText("See the notes [section 2]"));
        Assert.Equal("I said (quietly) hello", WhisperEngine.SpeechText("<|en|> I said (quietly) hello"));
        Assert.Equal("Hello there", WhisperEngine.SpeechText("[Music] Hello there (laughs)"));
    }

    // Guards the "Thank you." hallucinations: a segment over near silence is dropped, one over speech is kept, and a
    // NaN or infinite timestamp counts as silent.
    [Fact]
    public void DropsSegmentsOverNearSilence()
    {
        const int rate = 16_000;
        var quiet = Enumerable.Repeat(0.001f, 2 * rate).ToArray();
        var speech = Enumerable.Range(0, 2 * rate).Select(i => (float)(0.1 * Math.Sin(2 * Math.PI * 220 * i / rate))).ToArray();

        Assert.True(WhisperEngine.IsSilent(quiet, 0, 2, rate));
        Assert.False(WhisperEngine.IsSilent([.. quiet, .. speech], 1.5f, 5, rate));
        Assert.True(WhisperEngine.IsSilent([.. quiet, .. speech], float.NaN, 5, rate));
        Assert.True(WhisperEngine.IsSilent([.. quiet, .. speech], 0, float.PositiveInfinity, rate));
    }

    // Guards the subtitle-credit list ("Merci." / "Sous-titrage ST' 501" on silence): Whisper's credits are dropped,
    // the meeting's speech is kept, also when it mentions subtitles mid-sentence.
    [Fact]
    public void DropsSubtitleCreditsAndKeepsSpeech()
    {
        foreach (var (text, credit) in new[]
        {
            ("Sous-titrage Société Radio-Canada", true),
            ("- Sous-titrage ST' 501", true),
            ("Sous-titres réalisés par la communauté d'Amara.org", true),
            ("sous-titres realises par la communaute", true),
            ("Thanks for watching!", true),
            ("Hãy đăng ký kênh để ủng hộ kênh nhé!", true),
            ("Subtítulos por la comunidad de Amara.org", true),
            ("字幕由志愿者社区提供", true),
            ("Bonjour, on commence la réunion.", false),
            ("Can we enable subtitles by default?", false),
            ("On doit prévoir le sous-titrage de la vidéo.", false),
            ("字幕由谁提供？", false),
        })
        {
            Assert.True(WhisperEngine.IsSubtitleCredit(text) == credit, text);
        }
    }
}
