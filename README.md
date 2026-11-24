# SpeechToText

Talk instead of type. Hold a hotkey, speak, and let go: the text appears where your cursor is, or goes into your notes.

SpeechToText lives in the Windows tray. It records only while you hold a hotkey, and sends that recording only to the endpoint you choose.

## Quick start
1. `dotnet run --project src/SpeechToText`. Settings opens the first time you run it.
2. In **Transcription**, pick a provider and paste an API key, then press **Test**.
3. Click into any text box, hold `Ctrl+Alt+Space`, talk, and let go.

## Hotkeys
You can change both in Settings → General.

| Hotkey | Default | Does |
|---|---|---|
| Dictate | `Ctrl+Alt+Space` | Pastes the text at your cursor, saves it to your notes, or both |
| Note | `Ctrl+Delete` | Hold to record a voice note. A quick tap opens a box for a typed note |

Each hotkey has its own mode:
- **Smart:** tap to start and stop, or hold to talk.
- **Hold:** records only while you hold the key.
- **Toggle:** press once to start, again to stop.

**Esc** cancels a recording.

## Transcription
You can use Groq, OpenAI, or **any OpenAI-compatible endpoint**, such as Speaches, LocalAI or faster-whisper-server running on your PC or NAS.

**What leaves your PC:** only the recording, only after you press a hotkey, and only to the endpoint you chose. Audio is never written to disk. API keys are kept in Windows Credential Manager, not in the settings file. To keep everything local, point it at a server on your own machine.

## Notes
Turn on as many targets as you like:

| Target | Where the note goes |
|---|---|
| QuickNotes file | `my_notes.txt` on this PC, one line per note (`20251124093000V text`). This is the same format as the old quick-notes script |
| Obsidian / Logseq / Markdown | A Markdown file on this PC. The path and the line are templates, e.g. `Daily\{yyyy-MM-dd}.md` |
| Joplin | The Joplin desktop app on this PC, through its Web Clipper API |
| Notion | Added as a bullet on a Notion page. The note is sent to Notion |
| Webhook | A JSON POST to a URL you choose. Use Zapier, Make or n8n to reach Evernote, OneNote, Todoist and others |

## Files
- Settings: `%APPDATA%\SpeechToText\settings.json`
- Error log: `%APPDATA%\SpeechToText\error.log`

## Branding
This app follows the OwMyContext family rules:
- dark-first tokens with the family-default forest-green accent
- the OML mark as the app icon
- PascalCase product name

See [CLAUDE.md](CLAUDE.md).
