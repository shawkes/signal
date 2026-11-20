# Signal Native piano roll

The WinUI 3 prototype opens and saves Standard MIDI Files, edits notes one track at a time, and plays the whole song with its tempo changes. The recording test monitors a MIDI keyboard through a SoundFont piano, plays a 120 BPM metronome, and reports recorded note placement against the beat. Each track keeps its own undo history for the current session.

## Build and run

Use x64 Windows (build 19041 or newer) and the .NET 10 SDK. Run these commands from the repository root.

```powershell
dotnet build native/Signal.Native.slnx -c Release
dotnet test native/Signal.Native.slnx -c Release
dotnet run --project native/Windows.App -c Release
```

### Open, edit, and play a MIDI file

- **File → New** (Ctrl+N) starts an untitled song with one piano track. **File → Open MIDI…** (Ctrl+O) opens a `.mid` or `.midi` file. Both commands offer to save a modified song first. Select a track in **Tracks** to edit it. **File → Save** (Ctrl+S) writes the song and **Save as…** (Ctrl+Shift+S) writes a copy.
- Click an empty grid cell or select **Add note** to add a note. Drag from empty space to select a group, Ctrl-click to toggle individual notes, or press Ctrl+A to select the track's notes. Drag a selected note to move the group or drag its right edge to resize the group. Hold the pointer near the edge of the roll during a drag to scroll and keep editing.
- Set **Grid** to 1/4, 1/8, 1/16, 1/32, or 1/64. Left and Right nudge the selection by one grid step and Up and Down move it by one semitone. Shift+Left and Shift+Right change selected lengths by one grid step. The ruler groups beats by the song’s time signature.
- Use Ctrl+C to copy, Ctrl+X to cut, Ctrl+V to paste, and Delete to remove the selected notes. Shift-click an empty cell to set the paste position. The piano-roll clipboard also works across tracks. **Undo** or Ctrl+Z reverses a group edit in one step.
- Click the piano-roll ruler to place the playhead on the editing grid. Drag the ruler to set a loop range. **Play song** starts from the playhead using the file's tempo map. Use **Pause** to hold the position and **Resume** to continue. **Stop** returns to the ruler position where playback was cued. Edits made during playback take effect the next time you start playback.
- Finish a **Record test** take in **Recording settings** and select **Show in piano roll**. The test take appends as one undoable edit at the next available bar in the track selected when recording began.

To open a file at launch, run `dotnet run --project native/Windows.App -c Release -- "C:/Music/example.mid"`.

The adapter supports MIDI formats 0 and 1 with PPQ timing. Convert SMPTE-timed files to PPQ and choose one sequence from a format 2 file before opening it. Saving retains source track order, tick resolution, tempo events, and existing non-note events such as time signatures and program changes. Playback renders General MIDI program changes and the controllers supported by MeltySynth. SysEx is retained on save.

### Tracks and sounds

- Use **Add instrument** to assign an available melodic MIDI channel, or **Add drums** to create a percussion track on channel 10. Drum tracks show General MIDI labels such as kick, snare, and hi-hat in the piano roll.
- Select a track and choose an **Instrument** or **Drum kit** from the loaded SoundFont. The picker sets the starting bank and program for the displayed MIDI channel and keeps later program changes. Tracks on a shared channel use the same starting sound. Start playback again to hear the change.
- **Rename…** and **Remove track** operate on the selected track. **Undo track edit** reverses track and sound changes. The piano roll's **Undo** still applies to note edits in the current track. A song keeps at least one track.
- MIDI save writes the track list, names, bank/program assignments, and notes. A one-track file saves as format 0 and a song with multiple tracks saves as format 1. Removing a track that carried tempo or meter events preserves those song-wide events. The SF2 file itself stays separate from the MIDI file.

### Loop playback

- Drag across the piano-roll ruler to mark a range. The endpoints snap to beats and can be dragged to adjust the selection.
- Turn on **Loop**, then select **Play song**. All tracks repeat the highlighted range. With no selection, **Loop** repeats the whole song. **Clear range** returns to whole-song looping.
- The loop can be changed during playback. Enabling a range while the playhead is outside it jumps to the range start. Turning **Loop** off lets playback continue from the current position to the end of the song. The range stays selected for the next play.
- Turn on **Metronome** in the piano-roll transport to hear the song's beat grid during playback and recording. The transport click goes quiet when playback or recording stops. The click and notes follow the same tempo map at each loop boundary. Recording settings has a separate 120 BPM metronome for the recording and tap tests.

