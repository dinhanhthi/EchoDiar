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
        .library(name: "EchoLocalLLM", targets: ["EchoLocalLLM"]),
    ],
    dependencies: [
        // Same repo and tag as argmax-oss-swift 1.1.1; this URL keeps one package identity with speech-swift's dependency.
        .package(url: "https://github.com/argmaxinc/WhisperKit", exact: "1.1.1"),
        // traits: [] drops the default NemoTextProcessing trait (prebuilt Rust xcframework, only used for TTS/ITN).
        .package(url: "https://github.com/FluidInference/FluidAudio", exact: "0.17.7", traits: []),
        .package(url: "https://github.com/soniqo/speech-swift", exact: "0.0.28"),
        // speech-swift 0.0.28 pins mlx-swift-lm 3.31.4 and takes mlx-swift from 0.30.0 and swift-transformers from
        // 1.1.6; EchoLocalLLM pins the versions they resolve to.
        .package(url: "https://github.com/ml-explore/mlx-swift-lm", exact: "3.31.4"),
        .package(url: "https://github.com/ml-explore/mlx-swift", exact: "0.31.6"),
        .package(url: "https://github.com/huggingface/swift-transformers", exact: "1.3.5"),
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
        .target(
            name: "EchoLocalLLM",
            dependencies: [
                "EchoCore",
                "EchoAI",
                .product(name: "MLX", package: "mlx-swift"),
                .product(name: "MLXLLM", package: "mlx-swift-lm"),
                .product(name: "MLXLMCommon", package: "mlx-swift-lm"),
                .product(name: "MLXEmbedders", package: "mlx-swift-lm"),
                .product(name: "Tokenizers", package: "swift-transformers"),
            ],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
    ]
)
