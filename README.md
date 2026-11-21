# Signal Uno

Signal Uno is a multithreaded desktop MIDI sequencer forked from [ryohey’s signal](https://github.com/ryohey/signal). It’s written in C# with .NET 10 and Uno Platform and targets Windows, Linux, and macOS. Audio rendering and MIDI input run independently of the editor’s UI thread.

Use the piano roll to compose and edit MIDI, record from an external MIDI device, and audition instruments from a SoundFont. Edit note velocity, mute and solo tracks, and automate track volume and pan. The app opens and saves Standard MIDI Files and exports audio as Ogg Vorbis.

## Build and run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Run these commands from the repository root.

```sh
dotnet build native/Signal.Native.Uno.slnx -c Release
dotnet run --project native/Uno.App/Signal.Native.Uno.App.csproj -c Release -f net10.0-desktop --no-build
```

See the [desktop guide](native/Uno.App/README.md) for Linux system dependencies, publishing commands, and checks for MIDI input, audio, and file dialogs.

## Tests

```sh
dotnet test native/Signal.Native.Uno.slnx -c Release --no-build --no-restore
```

The [desktop CI workflow](.github/workflows/desktop.yml) builds the Uno app and runs the engine and MIDI file tests on Windows, Linux, and macOS. Audio devices, MIDI controllers, and desktop interaction are checked manually.

## Project structure

The [Uno app](native/Uno.App) uses Skia for the piano roll, PortAudio for audio output, and RtMidi for live MIDI input. The shared C# projects use DryWetMIDI for MIDI files and MeltySynth for SoundFont playback. The [native development notes](native/README.md) cover the sequencing engine and Windows fallback. The original web and Electron projects remain in the repository during the desktop transition.

## Credits and licenses

Signal Uno continues the work of [ryohey and the signal contributors](https://github.com/ryohey/signal/graphs/contributors). The application code is licensed under the [MIT License](LICENSE).

The bundled A320U SoundFont is by Milton Paredes and is distributed under the GNU General Public License, version 2. Its [copyright notice](assets/soundfonts/A320U-license.txt) and [license text](assets/soundfonts/GPL-2.0.txt) are included with desktop builds.
