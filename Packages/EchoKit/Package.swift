// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "EchoKit",
    platforms: [.macOS(.v15)],
    products: [
        .library(name: "EchoCore", targets: ["EchoCore"]),
        .library(name: "EchoAudio", targets: ["EchoAudio"]),
    ],
    targets: [
        .target(
            name: "EchoCore",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "EchoCoreTests",
            dependencies: ["EchoCore"],
            path: "Tests/EchoCoreTests"
        ),
        .target(
            name: "EchoAudio",
            dependencies: ["EchoCore"],
            swiftSettings: [.swiftLanguageMode(.v5), .enableUpcomingFeature("StrictConcurrency")]
        ),
        .testTarget(
            name: "EchoAudioTests",
            dependencies: ["EchoAudio"],
            path: "Tests/EchoAudioTests"
        ),
    ]
)