### Tempo changes

- The **Tempo** lane above the ruler shows the same BPM markers on every track. Click a marker to edit its BPM or remove it. Click empty space to add a point, or drag a later marker horizontally to move it. New and dragged positions snap to the piano-roll grid.
- The opening marker stays at the song start. Click any marker to access tempo undo. During playback, the next play uses your edited tempo map.
- Select **Tempo changes…** above the piano roll to see the opening BPM and every later tempo point. Opening the editor stops playback.
- Select a point, enter its BPM and position, then use **Update selected**. Positions use quarter-note beats from 1 and a tick within the beat. The dialog shows the song's tick resolution.
- Use **Add** at an unused position to create a tempo change. **Remove** deletes a selected later point. The opening point stays at beat 1, tick 0. To make the demo a constant tempo, remove its later point and set the opening BPM.
- **Apply** commits the changes for the next playback, metronome, count-in, recording, and MIDI save. **Cancel** discards the dialog's changes. Reopen the dialog and select **Undo tempo edit** to undo the previous application.

### Time signatures

- Click the **Meter** button beside the BPM. Select **Bar 1**, set **Beats per bar** and **Beat unit**, then select **Apply**. For 3/4, use 3 and 1/4. Bar 2 begins after three quarter notes.
- Choose **Add at a later bar…** for a meter change. An imported change partway through a bar starts a new bar at its original MIDI tick. The ruler, loop handles, and metronome follow these changes across all tracks. Note positions and lengths stay at their recorded ticks.
- Select a later signature and click **Remove** to delete it. **Undo time signature edit** reverses the last meter edit. Use **Save** to write the signatures to the MIDI file.

### Record in the song

With **Loop** off, select a track and press **Record** on the piano-roll transport. Recording runs from bar 1 and plays the backing tracks while you monitor the selected instrument or drum kit. Tick **Count-in** to hear one measure at the song's opening tempo and time signature before bar 1. The count-in works with the regular metronome off. The recorder keeps early attacks that land on beat one after applying the recording offset and quantization, even if you release the key during the count-in. With quantization off, attacks up to 25 ms early after compensation land at the start and keep their played length. Canceling during the count-in discards the take. With **Count-in** off, capture accepts input as soon as the engine arms the take.

Held notes appear and grow on the destination track as you play. The roll extends and follows the playhead through long takes. A held note crossing the loop boundary appears on both sides of the range. Press **Stop** to commit the take as one undoable edit and keep your place in the roll.

Choose **Quantize** in **Recording settings** to snap new note starts after compensation. It supports 1/4 through 1/64 and preserves played lengths. **Off** captures freely timed notes. A MIDI file’s tick resolution sets the finest position it can store.

### MIDI input routing

- **Selected track** is the default in the Tracks panel. A controller on any MIDI channel plays the selected track's instrument or drum kit while idle, during playback, and during recording. Selecting another track releases held notes and sends new notes to the new track. During a take, held notes finish on the track where they started.
- **Multi** routes each input channel to its matching track. This also records multiple channels into their own tracks in a single take. If several tracks use a channel, the first track with that primary channel takes precedence. Unassigned channels still appear in **View → MIDI monitor…**. Each destination track has its own undo for the take.

### Loop overdubbing

- Select the destination track and drag on the piano-roll ruler to set the loop. Turn on **Loop** and press **Record**. With no range selected, **Record** loops the whole song. The range stays fixed for the take.
- With **Count-in** on, one measure of clicks plays at the tempo and time signature in effect at the loop start. The transport then shows the pass number. The regular **Metronome** follows the song’s tempo and time signatures through each repeat.
- Play over the backing tracks. Released notes join a later pass and repeat with the backing. If an offset moves a note across a loop boundary, its start keeps its distance from that boundary on the destination pass. If the placed note extends past the end of the range, it continues at the beginning. Live replay covers the first 2,048 captured note segments.
- Press **Stop** to add all passes to the selected track. One **Undo** removes the whole overdub. Use regular MIDI save to keep the result.

### Test and calibrate recording

