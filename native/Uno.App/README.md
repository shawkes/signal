# Signal Uno desktop

Signal Uno is a separate .NET 10 application for Windows, Linux, and macOS. It links the Fluent XAML and application logic from the Windows editor. Both apps reference the same Engine, MIDI Files, and SoundFont projects. DryWetMIDI handles MIDI files, MeltySynth produces audio, and the Ogg exporter is shared.

The Uno app draws the visible piano-roll grid and notes with Skia. It uses PortAudio for audio output, RtMidi for live MIDI input, and Uno's standard file pickers. Open, Save As, and Export supply separate picker history IDs. The host controls whether its dialogs restore navigation history.

## Build and run

Run from the repository root with the .NET 10 SDK.

```shell
dotnet build native/Signal.Native.Uno.slnx -c Release
dotnet run --project native/Uno.App/Signal.Native.Uno.App.csproj -c Release -f net10.0-desktop
```

The bundled native audio library supports Windows x64, Linux x64 and ARM64, and macOS Intel and Apple Silicon. Linux needs the [Uno desktop system dependencies](https://platform.uno/docs/articles/features/using-skia-desktop.html#additional-setup), an X11 session or XWayland, a session D-Bus with a file chooser portal, and the ALSA runtime for MIDI. On Debian-based distributions use the distro's ALSA runtime package, commonly `libasound2` or `libasound2t64`.

To publish a runtime-specific build, use the project's desktop target and the destination runtime ID. This example requires .NET 10 on the destination machine.

```shell
dotnet publish native/Uno.App/Signal.Native.Uno.App.csproj -c Release -f net10.0-desktop -r linux-x64 --self-contained false
```

## Desktop smoke checks

On each platform, open and save a MIDI file, add a note with the mouse, verify Space plays and pauses and Ctrl+Z undoes the edit, then audition a piano key or drum row and record from a MIDI input. Confirm the file chooser and shortcuts on both X11 and XWayland where available. The Uno app stores recording offsets separately because device timing can differ between backends.
