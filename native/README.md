# Signal Native piano roll

The WinUI 3 prototype edits and plays a single piano-roll track. The recording test monitors a MIDI keyboard through a SoundFont piano, plays a 120 BPM metronome, and reports recorded note placement against the beat. The editor keeps notes and undo history in memory for the current session.

## Build and run

Use x64 Windows (build 19041 or newer) and the .NET 10 SDK. Run these commands from the repository root.

```powershell
dotnet build native/Signal.Native.slnx -c Release
dotnet test native/Engine.Tests -c Release
dotnet run --project native/Windows.App -c Release
```

### Edit and play notes

- Click an empty grid cell or select **Add note** to add a note. Drag a note to change its time or pitch, or drag its right edge to change its length. Horizontal moves and resizing use sixteenth-note increments and preserve the timing of notes recorded off the grid.
- Select a note and use **Delete** or the Delete key to remove it. Use **Undo** or Ctrl+Z to step back one edit. The arrow keys move the selected note. Shift+Left and Shift+Right change its length.
- Select **Play** to hear the current notes from the beginning. The playhead follows the audio timeline. Edits made during playback take effect on the next play. Use **Stop** to end playback early.
- On the **Recording test** tab, finish a take and select **Show in piano roll**. Each take imports as one undoable edit at the next available bar, with timing measured from the bar containing its first note.

### Record a take

1. On the **Recording test** tab, choose **Audio output** and **MIDI input**. **Play test note** exercises the monitor with synthetic MIDI if a keyboard is unavailable.
2. Use **Load SoundFont…** to select an SF2. The app starts with the same `A320U.sf2` General MIDI bank as packaged Electron Signal. [A320U license](../electron/assets/soundfonts/A320U-license.txt)
3. Turn on **Metronome** to hear the click. Select **Record test**, play several notes, then select **Stop recording**. **Raw (ms)** and **Adjusted (ms)** show timing before and after compensation, measured against the beat listed in the row. Positive values indicate a late note. A positive **Recording offset (ms)** shifts the saved note earlier. **Quantize** snaps **Start tick** to the selected grid after compensation and preserves **Length (ms)**.
4. Note the actual sample rate, engine period, and buffer capacity reported for the output device. Start with **Minimum**. If the output exhibits gaps on this endpoint under the target workload, repeat with **Conservative** and then **Default** until the run is clean. Record the actual values for each comparison. Frames divided by sample rate give the corresponding duration in seconds. The period is the render cadence and the capacity is the endpoint's allocated buffer size. [Microsoft low-latency audio documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)

### Workspace verification

The available **Remote Audio** endpoint negotiated 44,100 Hz stereo with event-driven `IAudioClient3` shared mode. The actual period was **441 frames (10.00 ms)** and the buffer capacity was **970 frames (22.00 ms)**. All three period settings negotiated the same values.

- Twenty automated Release tests passed, covering recording timing, editing and undo, and sample-based playback. The WinUI app loaded a second SF2 through the picker and switched back to A320U. A synthetic C4 in the latency test showed +10.02 ms before and +0.02 ms after compensation with the default +10 ms offset.
- A piano-roll playback probe captured two synthetic notes, exercised the edits, and played three edited notes through A320U and the Remote Audio endpoint. It produced nonzero PCM, completed naturally, and stopped early on command in a second pass. The probe recorded zero estimated underruns and clock failures.
- A headless run through A320U, MeltySynth, and WASAPI captured all 80 synthetic notes per pass with a +10 ms offset and a 1/16 grid. Both idle and loaded passes produced piano and metronome output with zero estimated underruns or clock failures and zero managed allocations on the audio thread after warmup. The loaded pass used eight busy CPU workers on 16 logical CPUs. A separate injected 90 ms render stall produced one estimated underrun, then the stream recovered.
- An offline PCM check scheduled C4 at velocity 100 exactly on the click. The piano first crossed a 0.005 absolute sample-amplitude threshold 2.00 ms after the click at 44.1 kHz and 2.02 ms after it at 48 kHz. These values include the SoundFont attack and MeltySynth's internal render block.

