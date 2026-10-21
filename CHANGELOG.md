# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-10-21

### Added

- Record one meeting app (Google Meet in a browser, Teams, Zoom, ...) or all system audio, together with your microphone, on separate channels of the same file. Pause and resume, live level meters, and a menu bar icon that shows the recording state.
- Recordings are compressed to AAC after each session; the original is deleted only after the compressed file is checked (with "Delete audio after transcription", the uncompressed audio goes to the Trash instead). A recording interrupted by a crash is recovered and kept.
- A conversation library grouped by day, with rename, Reveal in Finder, delete to the Trash and size on disk, plus search (⌘F) over titles and transcripts.
- On-device transcription with WhisperKit, Parakeet, Qwen3-ASR or Apple Speech, with optional speaker separation (diarization) of the meeting audio ("Tell speakers apart", off by default: the meeting audio is then one speaker). A recording is transcribed from its uncompressed audio, then compressed. English, French and Vietnamese come first in every language menu. Models are downloaded only when you choose them.
- Transcribe while recording (on by default, Settings > Transcription): with WhisperKit or Parakeet, each channel's speech is transcribed as it comes, the live captions come from the same model, and the transcript is ready seconds after Stop instead of minutes. Other engines transcribe the whole recording after Stop. The model's memory is freed while a recording stays paused (after 2 minutes by default).
- A transcript view with playback: click a line to hear it.
- Optional cloud transcription with OpenAI-compatible services, ElevenLabs, Gemini, Deepgram and AssemblyAI. Audio is sent only after you agree for each destination, and API keys are stored in the Keychain.
- AI summaries and chat about a conversation, with Apple's on-device Foundation Models, local MLX models, local servers (LM Studio, Ollama, Jan, ...), cloud providers (OpenAI, Anthropic, Gemini, Mistral, OpenRouter, ...) or coding CLIs (Claude, Codex, OpenCode, Gemini). Answers cite timestamps that jump to that moment in the recording.
- Copy as prompt: copies the transcript and your question, ready to paste into any AI tool.
- Meeting detection that offers to record when a call starts (or starts on its own for the apps you choose), a floating recording indicator (small, medium or large) that can follow the meeting window, and auto-stop when the meeting ends.
- Storage settings that delete old recordings automatically (the whole conversation or only its audio).
- Global shortcuts to start and stop recording (⌥⌘R by default) and to pause and resume it (⌥⌘P by default).
- First-run onboarding with a consent reminder, the microphone prompt and a system audio test.
- Automatic update checks in the background; "Check for Updates…" in the menu bar menu.
- Ask across conversations (opt-in, Settings › RAG): ask about all your transcribed conversations from the sparkles button in the toolbar; answers cite the conversation and the moment, and a click opens it at that time. Conversations are indexed in the background on this Mac by default (Qwen3-Embedding, multilingual-e5, bge-m3 or Apple's built-in model), or with an embedding API after you agree for each destination; indexing pauses while recording.