1. In **Recording settings**, choose **Audio output** and **MIDI input**. The app remembers your selected MIDI input on the next launch. If the keyboard was disconnected, plug it in and choose **Refresh devices**. **Play test note** exercises the monitor with synthetic MIDI if a keyboard is unavailable.
2. Use **Load SoundFont…** to select an SF2. The app starts with the same `A320U.sf2` General MIDI bank as packaged Electron Signal. [A320U license](../electron/assets/soundfonts/A320U-license.txt)
3. In **Recording offset calibration**, choose the MIDI controller you plan to record with or select **Space key** for a computer-keyboard trial. Start the tap test and tap once per click. The first two taps are practice. The test finishes after 12 measured taps, or you can stop it early after at least six. **Apply offset** sets **Recording offset (ms)** to the median signed tap error. Applied suggestions and manual changes to **Recording offset (ms)** are saved for future launches. The result includes your playing and device timing.
4. Select **Record test**, play a few notes, then select **Stop recording**. **Estimated monitoring latency** gives the median delay from MIDI receipt to the first synth frame scheduled for output on the audio clock. With multiple notes, the result also gives the 95th percentile. You can play freely for this measurement. To get a **Suggested recording offset**, keep the metronome on and play one note per click for at least six clicks. **Apply suggested offset** sets the compensation for the next take. For example, consistently arriving 20 ms late suggests a +20 ms offset, which places future notes 20 ms earlier than their input timestamps. The take summary reports beat placement before and after compensation, before grid snapping. In the detail rows, negative values mean early and positive values mean late. Quantization snaps the start tick and preserves the played length.
5. Note the actual sample rate, engine period, and buffer capacity reported for the output device. Start with **Minimum**. If the output exhibits gaps on this endpoint under the target workload, repeat with **Conservative** and then **Default** until the run is clean. Record the actual values for each comparison. Frames divided by sample rate give the corresponding duration in seconds. The period is the render cadence and the capacity is the endpoint's allocated buffer size. [Microsoft low-latency audio documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)

### Monitor MIDI input

Open **View → MIDI monitor…** after selecting an input in **Recording settings**. The window shows the newest short MIDI messages first, with elapsed time, original MIDI channel, raw bytes, and a description. Select **Show Active Sensing** to include the controller's `FE` messages. Use **Pause** to inspect a burst and **Clear** to reset the list and elapsed time. Messages appear before track-channel routing and recording compensation.

### Workspace verification

An eight-second WinUI run recorded all 16 synthetic notes while scrolling across three pages with a 1/64 edit grid. The p95 interval between playhead updates decreased from 47.3 ms to 16.7 ms. A second WinUI run blocked the dispatcher for 2.1 seconds while a background thread injected MIDI and rendered audio. All 2,250 notes played entirely during the freeze and the two notes outside it reached the document with their expected starts and durations. A ten-minute timing test at 120 BPM and −112 ms also checks 1,200 unquantized notes against a simulated endpoint running 100 parts per million slower than its nominal sample rate.

Two 64-second runs on the default WASAPI endpoint used a separate thread to issue 128 synthetic quarter notes per run against a monotonic clock. The endpoint ran at 48 kHz with a 480-frame period and a 1,056-frame buffer. Both runs captured all 128 notes and reported zero estimated underruns and clock failures. The p95 absolute errors against the intended beat schedule were 12.50 ms and 1.04 ms. This includes variation in when the input thread woke. In the first run, recorded receipt timing agreed with the rendered click timing to within 0.71 ms throughout. The physical comparison procedure below uses a controller and an external audio capture.

A separate warm WASAPI run rendered A320U, live notes, backing notes, and metronome clicks across tempo changes and loop boundaries. Over 450 callbacks (4.5 seconds), the render thread allocated zero managed bytes and the endpoint reported zero estimated underruns. A parallel test-suite run intermittently reported a small allocation in an overdub test. Its source remains unverified.

All 57 Release tests passed. On the Remote Audio endpoint, a 3/4 MIDI at 120 BPM produced clicks at 0, 500, and 1,000 ms, then started backing playback and capture at bar 1 after 1.5 seconds. A MIDI note held across the boundary from the count-in was excluded. The tap estimator recovered a known +12 ms offset from synthetic MIDI against the audio clock.

