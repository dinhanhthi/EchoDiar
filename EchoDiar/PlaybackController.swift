import AVFoundation
import EchoAudio
import EchoCore
import Foundation
import Observation

/// Plays the open transcript's audio file through an engine: both voices in both ears, or as recorded (mic left,
/// system right) with `PlaybackChannels.split`. The engine exists only between `load` and `close` and runs only
/// while playing; the play head is read at 4 Hz and only while playing, so nothing runs while paused.
@MainActor
@Observable
final class PlaybackController {
    private(set) var isPlaying = false
    /// Seconds; updated at most 4 times a second while playing.
    private(set) var currentTime: Double = 0
    private(set) var duration: Double = 0
    /// The segment under the play head; set only when the play head enters another segment, so the segment list
    /// is not redrawn at every tick.
    private(set) var activeSegmentID: UUID?
    /// An audio file is open.
    private(set) var isReady = false

    @ObservationIgnored private var engine: AVAudioEngine?
    @ObservationIgnored private var player: AVAudioPlayerNode?
    @ObservationIgnored private var file: AVAudioFile?
    @ObservationIgnored private var url: URL?
    @ObservationIgnored private var channels: PlaybackChannels = .mixed
    /// Where the scheduled segment starts; the player's own time counts from there.
    @ObservationIgnored private var startFrame: AVAudioFramePosition = 0
    /// The segment from `startFrame` is on the player.
    @ObservationIgnored private var scheduled = false
    /// Bumped whenever the scheduled segment is dropped, so its late completion is ignored.
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var configurationObserver: (any NSObjectProtocol)?
    /// Sorted by start.
    @ObservationIgnored private var segments: [(id: UUID, start: Double, end: Double)] = []
    @ObservationIgnored private var ticker: Task<Void, Never>?
    /// A `seek(to:)` made before the player existed; applied when `load` opens one.
    @ObservationIgnored private var pendingSeek: Double?

    /// Opens `url` unless it is already open (nil closes the player), and sets the segments to highlight and how
    /// the channels play.
    func load(url: URL?, segments: [Transcript.Segment], channels: PlaybackChannels) {
        self.segments = segments.map { ($0.id, $0.start, $0.end) }.sorted { $0.start < $1.start }
        if url != self.url {
            // A seek made before this view's player opened (recording or compressing) still applies.
            let pending = pendingSeek
            close()
            pendingSeek = pending
            self.channels = channels
            if let url {
                open(url)
                if isReady, let pendingSeek {
                    self.pendingSeek = nil
                    seek(to: pendingSeek)
                }
            }
        } else {
            setChannels(channels)
        }
        if isPlaying || currentTime > 0 { updateActiveSegment() }
    }

    /// Rebuilds the engine for `channels`, keeping the play head and whether it plays.
    func setChannels(_ channels: PlaybackChannels) {
        guard channels != self.channels else { return }
        self.channels = channels
        // The exact play head, not the last 4 Hz reading.
        tick()
        reopen()
    }

    /// Stops and releases the engine.
    func close() {
        ticker?.cancel()
        ticker = nil
        generation += 1
        if let configurationObserver { NotificationCenter.default.removeObserver(configurationObserver) }
        configurationObserver = nil
        player?.stop()
        engine?.stop()
        engine = nil
        player = nil
        file = nil
        url = nil
        startFrame = 0
        scheduled = false
        isReady = false
        isPlaying = false
        currentTime = 0
        duration = 0
        activeSegmentID = nil
        pendingSeek = nil
    }

    func togglePlay() {
        if isPlaying { pause() } else { play() }
    }

    /// Plays from the play head; from the start when it is at the end.
    func play() {
        guard let engine, let player, let file, file.length > 0 else { return }
        if !scheduled {
            if startFrame >= file.length {
                startFrame = 0
                currentTime = 0
            }
            schedule(file, on: player)
        }
        do {
            try engine.start()
        } catch {
            NSLog("EchoDiar: could not start playback: \(error)")
            return
        }
        player.play()
        isPlaying = true
        updateActiveSegment()
        startTicker()
    }

