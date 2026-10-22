using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Platform.Audio;

namespace EchoDiar.Platform.Tests;

// Port of macOS AudioCompressorTests, on the real Media Foundation AAC encoder and decoder (Windows only, CI job
// test-platform on a Windows 11 client image).
public sealed class AudioCompressorTests : IDisposable
{
    private const double Seconds = 3;
    private const int SampleRate = 16_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarPlatformTests-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public AudioCompressorTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // The core of compression: the CAF is replaced only by a verified m4a, and a finished transcription stays so.
    [Fact]
    public async Task VerifiedEncodeReplacesTheCafAndKeepsTheTranscribedStatus()
    {
        var folder = MakeConversation();

        var result = await new AudioCompressor().CompressAsync(folder, 96, keepCaf: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.Equal([AudioCompressor.StereoFileName], result.OutputFileNames);
        var m4a = Path.Combine(folder, AudioCompressor.StereoFileName);
        Assert.True(File.Exists(m4a));
        Assert.False(File.Exists(Path.Combine(folder, AudioCompressor.CafFileName)));
        Assert.Equal(AudioCompressor.StereoFileName, RecordingSession.Read(folder).AudioFileName);
        Assert.Equal(ConversationStatus.Transcribed, Conversation.Read(folder).Status);

        var decoder = new MediaFoundationAudio();
        Assert.Equal(2, decoder.ChannelCount(m4a));
        var duration = decoder.Duration(m4a);
        _output.WriteLine($"audio.m4a decodes to {duration:0.0000} s (source {Seconds} s), {new FileInfo(m4a).Length} bytes");
        Assert.InRange(duration, Seconds - AudioCompressor.DurationTolerance, Seconds + AudioCompressor.DurationTolerance);
    }

    // A failed encode never costs the recording: the CAF stays, partial outputs go, the failure shows in the library.
    [Fact]
    public async Task FailedEncodeKeepsTheCaf()
    {
        var folder = MakeConversation();
        var compressor = new AudioCompressor((_, destination, _, _) =>
        {
            File.WriteAllText(destination, "partial");
            throw new AudioEncodeException(AudioEncodeFailure.Encode, "forced");
        });

        var result = await compressor.CompressAsync(folder, 96, keepCaf: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AudioCompressionFailure.Encode, result.Failure);
        AssertUntouched(folder);
    }

    // An output that decodes short (a truncated file) is not trusted to replace the CAF.
    [Fact]
    public async Task ShortOutputFailsTheDurationCheck()
    {
        var folder = MakeConversation();
        var compressor = new AudioCompressor(
            (_, destination, _, _) => File.WriteAllText(destination, "truncated"), new FixedDecoder(2, Seconds - 0.5));

        var result = await compressor.CompressAsync(folder, 96, keepCaf: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AudioCompressionFailure.DurationMismatch, result.Failure);
        AssertUntouched(folder);
    }

    // session.json is untrusted: an audio name outside the folder is never read, encoded or deleted.
    [Fact]
    public async Task OutsideAudioFileNameIsRejected()
    {
        var outside = Path.Combine(_dir, "x.caf");
        WriteCaf(outside);
        var folder = MakeConversation(audioFileName: "../x.caf");
        var compressor = new AudioCompressor((_, _, _, _) => Assert.Fail("The encoder must not run"));

        var result = await compressor.CompressAsync(folder, 96, keepCaf: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AudioCompressionFailure.UnsafeAudioFileName, result.Failure);
        Assert.True(File.Exists(outside));
        Assert.Equal("../x.caf", RecordingSession.Read(folder).AudioFileName);
        Assert.Equal(ConversationStatus.CompressionFailed, Conversation.Read(folder).Status);
    }

    // Decision 6: the AAC encoder takes no 16 kHz input, so the CAF is resampled to 48 kHz, where it offers every
    // bitrate of the Storage page (96/128/160/192 kbps).
    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void EncoderRejects16kHzAndOffersTheBitratesAt48kHz(int channels)
    {
        Assert.Empty(MediaFoundationAudio.AacBytesPerSecond(16_000, channels));
        var offered = MediaFoundationAudio.AacBytesPerSecond(MediaFoundationAudio.EncodeSampleRate, channels);
        _output.WriteLine($"48 kHz, {channels} ch: {string.Join(", ", offered)} bytes/s");
        foreach (var bytesPerSecond in new[] { 12_000, 16_000, 20_000, 24_000 }) Assert.Contains(bytesPerSecond, offered);
    }

    // The mono fallback writes one CAF channel per file.
    [Fact]
    public void MonoEncodeOfOneChannelDecodesToOneChannel()
    {
        var caf = Path.Combine(_dir, "audio.caf");
        WriteCaf(caf);
        var m4a = Path.Combine(_dir, AudioCompressor.MicFileName);

        MediaFoundationAudio.EncodeAac(caf, m4a, RecordingSession.Channel.Mic, 48);

        Assert.Equal(1, new MediaFoundationAudio().ChannelCount(m4a));
    }

    /// <summary>A stopped recording folder as the app leaves it: a 3 s stereo CAF, session.json, a transcribed conversation.json.</summary>
    private string MakeConversation(string audioFileName = AudioCompressor.CafFileName)
    {
        var session = new RecordingSession { State = SessionState.Stopped, TapMode = TapMode.Global, AudioFileName = audioFileName };
        var folder = session.Folder(_dir);
        session.Write(folder);
        WriteCaf(Path.Combine(folder, AudioCompressor.CafFileName));
        var conversation = Conversation.FromSession(session, folder, Seconds);
        conversation.Status = ConversationStatus.Transcribed;
        conversation.HasTranscript = true;
        conversation.Write();
        return folder;
    }

    /// <summary>3 s of 2-channel Int16 at 16 kHz, as the recorder writes: a tone on ch0 (mic), another on ch1 (system).</summary>
    private static void WriteCaf(string path)
    {
        var frames = (int)(Seconds * SampleRate);
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[2 * i] = (short)(8_000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            samples[2 * i + 1] = (short)(8_000 * Math.Sin(2 * Math.PI * 660 * i / SampleRate));
        }
        using var writer = new CafFile.Writer(path, SampleRate, 2, CafFile.SampleFormat.Int16);
        writer.Append(samples);
    }

    private static void AssertUntouched(string folder)
    {
        Assert.True(File.Exists(Path.Combine(folder, AudioCompressor.CafFileName)));
        foreach (var name in new[] { AudioCompressor.StereoFileName, AudioCompressor.MicFileName, AudioCompressor.SystemFileName })
        {
            Assert.False(File.Exists(Path.Combine(folder, name)), $"{name} was left behind");
        }
        Assert.Equal(AudioCompressor.CafFileName, RecordingSession.Read(folder).AudioFileName);
        Assert.Equal(ConversationStatus.CompressionFailed, Conversation.Read(folder).Status);
    }

    private sealed class FixedDecoder(int channels, double duration) : IAudioDecoder
    {
        public int ChannelCount(string path) => channels;

        public double Duration(string path) => duration;

        public float[] DecodeToMono16k(string path, int channel) => throw new NotSupportedException();
    }
}
