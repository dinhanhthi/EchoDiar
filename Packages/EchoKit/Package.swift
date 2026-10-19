// swift-tools-version: 6.1
import PackageDescription

let package = Package(
    name: "EchoKit",
    platforms: [.macOS(.v15)],
    products: [
        .library(name: "EchoCore", targets: ["EchoCore"]),
        .library(name: "EchoAudio", targets: ["EchoAudio"]),
        .library(name: "EchoTranscribe", targets: ["EchoTranscribe"]),
        .library(name: "EchoAI", targets: ["EchoAI"]),
    ],
    dependencies: [
        // Same repo and tag as argmax-oss-swift 1.1.1; this URL keeps one package identity with speech-swift's dependency.
        .package(url: "https://github.com/argmaxinc/WhisperKit", exact: "1.1.1"),
        // traits: [] drops the default NemoTextProcessing trait (prebuilt Rust xcframework, only used for TTS/ITN).
        .package(url: "https://github.com/FluidInference/FluidAudio", exact: "0.17.7", traits: []),
        .package(url: "https://github.com/soniqo/speech-swift", exact: "0.0.28"),
        // Not linked yet (EchoLocalLLM, Phase 9); declared now so both MLX users resolve together.
        // speech-swift 0.0.28 pins mlx-swift-lm 3.31.4 and swift-transformers from 1.1.6.
        .package(url: "https://github.com/ml-explore/mlx-swift-lm", exact: "3.31.4"),
        .package(url: "https://github.com/huggingface/swift-transformers", .upToNextMajor(from: "1.1.6")),
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
        .target(
            name: "EchoTranscribe",
            dependencies: [
                "EchoCore",
                "EchoAI",
                .product(name: "WhisperKit", package: "WhisperKit"),
                .product(name: "FluidAudio", package: "FluidAudio"),
                .product(name: "Qwen3ASR", package: "speech-swift"),
            ],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "EchoTranscribeTests",
            dependencies: ["EchoTranscribe"],
            path: "Tests/EchoTranscribeTests"
        ),
        .target(
            name: "EchoAI",
            dependencies: ["EchoCore"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "EchoAITests",
            dependencies: ["EchoAI"],
            path: "Tests/EchoAITests"
        ),
    ]
)
