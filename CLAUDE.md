# CLAUDE.md

## Shared standards
Read the authorized private `mylegitches/owmycontext` repository before
changing branding or shared conventions. Start with its `README.md`,
`AGENTS.md`, and the matching `branding/products/<slug>.md`.
If access is unavailable, ask for the required files; do not guess.
Never transfer secrets into that repository.

## This project
- No `branding/products/speech-to-text.md` profile exists yet, so the app uses the global defaults:
  - the family-default accent
  - the OML favicon
- Brand tokens live in one place: `src/SpeechToText/Brand.xaml`. It maps `branding/tokens.json` onto WPF Fluent theme keys. Change colors there, not in individual windows.
- Native Windows feel comes first. Use WPF Fluent controls, restyled with the tokens; do not hand-draw controls.
- Light mode is an open OwMyContext owner decision. Until it is settled, the app is dark-only (`ThemeMode="Dark"`).
- `Assets/app.ico` is the OML pack, byte-for-byte. Do not edit it.
- Windows PowerShell 5.1 corrupts UTF-8 files on `Get-Content | Set-Content`. Edit source files with the Edit tool, or with .NET file APIs and explicit UTF-8.
