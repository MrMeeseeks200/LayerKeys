# Layer Keys

Layer Keys is a native Windows MIDI keyboard player and multitrack recorder. It is designed to stay simple when you only want to plug in a keyboard and play, while also providing an arrangement timeline for recording, layering, splitting, freely positioning, saving, loading, and exporting tracks.

The repository also includes a MIDI keyboard emulator that sends computer-keyboard notes to a loopMIDI virtual port for other MIDI software.

## Downloads

- `release/LayerKeysStudio-v9.exe` — player, recorder, arranger, and WAV/MP3/MIDI exporter
- `release/MidiKeyboardEmulator.exe` — QWERTY-to-virtual-MIDI controller

See the files in `docs/` for setup and usage instructions.

## Highlights

- Automatic USB MIDI keyboard detection
- Selectable MIDI input with automatic preference for physical keyboards
- One-button octave switching
- No-loop recording by default, with optional 2/4/8-bar loops
- Layered track timeline with waveform-style MIDI clips
- Free clip positioning without grid snapping
- Track splitting, muting, moving, and deletion
- Editable project save/load
- MIDI, WAV, and MP3 export
- WAV/MP3 export captures the Windows synthesizer used during playback
- Locked export screen with a single **Abort Export** action

## Building

The applications are Windows Forms programs targeting .NET Framework 4. The current Layer Keys build embeds NAudio 1.10.0 for Windows loopback capture. FFmpeg is required at runtime for MP3 conversion; WAV and MIDI export do not require FFmpeg.

The source files are under `src/`. The checked-in executables were built with the .NET Framework C# compiler included with Windows.

## Third-party software

Layer Keys embeds NAudio 1.10.0, used under the MIT License. See `THIRD_PARTY_NOTICES.md`.

