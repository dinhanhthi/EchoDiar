import CoreAudio
import Darwin
import Foundation

/// One Core Audio process object.
public struct AudioProcess: Sendable, Hashable {
    public let objectID: AudioObjectID
    public let pid: pid_t
    public let bundleID: String?
    public let isRunningInput: Bool
    public let isRunningOutput: Bool
}

/// Audio processes grouped by the app they belong to (browser helpers with their browser,
/// WebKit GPU processes with their host app).
public struct AudioProcessGroup: Sendable, Hashable {
    public let bundleID: String
    public let name: String
    public internal(set) var processes: [AudioProcess]
    /// Some processes could not be attributed (Safari's WebKit GPU process): only a global tap is reliable.
    public internal(set) var needsGlobalTap: Bool

    public var objectIDs: [AudioObjectID] { processes.map(\.objectID) }
    public var isRunningInput: Bool { processes.contains { $0.isRunningInput } }
    public var isRunningOutput: Bool { processes.contains { $0.isRunningOutput } }
}

/// Synchronous, listener-free queries of the Core Audio process list. Call off the main thread.
public enum AudioProcessList {
    public static let faceTimeDaemonBundleID = "com.apple.avconferenced"
    public static let webKitGPUBundleID = "com.apple.WebKit.GPU"
    public static let safariBundleID = "com.apple.Safari"

    public static func processes() -> [AudioProcess] {
        objectIDs().compactMap(process(for:))
    }

    /// The process object list alone, without reading any process's properties.
    public static func objectIDs() -> [AudioObjectID] {
        var address = globalAddress(kAudioHardwarePropertyProcessObjectList)
        let system = AudioObjectID(kAudioObjectSystemObject)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: kAudioObjectUnknown, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        ids.removeSubrange(min(ids.count, Int(size) / MemoryLayout<AudioObjectID>.size)...)
        return ids
    }

    public static func process(for objectID: AudioObjectID) -> AudioProcess? {
        guard let pid: pid_t = scalar(objectID, kAudioProcessPropertyPID) else { return nil }
        return AudioProcess(
            objectID: objectID,
            pid: pid,
            bundleID: bundleID(of: objectID),
            isRunningInput: (scalar(objectID, kAudioProcessPropertyIsRunningInput) as UInt32?) == 1,
            isRunningOutput: (scalar(objectID, kAudioProcessPropertyIsRunningOutput) as UInt32?) == 1
        )
    }

