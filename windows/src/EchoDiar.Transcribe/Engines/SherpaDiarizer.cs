using EchoDiar.Core;
using EchoDiar.Core.Models;
using SherpaOnnx;

namespace EchoDiar.Transcribe.Engines;

/// <summary>
/// sherpa-onnx offline speaker diarization (Swift <c>SpeakerDiarizer</c>'s pyannote path): pyannote segmentation 3.0
/// (<see cref="ModelEntry.Files"/>) plus a CAM++ speaker embedding (<see cref="ModelEntry.ExtraFiles"/>), both in
/// <c>Models/&lt;id&gt;/</c>. Loaded per call and freed before <see cref="DiarizeAsync"/> returns.
/// </summary>
/// <param name="speakerCountHint">Exact speaker count; null (or 0 and less) = clustered by threshold.</param>
public sealed class SherpaDiarizer(ModelEntry entry, string folder, int? speakerCountHint) : ISpeakerDiarizer
{
    /// <summary>Clustering distance threshold when the speaker count is automatic.</summary>
    public const float ClusteringThreshold = 0.5f;

    /// <summary>A <see cref="TranscriptionPipeline.DiarizerFactory"/>.</summary>
    public static ISpeakerDiarizer Create(ModelEntry entry, string folder, int? speakerCountHint) =>
        new SherpaDiarizer(entry, folder, speakerCountHint);

    public Task<IReadOnlyList<DiarizationTurn>> DiarizeAsync(string channelPath, Action<double> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Diarize(channelPath, progress, cancellationToken), cancellationToken);

    private IReadOnlyList<DiarizationTurn> Diarize(string channelPath, Action<double> progress, CancellationToken cancellationToken)
    {
        var config = Config();
        var samples = MonoAudio.Read(channelPath, 0, null);
        if (samples.Length == 0) return [];
        cancellationToken.ThrowIfCancellationRequested();
        using var diarizer = new OfflineSpeakerDiarization(config);
        // The native side holds a raw pointer to this delegate: keep it alive until Process returns.
        OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
        {
            if (total > 0) progress((double)done / total);
            return 0;
        };
        var segments = diarizer.ProcessWithCallback(samples, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        cancellationToken.ThrowIfCancellationRequested();
        var indexes = new Dictionary<int, int>();
        var turns = new List<DiarizationTurn>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (!indexes.TryGetValue(segment.Speaker, out var index))
            {
                index = indexes.Count;
                indexes[segment.Speaker] = index;
            }
            turns.Add(new DiarizationTurn(segment.Start, segment.End, index));
        }
        progress(1);
        return turns;
    }

    private OfflineSpeakerDiarizationConfig Config()
    {
        var segmentation = entry.Files is [var file] ? Path.Combine(folder, file) : null;
        var embedding = entry.ExtraFiles?.Files is [var extra] ? Path.Combine(folder, extra) : null;
        if (segmentation is null || embedding is null || !File.Exists(segmentation) || !File.Exists(embedding))
        {
            throw new TranscriptionException(TranscriptionErrorKind.NeedsModel, entry.Id);
        }
        var threads = Math.Min(8, Environment.ProcessorCount);
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentation;
        config.Segmentation.NumThreads = threads;
        config.Embedding.Model = embedding;
        config.Embedding.NumThreads = threads;
        config.Clustering.NumClusters = speakerCountHint is int count && count > 0 ? count : -1;
        config.Clustering.Threshold = ClusteringThreshold;
        return config;
    }
}
