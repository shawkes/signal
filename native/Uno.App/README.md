# Signal Uno desktop

Signal Uno runs on Windows, Linux, and macOS. The frontend owns the XAML views, Skia piano roll, and file pickers. It uses the shared .NET libraries for the song session, sequencing, SoundFont playback, audio export, and desktop audio and MIDI devices. Open, Save As, and Export supply separate picker history IDs to the desktop host.

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

On each platform, open and save a MIDI file, add a note with the mouse, verify Space plays and pauses and Ctrl+Z undoes the edit, then audition a piano key or drum row and record from a MIDI input. Confirm the file chooser and shortcuts on both X11 and XWayland where available. Recording preferences are saved under the current user's local application data in the `Signal Uno` profile.