    public static func processObject(for pid: pid_t) -> AudioObjectID? {
        var address = globalAddress(kAudioHardwarePropertyTranslatePIDToProcessObject)
        var pid = pid
        var objectID = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject), &address,
            UInt32(MemoryLayout<pid_t>.size), &pid, &size, &objectID
        )
        return status == noErr && objectID != kAudioObjectUnknown ? objectID : nil
    }

    public static func ownProcessObject() -> AudioObjectID? {
        processObject(for: getpid())
    }

    public static func groups() -> [AudioProcessGroup] {
        groups(processes())
    }

    /// Groups processes by app, excluding the current process.
    public static func groups(_ list: [AudioProcess]) -> [AudioProcessGroup] {
        let ownPID = getpid()
        var pathBuffer = [CChar](repeating: 0, count: 4 * Int(MAXPATHLEN))
        var appBundleIDs: [String: String] = [:]
        var order: [String] = []
        var byID: [String: AudioProcessGroup] = [:]

        func add(_ process: AudioProcess, bundleID: String, name: String, needsGlobalTap: Bool = false) {
            if byID[bundleID] == nil {
                order.append(bundleID)
                byID[bundleID] = AudioProcessGroup(bundleID: bundleID, name: name, processes: [], needsGlobalTap: false)
            }
            byID[bundleID]!.processes.append(process)
            if needsGlobalTap { byID[bundleID]!.needsGlobalTap = true }
        }

        func appIdentity(pid: pid_t, fallbackBundleID: String?, appOnly: Bool = false) -> (bundleID: String, name: String)? {
            guard let path = executablePath(pid, &pathBuffer) else {
                return appOnly ? nil : fallbackBundleID.map { ($0, $0) }
            }
            guard let appPath = outermostAppPath(path) else {
                if appOnly { return nil }
                let name = (path as NSString).lastPathComponent
                return (fallbackBundleID ?? path, name)
            }
            let name = ((appPath as NSString).lastPathComponent as NSString).deletingPathExtension
            if let cached = appBundleIDs[appPath] { return (cached, name) }
            let id = Bundle(path: appPath)?.bundleIdentifier ?? fallbackBundleID ?? appPath
            appBundleIDs[appPath] = id
            return (id, name)
        }

        for process in list where process.pid != ownPID {
            switch process.bundleID {
            case faceTimeDaemonBundleID:
                add(process, bundleID: faceTimeDaemonBundleID, name: "FaceTime", needsGlobalTap: true)
            case webKitGPUBundleID:
                guard let host = responsiblePID(of: process.pid) else {
                    add(process, bundleID: safariBundleID, name: "Safari", needsGlobalTap: true)
                    continue
                }
                // A host outside any .app (daemon, extension) is known but never a meeting app.
                guard let identity = appIdentity(pid: host, fallbackBundleID: nil, appOnly: true) else { continue }
                add(process, bundleID: identity.bundleID, name: identity.name)
            default:
                guard let identity = appIdentity(pid: process.pid, fallbackBundleID: process.bundleID) else {
                    add(process, bundleID: "pid:\(process.pid)", name: "pid \(process.pid)")
                    continue
                }
                add(process, bundleID: identity.bundleID, name: identity.name)
            }
        }

        // Helpers whose path did not lead to their app (e.g. "com.brave.Browser.helper").
        for id in order {
            guard let root = order.filter({ id.hasPrefix($0 + ".") }).min(by: { $0.count < $1.count }),
                  let helper = byID.removeValue(forKey: id) else { continue }
            byID[root]!.processes += helper.processes
            if helper.needsGlobalTap { byID[root]!.needsGlobalTap = true }
        }
        return order.compactMap { byID[$0] }
    }

    // MARK: - Helpers

    private static func globalAddress(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
    }

    private static func scalar<T: FixedWidthInteger>(_ objectID: AudioObjectID, _ selector: AudioObjectPropertySelector) -> T? {
        var address = globalAddress(selector)
        var value: T = 0
        var size = UInt32(MemoryLayout<T>.size)
        return AudioObjectGetPropertyData(objectID, &address, 0, nil, &size, &value) == noErr ? value : nil
    }

    private static func bundleID(of objectID: AudioObjectID) -> String? {
        var address = globalAddress(kAudioProcessPropertyBundleID)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(objectID, &address, 0, nil, &size, &value) == noErr,
              let id = value?.takeRetainedValue() as String?, !id.isEmpty else { return nil }
        return id
    }

    private static func executablePath(_ pid: pid_t, _ buffer: inout [CChar]) -> String? {
        let length = buffer.withUnsafeMutableBufferPointer { proc_pidpath(pid, $0.baseAddress, UInt32($0.count)) }
        guard length > 0 else { return nil }
        return String(cString: buffer)
    }

    private static func outermostAppPath(_ path: String) -> String? {
        guard let range = path.range(of: ".app/") else { return nil }
        return String(path[..<path.index(before: range.upperBound)])
    }

    /// The app that launched an XPC service (WebKit GPU processes are children of launchd, so ppid is useless).
    /// `responsibility_get_pid_responsible_for_pid` is undocumented libquarantine SPI, looked up at runtime.
    private static func responsiblePID(of pid: pid_t) -> pid_t? {
        guard let lookup = responsibilityLookup else { return nil }
        let host = lookup(pid)
        return host > 0 && host != pid && host != getpid() ? host : nil
    }

    private typealias ResponsibilityFunction = @convention(c) (pid_t) -> pid_t

    private static let responsibilityLookup: ResponsibilityFunction? = {
        guard let symbol = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "responsibility_get_pid_responsible_for_pid") else {
            return nil
        }
        return unsafeBitCast(symbol, to: ResponsibilityFunction.self)
    }()
}
