import Accelerate
import Darwin
import Synchronization

/// Single-producer single-consumer mono Float32 ring for audio IO threads.
///
/// Overflow drops the newest frames (the producer never moves the consumer's read index);
/// `overruns` counts the dropped frames. Every written block is stamped with its host time,
/// and `read` never crosses a block boundary, so the consumer sees each block's timing and any gap.
///
/// `@unchecked Sendable` by SPSC contract: one thread calls `write`, one thread calls `read`/`availableFrames`; shared state is atomics only.
public final class RingBuffer: @unchecked Sendable {
    private struct Stamp {
        var frameIndex: Int
        var hostTime: UInt64
    }

    public let capacity: Int
    public let sampleRate: Double

    private let frameMask: Int
    private let frames: UnsafeMutablePointer<Float>
    private let stampCount: Int
    private let stampMask: Int
    private let stamps: UnsafeMutablePointer<Stamp>
    private let hostTicksPerFrame: Double

    private let writeIndex = Atomic<Int>(0)
    private let readIndex = Atomic<Int>(0)
    private let stampHead = Atomic<Int>(0)
    private let stampTail = Atomic<Int>(0)
    private let droppedFrames = Atomic<Int>(0)
    private let peakBits = Atomic<UInt32>(0)
    private let rmsBits = Atomic<UInt32>(0)

    /// `capacityFrames` is rounded up to a power of two. `sampleRate` is the producer's frame rate,
    /// used to give the host time of a read that starts inside a block.
    public init(capacityFrames: Int, sampleRate: Double) {
        precondition(capacityFrames > 0 && sampleRate > 0)
        capacity = Self.powerOfTwo(atLeast: capacityFrames)
        frameMask = capacity - 1
        frames = .allocate(capacity: capacity)
        frames.initialize(repeating: 0, count: capacity)

        stampCount = Self.powerOfTwo(atLeast: max(256, capacity / 64))
        stampMask = stampCount - 1
        stamps = .allocate(capacity: stampCount)
        stamps.initialize(repeating: Stamp(frameIndex: 0, hostTime: 0), count: stampCount)

        self.sampleRate = sampleRate
        var timebase = mach_timebase_info_data_t()
        mach_timebase_info(&timebase)
        hostTicksPerFrame = 1e9 * Double(timebase.denom) / Double(timebase.numer) / sampleRate
    }

    deinit {
        frames.deallocate()
        stamps.deallocate()
    }

    // MARK: Producer (IO thread)

    public func write(_ samples: UnsafePointer<Float>, count: Int, hostTime: UInt64) {
        guard count > 0 else { return }
        updateLevels(samples, count: count)

        let head = writeIndex.load(ordering: .relaxed)
        let tail = readIndex.load(ordering: .acquiring)
        let n = min(count, capacity - (head - tail))
        let stampSlot = stampHead.load(ordering: .relaxed)
        let stampFull = stampSlot - stampTail.load(ordering: .acquiring) >= stampCount
        guard n > 0, !stampFull else {
            droppedFrames.wrappingAdd(count, ordering: .relaxed)
            return
        }

        let start = head & frameMask
        let first = min(n, capacity - start)
        (frames + start).update(from: samples, count: first)
        if first < n {
            frames.update(from: samples + first, count: n - first)
        }

        stamps[stampSlot & stampMask] = Stamp(frameIndex: head, hostTime: hostTime)
        stampHead.store(stampSlot + 1, ordering: .releasing)
        writeIndex.store(head + n, ordering: .releasing)
        if n < count {
            droppedFrames.wrappingAdd(count - n, ordering: .relaxed)
        }
    }

    private func updateLevels(_ samples: UnsafePointer<Float>, count: Int) {
        var peak: Float = 0
        var rms: Float = 0
        vDSP_maxmgv(samples, 1, &peak, vDSP_Length(count))
        vDSP_rmsqv(samples, 1, &rms, vDSP_Length(count))
        // Non-negative Float bit patterns order like their values, so an integer max works.
        if peak.isFinite { peakBits.max(peak.bitPattern, ordering: .relaxed) }
        if rms.isFinite { rmsBits.max(rms.bitPattern, ordering: .relaxed) }
    }

    // MARK: Consumer

    /// Copies up to `maxFrames` frames, stopping at the next block boundary.
    /// `hostTime` is the host time of the first copied frame (0 when nothing was read).
    public func read(into destination: UnsafeMutablePointer<Float>, maxFrames: Int) -> (frames: Int, hostTime: UInt64) {
        let tail = readIndex.load(ordering: .relaxed)
        let available = writeIndex.load(ordering: .acquiring) - tail
        guard available > 0, maxFrames > 0 else { return (0, 0) }

        let head = stampHead.load(ordering: .acquiring)
        var current = stampTail.load(ordering: .relaxed)
        while current + 1 < head, stamps[(current + 1) & stampMask].frameIndex <= tail {
            current += 1
        }
        stampTail.store(current, ordering: .releasing)

        let stamp = stamps[current & stampMask]
        var n = min(maxFrames, available)
        if current + 1 < head {
            n = min(n, stamps[(current + 1) & stampMask].frameIndex - tail)
        }

        let start = tail & frameMask
        let first = min(n, capacity - start)
        destination.update(from: frames + start, count: first)
        if first < n {
            (destination + first).update(from: frames, count: n - first)
        }
        readIndex.store(tail + n, ordering: .releasing)

        let offset = Double(tail - stamp.frameIndex) * hostTicksPerFrame
        return (n, stamp.hostTime &+ UInt64(offset.rounded()))
    }

    public var availableFrames: Int {
        writeIndex.load(ordering: .acquiring) - readIndex.load(ordering: .relaxed)
    }

    /// Total frames dropped because the ring was full.
    public var overruns: Int {
        droppedFrames.load(ordering: .relaxed)
    }

    /// Highest block peak and block RMS since the previous call; safe from any thread.
    public func readLevels() -> (peak: Float, rms: Float) {
        let peak = Float(bitPattern: peakBits.exchange(0, ordering: .relaxed))
        let rms = Float(bitPattern: rmsBits.exchange(0, ordering: .relaxed))
        return (peak, rms)
    }

    private static func powerOfTwo(atLeast value: Int) -> Int {
        var result = 1
        while result < value { result <<= 1 }
        return result
    }
}