Windows reported zero MIDI inputs. Physical key-to-sound median, p95, and comparative dropouts remain unmeasured.

## Project boundaries

| Folder | Responsibility |
| --- | --- |
| `Engine` | Portable `net10.0` song model, tempo map, tick and sample conversions, timestamp mapping, scheduling, and recording. |
| `Engine.Tests` | Synthetic clock, offset, quantization, and scheduling checks. |
| `SoundFont` | Portable MeltySynth adapter. It renders into preallocated buffers and consumes MIDI on the same render thread. MeltySynth generates audio in 16-frame internal blocks, about 0.33 ms at 48 kHz. [MeltySynth source](https://github.com/sinshu/meltysynth) |
| `Windows.Audio` | Windows audio endpoints, event-driven shared-mode WASAPI, stream clock and render diagnostics. |
| `Windows.Midi` | Windows MIDI inputs and receipt timestamps. |
| `Windows.App` | WinUI device setup, SoundFont picker, monitor controls, and recording readout. |

The audio output supplies a frame-to-performance-counter anchor. The engine maps incoming MIDI timestamps to that presentation timeline for recording and plays monitoring events at the earliest writable frame. The metronome and synth share the render timeline. Offset and quantization change the saved placement. The stored note length comes from the uncompensated note-on and note-off interval.

DryWetMIDI is a candidate for a portable Standard MIDI File adapter. Its `MidiFile.Read` and `Write` APIs cover SMF, and track chunks, notes, and tempo-map APIs expose useful data for conversion. Before adopting it, verify format 0 and 1 round trips with tempo and track metadata, channels, overlapping notes, and events the editor does not yet expose. Handle PPQ timing and specify how SMPTE time divisions import before relying on the conversion. Keep the conversion at the file boundary so `Engine` owns editing and timing. [DryWetMIDI source and documentation](https://github.com/melanchall/drywetmidi)

## Matched comparison with Electron

Use packaged Electron Signal at `codex/midi-recording-playback` commit `24f54bfb`. It maps received MIDI timestamps through Web Audio output timing, applies the recording offset, and uses the played interval for duration. The reported 10 ms recording offset and click-to-piano alignment within 2.5 ms are prior results. Physical key-to-speaker latency and output gaps require separate captures. The existing `scripts/perf` runs browser playback and collects browser metrics. [Web Audio output timestamp specification](https://www.w3.org/TR/webaudio/#dom-audiocontext-getoutputtimestamp)

### Prepare both builds

- In a checkout at `24f54bfb`, use Node 24 and pnpm 10.34.5 to build the packaged baseline. Launch `signal.exe` in the generated Windows directory under `electron/out`.

  ```powershell
  pnpm install --frozen-lockfile
  npm ci --prefix electron
  pnpm run package:electron
  ```

- Use the same computer, MIDI controller, cable and port, Windows output endpoint, sample rate, and output level. Set the Windows default output and Electron's per-app output to the endpoint selected in WinUI. Disable spatial sound and use the same device enhancement settings in every run. Close the other sequencer and silence system notifications.
- Select the same MIDI channel and acoustic grand piano preset (General MIDI program 0) in both apps. Use **A320U.sf2 (Signal Factory Sound)** in Electron and `A320U.sf2` in WinUI. For a different SF2, add its directory in Electron's SoundFont settings, scan for it, and select the same file in both apps. Save the SF2 SHA-256 (`Get-FileHash` in PowerShell), device and driver versions, and app builds with the raw recordings. Set the recording offset to +10 ms and quantization off for comparisons of recorded positions.

### Key-to-sound latency

1. Record two channels through one interface or recorder at 48 kHz or higher with fixed gain and automatic effects disabled. Feed channel 1 from an isolated key contact or an optical gate positioned at a repeatable point in the key travel. Keep the marker and controller setup fixed. A contact mic on the key is a useful substitute if the report labels it as a mechanical onset proxy. Feed channel 2 from a microphone at a fixed distance from the speaker to measure key-to-speaker latency. Alternatively, route the endpoint's analog line output to channel 2 and label the result key-to-line-out. Windows software loopback captures the endpoint stream, which is useful for software gap diagnosis. [Microsoft WASAPI loopback documentation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
2. Turn off the metronome and warm up both apps with five strikes. Capture at least 100 measured strikes per app per workload. Use MIDI note 60 and consistent velocity, release the key between strikes, and vary the interval from roughly 0.5 to 1 second to sample different render phases. Alternate app order across runs. Use the same capture path and thresholds throughout.
3. Mark the first contact transition on channel 1. On channel 2, use the start of the first two consecutive 1 ms RMS windows above a threshold chosen from warm-up noise and piano attack levels. Choose the same threshold for both apps, above both noise floors and below both attacks, and log it. Pair an onset within 250 ms of each press. Mark a press with no qualifying onset as missed. Keep all valid pairs, including long delays.

### Audio dropouts under load

1. Collect one 10-minute idle and one 10-minute loaded capture per app. Reproduce the same dense repeating piano part on both, with overlapping notes to keep output above the noise floor. Replay a fixed pattern from a hardware sequencer or a second machine through MIDI if available. Check an unloaded reference capture first to identify naturally quiet passages. Capture via the same analog endpoint for both runs. A 120 BPM click can provide a separate missed-beat check when continuous note playback is unavailable.
2. For the loaded passes, use the same silent background workload for both apps. For example, start one normal-priority CPU job per two logical processors, wait for all jobs to run, then repeat the strike test and dropout capture. Log worker count and typical CPU utilization. Run this PowerShell block before the loaded captures, and press Enter after the captures finish.

   ```powershell
   $workerCount = [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount / 2))
   $workers = @(1..$workerCount | ForEach-Object {
       Start-Job -ScriptBlock {
           while ($true) {
               for ($i = 1; $i -le 10000; $i++) { $null = [Math]::Sqrt($i) }
           }
       }
   })
   try { $null = Read-Host 'Press Enter after the loaded captures finish' }
   finally { $workers | Stop-Job; $workers | Remove-Job }
   ```

3. Mark contiguous interruptions of at least 2 ms in passages where the reference remains energized. Use a fixed threshold above the recorder noise floor and check each candidate against the reference to exclude natural decay and rests. Count intervals and total missing milliseconds. Record the native counter changes for **Estimated underruns**, **Clock failures**, and **MIDI drops** alongside the capture. Report missed clicks separately when using the click-only method. If the smallest WinUI period has gaps, repeat the loaded pass at the next period and report both runs with their settings.

### Summarize the captures

Save onset markers and marked gaps in JSON, in milliseconds from each capture's start. Use one file per period setting and capture method. The values below illustrate the input format. Use `soundMs: null` for a missed note, `dropouts: null` for a capture that was not scored for gaps, and `dropouts: []` for a scored capture with zero gaps. Trim each dropout capture to the observation window before marking it.

```json
{
  "runs": [
    {
      "app": "WinUI",
      "load": "idle",
      "durationMs": 2200,
      "strikes": [{ "pressMs": 430.0, "soundMs": 445.2 }],
      "dropouts": null
    },
    {
      "app": "WinUI",
      "load": "idle",
      "durationMs": 600000,
      "strikes": [],
      "dropouts": [{ "startMs": 11130.7, "endMs": 11133.8 }]
    }
  ]
}
```

Add entries for `Electron` and `WinUI` under both `idle` and `loaded`, then run the summarizer using PowerShell 7. It reports median and nearest-rank p95 key-to-sound time for successful strikes, missed notes, observed minutes, and dropout count, duration, and rate. Archive the unprocessed WAV files and marker file with the report.

```powershell
./native/tools/Measure-LatencyComparison.ps1 -CapturePath ./captures.json | Format-Table -AutoSize
```
