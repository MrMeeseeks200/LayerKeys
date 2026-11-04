MIDI KEYBOARD EMULATOR — QUICK START
====================================

Open MidiKeyboardEmulator.exe and play with the computer keyboard. The program
automatically starts loopMIDI when necessary and connects to the virtual port
named "MIDI Keyboard Emulator":

  A W S E D F T G Y H U J K
  C C#D D#E F F#G G#A A#B C

Controls
--------
- Z / X: Octave down / up
- C / V: Lower / raise velocity
- Space: Sustain pedal
- Mouse: Click the on-screen piano keys
- MIDI Program: Sends a General MIDI program-change request to the receiving app;
  the emulator itself never generates audio.

The app does not produce audio. In your DAW, game, synthesizer, or other receiving
software, select "MIDI Keyboard Emulator" as its MIDI input device. Keep loopMIDI
running in the notification area while using the emulator.

If the emulator says WAITING FOR LOOPMIDI, open loopMIDI and confirm that a port
named exactly "MIDI Keyboard Emulator" exists, then wait a moment or restart the
emulator.
