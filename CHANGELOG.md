# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Calendar triggers (Settings > Recording > Calendar), with any account added to macOS Calendar (iCloud, Google, Outlook/Exchange, CalDAV) and the optional Calendars permission. "When a meeting starts": Off, Ask (default: the floating indicator offers Record, Join or Not now), Wait for the call (records the meeting app as soon as the call starts) or Record at start (records all system audio and the microphone at the start time). Automatic modes start without asking only for events you organize, accepted or answered Maybe to, and events without invitees on any calendar you can edit, shared ones included (and those you create in EchoDiar); other invites ask. Choose the calendars to watch, keep only events with a meeting link (Zoom, Google Meet, Microsoft Teams, Webex), and stop when the event ends plus a few minutes (10 by default, asked with a countdown). Works while EchoDiar runs, so Launch at login is recommended.
- A recording started from a calendar event is named after the event.
- A Calendar page (toolbar button next to Settings) lists the meetings of the next 7 days, updated automatically, with Join, Record and Skip, and the menu bar menu lists upcoming meetings.
- Create your own meetings from the Calendar page with "New Event…": title, date, start and end, an optional meeting link and Repeat (None, Daily, Weekdays, Weekly or Monthly, ending never or on a date). They go to an "EchoDiar" calendar (On My Mac when available, otherwise your default account, e.g. iCloud, which syncs those events like any other). "Edit…" changes or deletes them; for a repeating event, choose "This Event Only" or "This and Future Events" (only the latter when Repeat or End Repeat changed). Events in the EchoDiar calendar count even without a meeting link, and follow the same "When a meeting starts" mode.
- Settings > Transcription > General > "Remember last record language" (off by default): the language picked in the Record button's menu before recording is kept for the next recordings instead of the default language.

### Changed

- Buttons in Settings and on the Calendar page are capsule-shaped.

## [1.0.2] - 2026-10-21

### Added

- Transcribing a conversation again keeps the earlier transcripts as versions, which you can switch between from the transcript's versions menu.
- One-click update: once an update has downloaded in the background, a green Update button in the toolbar and an "Install Update and Relaunch" menu item install it and relaunch the app without any dialog. Both are hidden or disabled while recording.
- Check for Updates in the app menu.

### Fixed

- The pointer cursor now shows over the whole transcript line, not only its text.
- A bottom bar menu keeps its hover look while it is open.

## [1.0.1] - 2026-10-21

### Added

- Settings > General > Floating indicator > "Show it in screenshots and screen recordings": lets screenshots, screen recordings and screen sharing capture the floating indicator. Off by default, so the indicator stays out of them as before.

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
