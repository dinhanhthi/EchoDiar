namespace EchoDiar.Core.Audio;

/// <summary>
/// Decodes compressed recordings (the compressor's m4a) for transcription. Implemented by Platform
/// (MediaFoundationAudio). Failures (a missing, unreadable or undecodable file, no such channel) throw
/// <see cref="IOException"/> or <see cref="InvalidDataException"/>.
/// </summary>
public interface IAudioDecoder
{
    /// <summary>Channels in the file: 2 for a stereo <c>audio.m4a</c> (ch0 mic, ch1 system), 1 for a mono fallback file.</summary>
    int ChannelCount(string path);

    /// <summary>
    /// Seconds of audio obtained by decoding the whole file (Swift <c>AudioCompressor.decodedDuration</c>), so a file
    /// that does not decode to its end fails here.
    /// </summary>
    double Duration(string path);

    /// <summary>
    /// One channel of the whole file as 16 kHz mono Float32. <paramref name="channel"/> picks a source channel, never a
    /// mix (Swift <c>MonoAudio.extract(channel:)</c>): 0 mic and 1 system in a stereo file, 0 in a mono file.
    /// </summary>
    float[] DecodeToMono16k(string path, int channel);
}
