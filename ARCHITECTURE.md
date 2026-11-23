# Signal Uno architecture

The solution has one frontend and five reusable .NET libraries.

| Project | Responsibility |
| --- | --- |
| [Engine](native/Engine) | Song and track models, edit history, tempo and meter, playback scheduling, live MIDI routing and recording. |
| [MidiFiles](native/MidiFiles) | Standard MIDI File import, export, and preservation of source events using DryWetMIDI. |
| [SoundFont](native/SoundFont) | MeltySynth instruments and offline Ogg Vorbis export. |
| [Application](native/Application) | Song sessions, including open, save, crop, and crop undo. Also supplies recording preference storage and the MIDI monitor buffer. |
| [Desktop.Devices](native/Desktop.Devices) | PortAudio output and RtMidi input, including device enumeration and timestamped callbacks. |
| [Uno.App](native/Uno.App) | Windows, dialogs, keyboard and pointer input, Skia rendering, and desktop startup. |

`Engine` is independent of the other projects. `MidiFiles`, `SoundFont`, and `Desktop.Devices` depend on `Engine`. `Application` uses `Engine` and `MidiFiles` to coordinate song edits and file operations. `Uno.App` composes these services and owns the UI framework dependency.

A replacement frontend can use `SongSession` as its document entry point and use the engine models for editing and transport. Keep window state, pointer gestures, and picker behavior in the frontend. Run song edits and session operations on one editor context and await file operations before starting another edit.

The audio callback drives engine rendering on the device clock. MIDI input supplies monotonic receipt timestamps. The engine schedules monitored notes and collects recorded notes independently of the UI, which samples transport and recording state for display.
