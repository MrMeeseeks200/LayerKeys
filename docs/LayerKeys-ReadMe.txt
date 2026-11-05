LAYER KEYS — QUICK START
========================

1. Plug your USB MIDI keyboard into Windows.
2. Double-click the Layer Keys executable from the latest GitHub release.
3. Play. Layer Keys automatically connects to the first available MIDI input.

PLAYING
-------
- Use the large − OCTAVE and + OCTAVE buttons to change range.
- Keyboard shortcuts: Z = octave down, X = octave up.
- Choose a sound from the Sound menu.
- Use the MIDI INPUT menu if more than one MIDI device is connected. Layer Keys
  automatically prefers a physical keyboard over loopMIDI virtual ports.
- Adjust KEY SENSITIVITY from 25% to 150%. The responsive setting previously
  labeled 200% is now the 100% default. Its smooth curve boosts soft presses
  without abruptly clipping louder notes.
- KEYBOARD GAIN changes the instrument level without changing the sensitivity
  curve. MIC GAIN independently changes microphone-track playback and export
  level. Both range from 0% to 200% and are saved with the project.
- Lower keyboard notes receive gradual loudness compensation so the piano feels
  more even across its range without making higher notes harsher.

RECORDING AND LAYERING
----------------------
- No loop is the default: recording continues until you press FINISH TRACK or
  STOP, and playback runs through the arrangement once without repeating.
- Select 2, 4, or 8 bars when you want an automatically repeating loop.
- Press RECORD A TRACK, then play. Recording stops at the end of the loop.
- To record voice or another acoustic source too, enable RECORD MICROPHONE ON
  SEPARATE TRACK and choose a microphone. The keyboard and microphone are captured
  simultaneously as independent clips, so either can be moved, split, or muted.
- Leave the microphone option off for the original one-button keyboard workflow.
- Press RECORD A TRACK again to add another layer while earlier tracks play.
- NEW TRACK → Layer on top records from bar 1 over the existing arrangement.
- NEW TRACK → Record after existing appends the take after the last track.
- Recordings appear as colored waveform-style clips on a traditional timeline.
- Microphone clips draw their real waveform live while recording. Quiet takes are
  visually normalized so their waveform remains readable without changing sound.
- Drag a clip left or right to move it freely. Bar lines are visual guides only;
  clips no longer snap to them.
- Waveform peaks show MIDI note velocity and horizontal marks show duration.
- Press M in a track header to mute it, or × to delete it.
- Click a point inside a clip and press SPLIT. The material after that point moves
  to a new track lane without changing its timestamp.
- Either half of a split clip can be dragged left or right. The later half can be
  moved all the way back to the beginning of the arrangement.
- The Space bar also starts or finishes recording.

PROJECTS AND EXPORTING
----------------------
- SAVE PROJECT stores the complete editable arrangement, including recorded
  microphone audio, as an .lkproj file.
- LOAD PROJECT restores its tempo, sounds, tracks, splits, mutes, and positions.
- EXPORT WAV and EXPORT MP3 capture the same Windows synthesizer used during
  playback, so the exported instrument tone matches what you hear in the app.
- Audio export runs in real time: a three-minute song takes about three minutes.
  The song plays audibly during export. Avoid playing other computer audio while
  exporting because Windows output is being captured.
- During export, the normal interface, MIDI input, keyboard shortcuts, and window
  controls are locked. ABORT EXPORT is the only available action; it stops the
  export and removes the incomplete audio file.
- MP3 conversion uses FFmpeg, which is already installed on this computer;
  another PC will need FFmpeg installed.
- EXPORT MIDI creates a standard .mid file for editing in a DAW or notation app.
- MP4 is a video container and is not produced directly; use the WAV export when
  you need an audio file.

NOTES
-----
- Sound uses the standard Windows MIDI synthesizer.
- Tracks are kept for the current session and clear when the app closes.
- If you connect the keyboard after opening the app, wait about two seconds.
- If the app says the keyboard is busy, close other music software using it.
