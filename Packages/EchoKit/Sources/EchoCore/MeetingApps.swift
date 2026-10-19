import Foundation

/// An app whose microphone use suggests a call. Browsers are weaker evidence (any web page can use the mic).
public struct MeetingApp: Sendable, Hashable {
    public let name: String
    /// Matched against `AudioProcessGroup.bundleID` (helpers are already grouped under their app).
    public let bundleIDs: [String]
    public let isBrowser: Bool
    /// The app the user sees (`bundleIDs[0]` unless set): the key of its "Always"/"Ignore" choice, and the app whose
    /// quit ends its meeting. FaceTime calls run in `avconferenced`, but the user quits FaceTime.
    public let appBundleID: String

    public init(name: String, bundleIDs: [String], isBrowser: Bool = false, appBundleID: String? = nil) {
        self.name = name
        self.bundleIDs = bundleIDs
        self.isBrowser = isBrowser
        self.appBundleID = appBundleID ?? bundleIDs[0]
    }
}

public enum MeetingApps {
    /// Meeting apps, then browsers (Meet, Teams web, Zoom web), plus the DEBUG `--extra-meeting-bundle <id>` stand-in.
    public static let known: [MeetingApp] = apps + browsers + extra

    public static func app(forBundleID bundleID: String) -> MeetingApp? {
        known.first { $0.bundleIDs.contains(bundleID) }
    }

    private static let apps = [
        MeetingApp(name: "Zoom", bundleIDs: ["us.zoom.xos"]),
        MeetingApp(name: "Microsoft Teams", bundleIDs: ["com.microsoft.teams2", "com.microsoft.teams"]),
        MeetingApp(name: "Slack", bundleIDs: ["com.tinyspeck.slackmacgap"]),
        MeetingApp(name: "Webex", bundleIDs: ["Cisco-Systems.Spark", "com.cisco.webexmeetingsapp"]),
        // FaceTime calls run their audio in the avconferenced daemon (`AudioProcessList.faceTimeDaemonBundleID`).
        MeetingApp(name: "FaceTime", bundleIDs: ["com.apple.avconferenced"], appBundleID: "com.apple.FaceTime"),
        MeetingApp(name: "Discord", bundleIDs: ["com.hnc.Discord"]),
        MeetingApp(name: "Skype", bundleIDs: ["com.skype.skype"]),
    ]

    private static let browsers = [
        MeetingApp(name: "Google Chrome", bundleIDs: ["com.google.Chrome"], isBrowser: true),
        MeetingApp(name: "Arc", bundleIDs: ["company.thebrowser.Browser"], isBrowser: true),
        MeetingApp(name: "Microsoft Edge", bundleIDs: ["com.microsoft.edgemac"], isBrowser: true),
        MeetingApp(name: "Brave", bundleIDs: ["com.brave.Browser"], isBrowser: true),
        MeetingApp(name: "Firefox", bundleIDs: ["org.mozilla.firefox"], isBrowser: true),
        // Unattributed WebKit GPU processes are grouped under Safari by `AudioProcessList.groups`.
        MeetingApp(name: "Safari", bundleIDs: ["com.apple.Safari", "com.apple.WebKit.GPU"], isBrowser: true),
        MeetingApp(name: "Opera", bundleIDs: ["com.operasoftware.Opera"], isBrowser: true),
        MeetingApp(name: "Vivaldi", bundleIDs: ["com.vivaldi.Vivaldi"], isBrowser: true),
        MeetingApp(name: "Dia", bundleIDs: ["company.thebrowser.dia"], isBrowser: true),
        MeetingApp(name: "Comet", bundleIDs: ["ai.perplexity.comet"], isBrowser: true),
    ]

    private static let extra: [MeetingApp] = {
        #if DEBUG
        let arguments = ProcessInfo.processInfo.arguments
        guard let index = arguments.firstIndex(of: "--extra-meeting-bundle"), index + 1 < arguments.count else { return [] }
        let bundleID = arguments[index + 1]
        return [MeetingApp(name: bundleID, bundleIDs: [bundleID])]
        #else
        return []
        #endif
    }()
}