All 45 Release tests passed after adding track controls. In the WinUI demo, adding, changing the sound of, removing, and undoing a temporary track restored its preset and the clean document state. A separate MIDI round trip preserved the tempo changes and played piano, organ, and the Electronic kit through WASAPI with zero estimated underruns or clock failures.

All 39 Release tests passed. On the Remote Audio endpoint, a two-track MIDI passage repeated across a 120-to-90 BPM tempo change. All ten measured metronome onsets landed within one sample of their expected positions over three wraps. Both tracks rendered through A320U. Turning Loop off mid-pass let the song finish, with zero estimated underruns and clock failures.

The Release build opened a two-track MIDI file at 101 PPQ with a tempo change. All 29 automated tests passed, including format 0 and 1 round trips and sample scheduling across tempo changes. A separate round-trip playback probe rendered piano and bass through WASAPI with zero estimated underruns and clock failures.

The available **Remote Audio** endpoint negotiated 44,100 Hz stereo with event-driven `IAudioClient3` shared mode. The actual period was **441 frames (10.00 ms)** and the buffer capacity was **970 frames (22.00 ms)**. All three period settings negotiated the same values.

- The WinUI app loaded a second SF2 through the picker and switched back to A320U. A synthetic C4 in the latency test showed +10.02 ms before and +0.02 ms after compensation with the default +10 ms offset.
- A piano-roll playback probe captured two synthetic notes, exercised the edits, and played three edited notes through A320U and the Remote Audio endpoint. It produced nonzero PCM, completed naturally, and stopped early on command in a second pass. The probe recorded zero estimated underruns and clock failures.
- A headless run through A320U, MeltySynth, and WASAPI captured all 80 synthetic notes per pass with a +10 ms offset and a 1/16 grid. Both idle and loaded passes produced piano and metronome output with zero estimated underruns or clock failures and zero managed allocations on the audio thread after warmup. The loaded pass used eight busy CPU workers on 16 logical CPUs. A separate injected 90 ms render stall produced one estimated underrun, then the stream recovered.
- An offline PCM check scheduled C4 at velocity 100 exactly on the click. The piano first crossed a 0.005 absolute sample-amplitude threshold 2.00 ms after the click at 44.1 kHz and 2.02 ms after it at 48 kHz. These values include the SoundFont attack and MeltySynth's internal render block.

Windows reported zero MIDI inputs. Physical key-to-sound median, p95, and comparative dropouts remain unmeasured.

## Project boundaries

| Folder | Responsibility |
| --- | --- |
| `Engine` | Portable `net10.0` song model, tempo map, tick and sample conversions, timestamp mapping, scheduling, and recording. |
| `Engine.Tests` | Synthetic clock, offset, quantization, and scheduling checks. |
| `MidiFiles` | Portable Standard MIDI File adapter using DryWetMIDI's managed package. |
| `MidiFiles.Tests` | Round-trip and MIDI format coverage. |
| `SoundFont` | Portable MeltySynth adapter. It renders into preallocated buffers and consumes MIDI on the same render thread. MeltySynth generates audio in 16-frame internal blocks, about 0.33 ms at 48 kHz. [MeltySynth source](https://github.com/sinshu/meltysynth) |
| `Windows.Audio` | Windows audio endpoints, event-driven shared-mode WASAPI, stream clock and render diagnostics. |
| `Windows.Midi` | Windows MIDI inputs and receipt timestamps. |
| `Windows.App` | WinUI piano roll, MIDI and SoundFont pickers, device setup, and recording readout. |

WinMM timestamps a controller message in its device callback. The WASAPI render thread maps that timestamp to a sample frame using the endpoint clock. Monitoring uses the earliest writable frame, and the metronome and synth share that timeline. Offset and quantization change the saved placement. The stored note length comes from the uncompensated note-on and note-off interval. A recording worker collects completed notes even while the window is frozen or minimized. WinUI samples the recording playhead at the current presentation time, so a delayed redraw catches up to the endpoint. The optional spacebar tap test receives computer-keyboard events through WinUI.

The adapter uses DryWetMIDI to read and write the source event stream. It carries original track and event data across edits and replaces the destination only after writing the full file. The engine owns the portable song model, note editing, and scheduling. [DryWetMIDI source and documentation](https://github.com/melanchall/drywetmidi)

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
