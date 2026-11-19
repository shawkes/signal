import {
  type MIDIDeviceStore,
  type MIDIInput,
  type NoteEvent,
  type SongStore,
  type Track,
  type TrackEvent,
  type TrackId,
  UNASSIGNED_TRACK_ID,
} from "@signal-app/core"
import { type Observable, ObservableValue } from "@signal-app/observable"
import type { Player } from "@signal-app/player"

type HeldNote = Pick<NoteEvent, "id" | "noteNumber"> & {
  playedTick: number
}

export class MIDIRecorder {
  trackId: TrackId = UNASSIGNED_TRACK_ID
  private stopRecording: (() => void) | null = null
  private readonly _isRecording = new ObservableValue(false)

  readonly onIsRecordingChanged: Observable = this._isRecording.onChanged

  constructor(
    private readonly songStore: Pick<SongStore, "song">,
    private readonly player: Pick<
      Player,
      "getTickAtTime" | "onPositionChanged"
    >,
    private readonly midiDeviceStore: Pick<
      MIDIDeviceStore,
      "midiInputRouting" | "recordingOffsetMs"
    >,
    private readonly midiInput: Pick<MIDIInput, "on">,
    private readonly toRenderTime: (outputTime: number) => number,
  ) {}

  get isRecording() {
    return this._isRecording.value
  }

  start(quantizeTick?: (tick: number) => number) {
    this.stop()

    const { recordingOffsetMs } = this.midiDeviceStore
    const getTick = (outputTime: number) =>
      Math.floor(
        this.player.getTickAtTime(
          this.toRenderTime(outputTime) - recordingOffsetMs,
        ),
      )

    const recordedNotes: { [key: TrackId]: HeldNote[] } = {}

    const unsubscribeMidiInput = this.midiInput.on("midiMessage", (e) => {
      if (!this.isRecording) {
        return
      }

      const message = e.message

      if (message.type !== "channel") {
        return
      }

      const tick = getTick(e.timeStamp)

      const routing = this.midiDeviceStore.midiInputRouting

      let tracks: Track[]
      if (routing === "channelRouting") {
        tracks = this.songStore.song.tracks.filter(
          (t) => !t.isConductorTrack && t.channel === message.channel,
        )
      } else {
        // selectedTrack mode
        const track = this.songStore.song.getTrack(this.trackId)
        tracks = track !== undefined ? [track] : []
      }

      switch (message.subtype) {
        case "noteOn": {
          const startTick = quantizeTick?.(tick) ?? tick
          tracks.forEach((track) => {
            const note = track.addEvent<NoteEvent>({
              type: "channel",
              subtype: "note",
              noteNumber: message.noteNumber,
              tick: startTick,
              velocity: message.velocity,
              duration: 0,
              isRecording: true,
            })
            if (recordedNotes[track.id] === undefined) {
              recordedNotes[track.id] = []
            }
            recordedNotes[track.id].push({
              id: note.id,
              noteNumber: note.noteNumber,
              playedTick: tick,
            })
          })
          break
        }
        case "noteOff": {
          tracks.forEach((track) => {
            const recordedNotesForTrack = recordedNotes[track.id] ?? []

            recordedNotesForTrack
              .filter((n) => n.noteNumber === message.noteNumber)
              .forEach((n) => {
                track.updateEvent<NoteEvent>(n.id, {
                  duration: Math.max(0, tick - n.playedTick),
                })
              })

            recordedNotes[track.id] = recordedNotesForTrack.filter(
              (n) => n.noteNumber !== message.noteNumber,
            )
          })
          break
        }
        default: {
          tracks.forEach((track) => {
            track.addEvent({ ...message, tick, isRecording: true })
          })
          break
        }
      }
    })

    // extend duration while key press
    const unsubscribePlayer = this.player.onPositionChanged.subscribe(() => {
      const tick = getTick(performance.now())

      Object.entries(recordedNotes).forEach(([trackId, notes]) => {
        const track = this.songStore.song.getTrack(
          parseInt(trackId, 10) as TrackId,
        )
        if (track === undefined) {
          return
        }
        notes.forEach((n) => {
          track.updateEvent<NoteEvent>(n.id, {
            duration: Math.max(0, tick - n.playedTick),
          })
        })
      })
    })

    this.stopRecording = () => {
      unsubscribeMidiInput()
      unsubscribePlayer()

      // stop recording
      this.songStore.song.tracks.forEach((track) => {
        const events = track.events
          .filter((e) => e.isRecording === true)
          .map<Partial<TrackEvent>>((e) => ({ ...e, isRecording: false }))
        track.updateEvents(events)
      })
    }

    this._isRecording.set(true)
  }

  stop() {
    this.stopRecording?.()
    this.stopRecording = null
    this._isRecording.set(false)
  }
}
