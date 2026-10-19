<div align="center">

<img src="assets/logo_square_512.png" width="128" alt="EchoDiar icon">

# EchoDiar

**Record any meeting on your Mac, see who said what, and ask AI about it.**

[![Latest release](https://img.shields.io/github/v/release/dinhanhthi/EchoDiar)](https://github.com/dinhanhthi/EchoDiar/releases/latest)
[![License: AGPLv3](https://img.shields.io/badge/license-AGPLv3-blue)](LICENSE)
![macOS 15+](https://img.shields.io/badge/macOS-15%2B-blue)

[Website](https://ed.dinhanhthi.com) · [Download](https://github.com/dinhanhthi/EchoDiar/releases/latest) · [Docs](https://ed.dinhanhthi.com/docs.html) · [Changelog](CHANGELOG.md)

<img src="assets/poster.jpg" width="720" alt="EchoDiar: a transcript with speakers and an AI answer that cites timestamps">

</div>

## Features

- Record one meeting app (Google Meet in a browser, Teams, Zoom, ...) or all system audio, plus your microphone, on separate channels. Pause, resume, live level meters and a menu bar status.
- On-device transcription (WhisperKit, Parakeet, Qwen3-ASR or Apple Speech) with speaker separation, optional live captions, and a transcript you can click to hear each line.
- Optional cloud transcription: OpenAI-compatible services, ElevenLabs, Gemini, Deepgram and AssemblyAI.
- AI summaries and chat with on-device Foundation Models, local MLX models, local servers (LM Studio, Ollama, ...), cloud APIs (OpenAI, Anthropic, Gemini, ...) or coding CLIs (Claude Code, Codex, OpenCode, Gemini CLI). Answers cite timestamps that jump to that moment. "Copy as prompt" for any other AI tool.
- Meeting detection that offers to record, a floating indicator that can follow the meeting window, and auto-stop when the call ends.
- A library grouped by day with search (⌘F), compression to AAC, crash recovery, automatic deletion of old recordings, a global shortcut (⌥⌘R) and automatic updates.

## Install

Download the `.dmg` from [Releases](https://github.com/dinhanhthi/EchoDiar/releases/latest), drag EchoDiar to `/Applications` and launch it. Requires macOS 15 or later on Apple Silicon.

## Permissions

- **Microphone**: records your voice.
- **System Audio Recording**: records the meeting's audio. Onboarding tests it and opens the right Privacy & Security pane.
- **Accessibility** (optional): only to attach the recording indicator to the meeting window.

Always tell the other participants that you are recording, and follow the law where you and they live.

## Privacy

- Local-first: recordings, transcripts, summaries and chats stay in `~/Library/Application Support/EchoDiar/`. No account, no analytics.
- API keys are stored in the Keychain.
- Audio or transcripts go to a cloud provider or a CLI only when you choose one; cloud transcription asks once for each destination before sending audio.
- Models are downloaded from Hugging Face only when you click Download. Update checks use the Sparkle feed at `ed.dinhanhthi.com`.

## Resource usage

Idle in the background: **27 MB**, 0% CPU (measured 2026-10-19 on a Release build of 0.1.0 with `scripts/measure.sh idle 30`). Recording and transcribing have not been measured yet.

## Development

```sh
brew install xcodegen
scripts/dev.sh   # generate the project, build Debug, launch
```

See [CONTRIBUTING.md](CONTRIBUTING.md). Releases are maintainer-only, see [docs/RELEASING.md](docs/RELEASING.md).

## License

© 2026 Anh-Thi Dinh. Licensed under the GNU Affero General Public License v3.0 (AGPL-3.0), see [LICENSE](LICENSE).
