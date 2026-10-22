using System.Text.Json;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;

namespace EchoDiar.Platform.Audio;

/// <summary>Why a compression failed (Swift <c>AudioCompressorError</c>).</summary>
public enum AudioCompressionFailure
{
    /// <summary><c>session.json</c> is missing or unreadable.</summary>
    SessionUnreadable,

    /// <summary>The session is still recording or paused; nothing was touched.</summary>
    StillRecording,

    /// <summary>The source audio is missing, unreadable or empty.</summary>
    SourceUnreadable,

    /// <summary><c>session.audioFileName</c> is not a plain file in the folder (a path, <c>..</c> or a symlink); nothing was touched.</summary>
    UnsafeAudioFileName,

    /// <summary>The encoder refused the output format or bitrate (stereo and mono).</summary>
    SettingRejected,

    Encode,
    OutputUnreadable,

    /// <summary>The decoded output's length differs from the source's by more than <see cref="AudioCompressor.DurationTolerance"/>.</summary>
    DurationMismatch,

    /// <summary>Writing <c>session.json</c> or <c>conversation.json</c> failed.</summary>
    Metadata,
}

/// <summary>A compression's outcome: <see cref="OutputFileNames"/> are the compressed files in the folder, empty on failure.</summary>
public sealed record CompressionResult(
    string Folder, IReadOnlyList<string> OutputFileNames, AudioCompressionFailure? Failure = null, string? Detail = null);

/// <summary>
/// Compresses a recording folder's 2-channel CAF to AAC-LC m4a, one job at a time (Swift <c>AudioCompressor</c>).
/// The CAF is deleted only after the decoded output matches its duration and <c>session.json</c> points to the new
/// file; on any failure the CAF stays and <c>conversation.json</c> is marked <c>compressionFailed</c>.
/// </summary>
/// <param name="encoder">Writes the m4a; <see cref="MediaFoundationAudio.EncodeAac"/> by default. Injectable so tests can force failures.</param>
/// <param name="decoder">Checks the output (channels, decoded length); <see cref="MediaFoundationAudio"/> by default.</param>
public sealed class AudioCompressor(AudioCompressor.Encoder? encoder = null, IAudioDecoder? decoder = null)
{
    /// <summary>
    /// Encodes <paramref name="source"/> into an m4a at <paramref name="destination"/> at <paramref name="kbps"/> (total).
    /// <paramref name="channel"/> null keeps every source channel; otherwise only that channel is written, as mono.
    /// Throws <see cref="AudioEncodeException"/>; <see cref="AudioEncodeFailure.SettingRejected"/> triggers the mono fallback.
    /// </summary>
    public delegate void Encoder(string source, string destination, int? channel, int kbps);

    /// <summary>The recorder's uncompressed file name.</summary>
    public const string CafFileName = "audio.caf";
    public const string StereoFileName = "audio.m4a";
    /// <summary>Mono fallback: <c>session.audioFileName</c> becomes the mic file; the system file sits next to it.</summary>
    public const string MicFileName = "audio-mic.m4a";
    public const string SystemFileName = "audio-system.m4a";
    public const double DurationTolerance = 0.1;

    private readonly Encoder _encoder = encoder ?? MediaFoundationAudio.EncodeAac;
    private readonly IAudioDecoder _decoder = decoder ?? new MediaFoundationAudio();
    private readonly SemaphoreSlim _queue = new(1, 1);

