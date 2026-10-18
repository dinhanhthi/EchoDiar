import AVFoundation
import EchoCore
import Foundation
import Observation

/// Plays the open transcript's audio file as is (stereo: mic left, system right). The player exists only between
/// `load` and `close`; the play head is read at 4 Hz and only while playing, so nothing runs while paused.
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

    @ObservationIgnored private var player: AVAudioPlayer?
    @ObservationIgnored private var url: URL?
    /// Sorted by start.
    @ObservationIgnored private var segments: [(id: UUID, start: Double, end: Double)] = []
    @ObservationIgnored private var ticker: Task<Void, Never>?

    /// Opens `url` unless it is already open (nil closes the player), and sets the segments to highlight.
    func load(url: URL?, segments: [Transcript.Segment]) {
        self.segments = segments.map { ($0.id, $0.start, $0.end) }.sorted { $0.start < $1.start }
        if url != self.url {
            close()
            self.url = url
            if let url {
                do {
                    let player = try AVAudioPlayer(contentsOf: url)
                    player.prepareToPlay()
                    self.player = player
                    duration = player.duration
                    isReady = true
                } catch {
                    NSLog("EchoDiar: could not open \(url.lastPathComponent) for playback: \(error)")
                }
            }
        }
        if isPlaying || currentTime > 0 { updateActiveSegment() }
    }

    /// Stops and releases the player.
    func close() {
        ticker?.cancel()
        ticker = nil
        player?.stop()
        player = nil
        url = nil
        isReady = false
        isPlaying = false
        currentTime = 0
        duration = 0
        activeSegmentID = nil
    }

    func togglePlay() {
        if isPlaying { pause() } else { play() }
    }

    func play() {
        guard let player, player.play() else { return }
        isPlaying = true
        updateActiveSegment()
        startTicker()
    }

    func pause() {
        player?.pause()
        ticker?.cancel()
        ticker = nil
        isPlaying = false
        tick()
    }

    /// Seeks to `time` (a segment start) and plays.
    func play(from time: Double) {
        guard let player else { return }
        player.currentTime = min(max(0, time), player.duration)
        currentTime = player.currentTime
        play()
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

    /// Reads the play head; the end of the file stops the ticker and clears the highlight (the player rewinds).
    private func tick() {
        guard let player else { return }
        currentTime = player.currentTime
        if isPlaying && !player.isPlaying {
            isPlaying = false
            ticker = nil
            if activeSegmentID != nil { activeSegmentID = nil }
            return
        }
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
