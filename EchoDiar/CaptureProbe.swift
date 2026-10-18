#if DEBUG
import AVFoundation
import EchoAudio
import EchoCore
import Foundation

/// DEBUG harness `--capture-probe <seconds> [bundleID|pid:N|global]`: records a probe session whatever the TCC
/// state (denied = silence, prompts are never awaited), writes `probe.json` and the same JSON to stdout, then exits.
enum CaptureProbe {
    static let argument = "--capture-probe"

    private struct Failure: Error {
        let message: String
    }

    static var isRequested: Bool { ProcessInfo.processInfo.arguments.contains(argument) }

    static var outputURL: URL {
        RecordingSession.conversationsRoot.deletingLastPathComponent().appendingPathComponent("probe.json")
    }

    /// Runs off the main thread so the main run loop keeps serving Core Audio and AVAudioEngine; calls `exit`.
    static func run() {
        let arguments = ProcessInfo.processInfo.arguments
        DispatchQueue.global(qos: .userInitiated).async {
            do {
                emit(try probe(arguments: arguments))
                exit(0)
            } catch let failure as Failure {
                emit(["error": failure.message])
                exit(1)
            } catch {
                emit(["error": String(describing: error)])
                exit(1)
            }
        }
    }

    private static func probe(arguments: [String]) throws -> [String: Any] {
        guard let index = arguments.firstIndex(of: argument), index + 1 < arguments.count,
              let seconds = Double(arguments[index + 1]), seconds > 0 else {
            throw Failure(message: "usage: \(argument) <seconds> [bundleID|pid:N|global]")
        }
        let targetName = index + 2 < arguments.count ? arguments[index + 2] : "global"
        let target = try resolve(targetName)

        let engine = RecordingEngine()
        let folder = try engine.start(target: target, language: "auto", probe: true)
        var livePeak: [Float] = [0, 0]
        let ticks = Int((seconds * 10).rounded())
        for _ in 0..<ticks {
            Thread.sleep(forTimeInterval: 0.1)
            let levels = engine.currentLevels()
            livePeak[0] = max(livePeak[0], levels.mic)
            livePeak[1] = max(livePeak[1], levels.system)
        }
        // Nil when the engine already stopped itself (disk full, write failure); its session.json is final.
        let session = try engine.stop() ?? RecordingSession.read(from: folder)
        let snapshot = engine.currentSnapshot()

        let analysis = try analyze(folder.appendingPathComponent(session.audioFileName))
        let names = ["mic", "system"]
        let channels: [[String: Any]] = analysis.channels.indices.map { i in
            [
                "name": names[i],
                "rms": analysis.channels[i].rms,
                "peak": analysis.channels[i].peak,
                "livePeak": livePeak[i],
                "onsetSeconds": analysis.channels[i].onset.map { $0 as Any } ?? NSNull(),
            ]
        }
        var drift: Any = NSNull()
        if let mic = analysis.channels[0].onset, let system = analysis.channels[1].onset {
            drift = system - mic
        }
        return [
            "target": targetName,
            "tapMode": session.tapMode.rawValue,
            "globalTapFallback": session.globalTapFallback,
            "durationSeconds": Double(analysis.frames) / ChannelWriter.sampleRate,
            "framesWritten": analysis.frames,
            "channels": channels,
            "driftSeconds": drift,
            "silentTapSuspected": snapshot.silentTapSuspected,
            "endReason": session.endReason.map { $0 as Any } ?? NSNull(),
            "sessionFolder": folder.path,
        ]
    }

    private static func resolve(_ name: String) throws -> CaptureTarget {
        if name == "global" { return .global }
        if name.hasPrefix("pid:") {
            guard let pid = pid_t(name.dropFirst(4)) else { throw Failure(message: "invalid pid: \(name)") }
            guard let target = CaptureTarget.process(pid: pid) else { throw Failure(message: "no audio process for \(name)") }
            return target
        }
        guard let group = AudioProcessList.groups().first(where: { $0.bundleID == name }) else {
            throw Failure(message: "no audio process group for \(name)")
        }
        return .app(group)
    }

    private struct Analysis {
        var frames: Int64 = 0
        var channels: [(rms: Double, peak: Float, onset: Double?)] = []
    }

    /// Per-channel RMS, peak and onset (first sample at or above -40 dBFS) of the written file.
    private static func analyze(_ url: URL) throws -> Analysis {
        let onsetThreshold: Float = 0.01
        let file = try AVAudioFile(forReading: url)
        let format = file.processingFormat
        let channelCount = Int(format.channelCount)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 16_384) else {
            throw Failure(message: "cannot allocate read buffer")
        }
        var squares = [Double](repeating: 0, count: channelCount)
        var peaks = [Float](repeating: 0, count: channelCount)
        var onsets = [Double?](repeating: nil, count: channelCount)
        var position: Int64 = 0
        while position < file.length {
            try file.read(into: buffer)
            let count = Int(buffer.frameLength)
            guard count > 0, let data = buffer.floatChannelData else { break }
            for channel in 0..<channelCount {
                let samples = data[channel]
                for i in 0..<count {
                    let value = abs(samples[i])
                    squares[channel] += Double(value * value)
                    peaks[channel] = max(peaks[channel], value)
                    if onsets[channel] == nil, value >= onsetThreshold {
                        onsets[channel] = Double(position + Int64(i)) / format.sampleRate
                    }
                }
            }
            position += Int64(count)
        }
        var analysis = Analysis(frames: position)
        for channel in 0..<channelCount {
            let rms = position > 0 ? (squares[channel] / Double(position)).squareRoot() : 0
            analysis.channels.append((rms, peaks[channel], onsets[channel]))
        }
        guard analysis.channels.count >= 2 else { throw Failure(message: "expected 2 channels, found \(channelCount)") }
        return analysis
    }

    private static func emit(_ object: [String: Any]) {
        let data = (try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]))
            ?? Data(#"{"error":"cannot encode probe result"}"#.utf8)
        try? FileManager.default.createDirectory(at: outputURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: outputURL, options: .atomic)
        FileHandle.standardOutput.write(data + Data("\n".utf8))
    }
}
#endif