    /// <summary>
    /// Queued behind earlier jobs; completes when this one is done. <paramref name="bitrateKbps"/> snaps to
    /// <see cref="MediaFoundationAudio.BitratesKbps"/>; the mono fallback uses half of it per file (snapped again by the
    /// encoder). <paramref name="started"/> runs on the job's thread right after <c>conversation.json</c> is marked
    /// <c>compressing</c>, before the encode. <paramref name="cancellationToken"/> stops a job that has not started yet
    /// (an encode under way runs to its end), throwing <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<CompressionResult> CompressAsync(
        string folder, int bitrateKbps, bool keepCaf, Action? started = null, CancellationToken cancellationToken = default)
    {
        await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Run(folder, bitrateKbps, keepCaf, started, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _queue.Release();
        }
    }

    // Job

    private CompressionResult Run(string folder, int bitrateKbps, bool keepCaf, Action? started, CancellationToken cancellationToken)
    {
        RecordingSession session;
        try
        {
            session = RecordingSession.Read(folder);
        }
        catch (Exception e) when (IsFileError(e) || e is JsonException)
        {
            return Failed(folder, AudioCompressionFailure.SessionUnreadable, e.Message);
        }
        if (session.State is SessionState.Recording or SessionState.Paused) return Failed(folder, AudioCompressionFailure.StillRecording);
        var sourceName = session.AudioFileName;
        if (ConversationFiles.AudioPath(folder, sourceName) is not { } source)
        {
            TryUpdateConversation(folder, session, 0, c => c.Status = ConversationStatus.CompressionFailed);
            return Failed(folder, AudioCompressionFailure.UnsafeAudioFileName);
        }
        if (!IsCaf(source)) return AlreadyCompressed(session, source, folder, keepCaf);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceDuration = 0.0;
        ConversationStatus? before = null;
        IReadOnlyList<string> outputs;
        try
        {
            sourceDuration = SourceDuration(source);
            if (!(sourceDuration > 0)) throw new CompressionException(AudioCompressionFailure.SourceUnreadable, $"{sourceName} is empty");
            before = TryReadConversation(folder)?.Status;
            UpdateConversation(folder, session, sourceDuration, c => c.Status = ConversationStatus.Compressing);
            started?.Invoke();
            outputs = EncodeAndVerify(source, folder, MediaFoundationAudio.SnapKbps(bitrateKbps), sourceDuration);
            session.AudioFileName = outputs[0];
            try
            {
                session.Write(folder);
            }
            catch
            {
                RemoveOutputs(folder);
                throw;
            }
        }
        // Any failure keeps the CAF, which session.json still points to.
        catch (Exception e)
        {
            TryUpdateConversation(folder, session, sourceDuration, c => c.Status = ConversationStatus.CompressionFailed);
            return Failed(folder, e is CompressionException typed ? typed.Failure : AudioCompressionFailure.Metadata, e.Message);
        }

        // session.json already points to the verified output, so the CAF is no longer needed.
        if (!keepCaf) RemoveCaf(sourceName, folder);
        try
        {
            UpdateConversation(folder, session, sourceDuration, c =>
            {
                c.Status = DoneStatus(before, c.HasTranscript);
                c.Duration = sourceDuration;
                c.SizeBytes = LibraryStore.SizeOnDisk(folder);
            });
        }
        catch (Exception e) when (IsFileError(e) || e is JsonException)
        {
            return new CompressionResult(folder, outputs, AudioCompressionFailure.Metadata, e.Message);
        }
        return new CompressionResult(folder, outputs);
    }

    /// <summary>
    /// The session already points to compressed audio (<paramref name="audio"/>): nothing to encode; heals a status, and
    /// removes a CAF, left by a job interrupted after <c>session.json</c> was rewritten.
    /// </summary>
    private static CompressionResult AlreadyCompressed(RecordingSession session, string audio, string folder, bool keepCaf)
    {
        if (!File.Exists(audio)) return Failed(folder, AudioCompressionFailure.SourceUnreadable, $"{session.AudioFileName} is missing");
        if (!keepCaf) RemoveCaf(CafFileName, folder);
        IReadOnlyList<string> outputs = session.AudioFileName == MicFileName ? [MicFileName, SystemFileName] : [session.AudioFileName];
        if (TryReadConversation(folder) is { Status: ConversationStatus.Compressing or ConversationStatus.CompressionFailed } conversation)
        {
            TryUpdateConversation(folder, session, conversation.Duration, c =>
            {
                c.Status = DoneStatus(conversation.Status, c.HasTranscript);
                c.SizeBytes = LibraryStore.SizeOnDisk(folder);
            });
        }
        return new CompressionResult(folder, outputs);
    }

    /// <summary>Stereo first; two mono files when the encoder rejects the stereo setting. Removes partial outputs on failure.</summary>
    private IReadOnlyList<string> EncodeAndVerify(string source, string folder, int kbps, double sourceDuration)
    {
        RemoveOutputs(folder);
        try
        {
            var stereo = Path.Combine(folder, StereoFileName);
            Encode(source, stereo, null, kbps);
            Verify(stereo, 2, sourceDuration);
            return [StereoFileName];
        }
        catch (CompressionException e) when (e.Failure == AudioCompressionFailure.SettingRejected)
        {
            RemoveOutputs(folder);
        }
        catch
        {
            RemoveOutputs(folder);
            throw;
        }

        (string Name, int Channel)[] mono = [(MicFileName, RecordingSession.Channel.Mic), (SystemFileName, RecordingSession.Channel.System)];
        try
        {
            foreach (var (name, channel) in mono)
            {
                var path = Path.Combine(folder, name);
                Encode(source, path, channel, kbps / 2);
                Verify(path, 1, sourceDuration);
            }
        }
        catch
        {
            RemoveOutputs(folder);
            throw;
        }
        return [.. mono.Select(m => m.Name)];
    }

    private void Encode(string source, string destination, int? channel, int kbps)
    {
        try
        {
            _encoder(source, destination, channel, kbps);
        }
        catch (AudioEncodeException e)
        {
            var failure = e.Failure switch
            {
                AudioEncodeFailure.SourceUnreadable => AudioCompressionFailure.SourceUnreadable,
                AudioEncodeFailure.SettingRejected => AudioCompressionFailure.SettingRejected,
                _ => AudioCompressionFailure.Encode,
            };
            throw new CompressionException(failure, e.Message, e);
        }
    }

    /// <summary>The output has <paramref name="channels"/> and decodes to the source's length, within <see cref="DurationTolerance"/>.</summary>
    private void Verify(string path, int channels, double sourceDuration)
    {
        double duration;
        try
        {
            var count = _decoder.ChannelCount(path);
            if (count != channels)
            {
                throw new CompressionException(
                    AudioCompressionFailure.OutputUnreadable, $"{Path.GetFileName(path)} has {count} channels, not {channels}");
            }
            duration = _decoder.Duration(path);
        }
        catch (Exception e) when (IsFileError(e) || e is InvalidDataException)
        {
            throw new CompressionException(AudioCompressionFailure.OutputUnreadable, e.Message, e);
        }
        if (!(Math.Abs(duration - sourceDuration) <= DurationTolerance))
        {
            throw new CompressionException(
                AudioCompressionFailure.DurationMismatch, $"{Path.GetFileName(path)} decodes to {duration:0.000} s, the source is {sourceDuration:0.000} s");
        }
    }

    /// <summary>Seconds of audio in the CAF.</summary>
    private static double SourceDuration(string path)
    {
        try
        {
            using var reader = new CafFile.Reader(path);
            return reader.SampleRate > 0 ? reader.Frames / reader.SampleRate : 0;
        }
        catch (Exception e) when (IsFileError(e) || e is InvalidDataException or NotSupportedException or ArgumentException)
        {
            throw new CompressionException(AudioCompressionFailure.SourceUnreadable, $"{Path.GetFileName(path)}: {e.Message}", e);
        }
    }

    /// <summary>Deletes the CAF only when the name still resolves to a plain file inside <paramref name="folder"/>, never through a symlink.</summary>
    private static void RemoveCaf(string name, string folder)
    {
        if (ConversationFiles.AudioPath(folder, name) is not { } caf || !IsCaf(caf)) return;
        TryDelete(caf);
    }

    private static void RemoveOutputs(string folder)
    {
        foreach (var name in new[] { StereoFileName, MicFileName, SystemFileName }) TryDelete(Path.Combine(folder, name));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (IsFileError(e))
        {
            // Left behind, as Swift's `try?`. A partial output is removed by the next run; a CAF stays.
        }
    }

    /// <summary>
    /// Read-modify-write, so a rename made meanwhile survives; creates the file from <paramref name="session"/> when
    /// missing. Throws when the existing file is unreadable or the write fails.
    /// </summary>
    private static void UpdateConversation(string folder, RecordingSession session, double duration, Action<Conversation> change)
    {
        var conversation = File.Exists(Path.Combine(folder, Conversation.FileName))
            ? Conversation.Read(folder)
            : Conversation.FromSession(session, folder, duration);
        change(conversation);
        conversation.Write();
    }

    private static void TryUpdateConversation(string folder, RecordingSession session, double duration, Action<Conversation> change)
    {
        try
        {
            UpdateConversation(folder, session, duration, change);
        }
        catch (Exception e) when (IsFileError(e) || e is JsonException)
        {
        }
    }

    private static Conversation? TryReadConversation(string folder)
    {
        try
        {
            return Conversation.Read(folder);
        }
        catch (Exception e) when (IsFileError(e) || e is JsonException)
        {
            return null;
        }
    }

    /// <summary>Compression may run after transcription, so it keeps a transcription outcome instead of resetting it to ready.</summary>
    private static ConversationStatus DoneStatus(ConversationStatus? before, bool hasTranscript) =>
        before == ConversationStatus.TranscriptionFailed
            ? ConversationStatus.TranscriptionFailed
            : hasTranscript ? ConversationStatus.Transcribed : ConversationStatus.Ready;

    private static CompressionResult Failed(string folder, AudioCompressionFailure failure, string? detail = null) =>
        new(folder, [], failure, detail);

    private static bool IsCaf(string path) => Path.GetExtension(path).Equals(".caf", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException;

    private sealed class CompressionException(AudioCompressionFailure failure, string message, Exception? inner = null)
        : Exception(message, inner)
    {
        public AudioCompressionFailure Failure { get; } = failure;
    }
}
