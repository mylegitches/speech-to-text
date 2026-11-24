# SpeechToText

Talk instead of type. Hold a hotkey, speak, and let go: the text appears where your cursor is, or goes into your notes.

SpeechToText lives in the Windows tray. It records only while you hold a hotkey, and sends that recording only to the endpoint you choose.

## Quick start
1. Run `.\install.ps1`. It builds one `SpeechToText.exe`, installs it for you (no admin needed), adds it to the Start menu and starts it. Settings opens the first time.
2. In **Transcription**, pick a provider and paste an API key, then press **Test**.
3. Click into any text box, hold `Ctrl+Alt+Space`, talk, and let go.

| | |
|---|---|
| Update | Run `.\install.ps1` again. It closes the running copy and replaces it. |
| Open Settings | Start SpeechToText from the Start menu. If it's already running, that opens its Settings. |
| Uninstall | `.\install.ps1 -Uninstall`. Your settings and saved keys are kept. |
| Develop | `dotnet run --project src/SpeechToText` |

**Sizes:**
- The app is one self-contained 127 MB `.exe` in `%LOCALAPPDATA%\Programs\SpeechToText`. It needs no .NET install.
- While idle in the tray it uses about 75 MB of RAM (21 MB private).
- Building needs the .NET 10 SDK.

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

**What leaves your PC:** only the recording, only after you press a hotkey, and only to the endpoint you chose. Audio is never written to disk. API keys are kept in Windows Credential Manager, not in the settings file.

### On this PC (offline)
Pick **On this PC** to run Whisper on your own machine. Nothing leaves your PC.
- **Device:** chosen automatically. NVIDIA with the CUDA Toolkit is tried first, then any GPU through Vulkan, then the CPU. You can override it.
- **One-time download:** press **Download**. You get the model plus the runtime for your device. Downloads are checked against their published SHA-256.

| Model | Size | Measured on an Intel Arc 140V (Vulkan), 10 s of speech |
|---|---|---|
| large-v3, best accuracy (default) | 2.9 GB | 3.0 s (first run 10.6 s), 3.3 GB RAM while loaded |
| large-v3-turbo (q8), nearly as accurate | 834 MB | 2.0 s (first run 7.4 s), 1.2 GB RAM while loaded |

**Runtimes:**
- CPU: 2 MB
- Vulkan: 57 MB
- CUDA 13: 150 MB
- CUDA 12: 540 MB

The model is unloaded after 5 idle minutes, so it doesn't hold memory while you're not dictating. On CPU alone, expect several times your recording length; that test PC needed 27 s for 10 s of speech.

## Notes
Turn on as many targets as you like:

| Target | Where the note goes |
|---|---|
| QuickNotes file | `my_notes.txt` on this PC, one line per note (`20251124093000V text`). This is the same format as the old quick-notes script |
| Obsidian / Logseq / Markdown | A Markdown file on this PC. The path and the line are templates, e.g. `Daily\{yyyy-MM-dd}.md` |
| Joplin | The Joplin desktop app on this PC, through its Web Clipper API |
| Notion | Added as a bullet on a Notion page. The note is sent to Notion |
| Webhook | A JSON POST to a URL you choose. Use Zapier, Make or n8n to reach Evernote, OneNote, Todoist and others |

### Rich notes
The note box is a rich editor. Notes are saved as Markdown, so they also read well in Obsidian or any text editor.
- **Formatting:** paste from Word or Outlook and the formatting is kept. Paste from a web page and bold, italics, links, headings and lists are kept. Shortcuts: `Ctrl+B`, `Ctrl+I`, `Ctrl+Shift+L` for bullets.
- **Images:** screenshots, copied images and image files show up inline. They're saved to `my_notes_assets\` next to your notes file.
- **Where images end up in other targets:**
  - Obsidian / Markdown: a copy goes into an `attachments\` folder next to the day's file.
  - Joplin: an `[image]` placeholder.
  - Notion: plain text.
  - Webhook: the image file paths are included in the JSON.
- **Images from web pages:** these are pasted as links, not downloaded.
- **Storage:** in `my_notes.txt` each note stays on one line, with line breaks stored as `⏎`.

### Computer audio
Settings → Audio → **Also record computer audio** mixes whatever is playing on an output device (a call, a video) into dictation and voice notes. Pick the output device, or keep Windows default. If that device can't be recorded, you get a notice and it records your mic alone.

Everyone you can hear gets transcribed, so make sure the people on a call are OK with that.

### Notes manager
Open it by double-tapping the Note hotkey (Hold mode), from the tray (**Notes…**), or with `SpeechToText.exe --notes`. It lists your QuickNotes file newest first.
- Search with `Ctrl+F`.
- Double-click a note to open it in the rich editor.
- Copy a note with its copy button.
- **Delete is reversible.** A deleted note moves to `my_notes.deleted.txt` next to your notes file, and **Undo** puts it back where it was.
- Notes saved while the window is open appear right away.

## Files
- Settings: `%APPDATA%\SpeechToText\settings.json`
- Offline models and runtimes: `%LOCALAPPDATA%\SpeechToText\whisper`
- Error log: `%APPDATA%\SpeechToText\error.log`

## Branding
This app follows the OwMyContext family rules:
- dark-first tokens with the family-default forest-green accent
- the OML mark as the app icon
- PascalCase product name

See [CLAUDE.md](CLAUDE.md).