    func pause() {
        tick()
        player?.pause()
        engine?.pause()
        ticker?.cancel()
        ticker = nil
        isPlaying = false
    }

    /// Seeks to `time` (a segment start) and plays.
    func play(from time: Double) {
        seek(to: time)
        if !isPlaying { play() }
    }

    /// Moves the play head to `time`, still playing if it was; before a player exists, `load` applies it once one
    /// opens.
    func seek(to time: Double) {
        guard let player, let file else {
            pendingSeek = time
            currentTime = time
            updateActiveSegment()
            return
        }
        let time = min(max(0, time), duration)
        let wasPlaying = isPlaying
        generation += 1
        player.stop()
        scheduled = false
        startFrame = AVAudioFramePosition(time * file.processingFormat.sampleRate)
        currentTime = time
        if wasPlaying { play() } else { updateActiveSegment() }
    }

    private func open(_ url: URL) {
        self.url = url
        do {
            let file = try AVAudioFile(forReading: url)
            let engine = AVAudioEngine()
            let player = AVAudioPlayerNode()
            PlaybackGraph.connect(player, to: engine, format: file.processingFormat, channels: channels)
            engine.prepare()
            self.file = file
            self.engine = engine
            self.player = player
            duration = Double(file.length) / file.processingFormat.sampleRate
            isReady = true
            // A new output device (headphones plugged in or out) stops the engine.
            configurationObserver = NotificationCenter.default.addObserver(
                forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated { self?.reopen() }
            }
        } catch {
            NSLog("EchoDiar: could not open \(url.lastPathComponent) for playback: \(error)")
        }
    }

    /// Rebuilds the engine for the open file, keeping the play head and whether it plays.
    private func reopen() {
        guard let url else { return }
        let time = currentTime
        let wasPlaying = isPlaying
        close()
        open(url)
        seek(to: time)
        if wasPlaying { play() }
    }

    /// Schedules the file from `startFrame` to its end.
    private func schedule(_ file: AVAudioFile, on player: AVAudioPlayerNode) {
        generation += 1
        let generation = generation
        player.scheduleSegment(
            file, startingFrame: startFrame, frameCount: AVAudioFrameCount(file.length - startFrame), at: nil,
            completionCallbackType: .dataPlayedBack
        ) { [weak self] _ in
            Task { @MainActor in self?.finished(generation) }
        }
        scheduled = true
    }

    /// The end of the file: stops, clears the highlight and rewinds.
    private func finished(_ generation: Int) {
        guard generation == self.generation else { return }
        ticker?.cancel()
        ticker = nil
        player?.stop()
        engine?.pause()
        startFrame = 0
        scheduled = false
        isPlaying = false
        currentTime = 0
        if activeSegmentID != nil { activeSegmentID = nil }
    }

    private func startTicker() {
        ticker?.cancel()
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                guard let self, !Task.isCancelled else { return }
                tick()
                if !isPlaying { return }
            }
        }
    }

    /// Reads the play head while playing.
    private func tick() {
        guard isPlaying, let player, let file, let nodeTime = player.lastRenderTime,
              let time = player.playerTime(forNodeTime: nodeTime) else { return }
        currentTime = min(Double(startFrame + time.sampleTime) / file.processingFormat.sampleRate, duration)
        updateActiveSegment()
    }

    /// The last segment starting at or before the play head, while the play head is inside it.
    private func updateActiveSegment() {
        var low = 0
        var high = segments.count
        while low < high {
            let mid = (low + high) / 2
            if segments[mid].start <= currentTime { low = mid + 1 } else { high = mid }
        }
        let id = low > 0 && currentTime <= segments[low - 1].end ? segments[low - 1].id : nil
        if id != activeSegmentID { activeSegmentID = id }
    }
}
