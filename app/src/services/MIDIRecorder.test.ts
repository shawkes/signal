import {
  isNoteEvent,
  type MIDIInputEvent,
  SongStore,
  type Track,
} from "@signal-app/core"
import { Emitter } from "@signal-app/observable"
import { describe, expect, it, vi } from "vitest"
import { getRenderTimeForOutputTime } from "../player/getRenderTimeForOutputTime"
import { MIDIRecorder } from "./MIDIRecorder"

type MIDIMessage = MIDIInputEvent["message"]

const noteOn = (noteNumber: number, velocity: number, channel = 0) =>
  ({
    type: "channel",
    subtype: "noteOn",
    channel,
    noteNumber,
    velocity,
    deltaTime: 0,
  }) satisfies MIDIMessage

const noteOff = (noteNumber: number, channel = 0) =>
  ({
    type: "channel",
    subtype: "noteOff",
    channel,
    noteNumber,
    velocity: 0,
    deltaTime: 0,
  }) satisfies MIDIMessage

const createRecorder = (
  toRenderTime: (outputTime: number) => number = (outputTime) => outputTime,
) => {
  const songStore = new SongStore()
  const player = {
    position: 0,
    onPositionChanged: new Emitter(),
    getTickAtTime: vi.fn((_timestamp: number): number => player.position),
  }
  const midiDeviceStore = {
    midiInputRouting: "selectedTrack" as "selectedTrack" | "channelRouting",
    recordingOffsetMs: 0,
  }
  const midiMessages = new Emitter<MIDIInputEvent>()
  const midiInput = {
    on: (_event: "midiMessage", callback: (e: MIDIInputEvent) => void) =>
      midiMessages.subscribe(callback),
  }
  const recorder = new MIDIRecorder(
    songStore,
    player,
    midiDeviceStore,
    midiInput,
    toRenderTime,
  )
  const track = songStore.song.tracks[1]
  recorder.trackId = track.id

  const send = (message: MIDIMessage, timeStamp = performance.now()) =>
    midiMessages.emit({ message, timeStamp })

  const moveTo = (tick: number) => {
    player.position = tick
    player.onPositionChanged.emit()
  }

  return { recorder, songStore, midiDeviceStore, player, track, send, moveTo }
}

const notesOf = (track: Track) => track.events.filter(isNoteEvent)
const snapToSixteenth = (tick: number) => Math.round(tick / 120) * 120

describe("MIDIRecorder", () => {
  it("is not recording initially", () => {
    const { recorder } = createRecorder()
    expect(recorder.isRecording).toBe(false)
  })

  it("notifies listeners with the updated isRecording", () => {
    const { recorder } = createRecorder()
    const values: boolean[] = []
    recorder.onIsRecordingChanged.subscribe(() => {
      values.push(recorder.isRecording)
    })

    recorder.start()
    recorder.stop()

    expect(values).toEqual([true, false])
  })

  it("does not notify when stop is called while not recording", () => {
    const { recorder } = createRecorder()
    let count = 0
    recorder.onIsRecordingChanged.subscribe(() => {
      count++
    })

    recorder.stop()

    expect(count).toBe(0)
  })

  it("records notes into the selected track", () => {
    const { recorder, track, send, moveTo } = createRecorder()

    recorder.start()
    moveTo(100)
    send(noteOn(60, 100))
    moveTo(250)
    send(noteOff(60))

    const [note] = notesOf(track)
    expect(note).toMatchObject({
      noteNumber: 60,
      tick: 100,
      velocity: 100,
      duration: 150,
      isRecording: true,
    })
  })

  it("uses each MIDI receive timestamp against the audible transport", () => {
    const now = vi.spyOn(performance, "now").mockReturnValue(1075)
    try {
      const context = {
        state: "running" as const,
        currentTime: 5.125,
        getOutputTimestamp: () => ({ contextTime: 5, performanceTime: 1000 }),
        baseLatency: 0.015,
        outputLatency: 0.005,
      }
      const { recorder, player, track, send } = createRecorder((outputTime) =>
        getRenderTimeForOutputTime(context, outputTime),
      )
      player.getTickAtTime.mockImplementation((time) => (time - 500) * 2)

      recorder.start()
      send(noteOn(60, 100), 800)
      send(noteOff(60), 950)

      expect(player.getTickAtTime.mock.calls).toEqual([[750], [900]])
      expect(notesOf(track)[0]).toMatchObject({ tick: 500, duration: 300 })
    } finally {
      now.mockRestore()
    }
  })

  it("extends the duration of held notes as the player advances", () => {
    const { recorder, track, send, moveTo } = createRecorder()

    recorder.start()
    send(noteOn(60, 100))
    moveTo(480)

    expect(notesOf(track)[0].duration).toBe(480)
  })

  it("applies the same recording offset to a note's start and release", () => {
    const { recorder, midiDeviceStore, player, track, send } = createRecorder()
    player.getTickAtTime.mockImplementation((time) => (time - 500) * 0.96)
    midiDeviceStore.recordingOffsetMs = 125

    recorder.start()
    send(noteOn(60, 100), 1125)
    midiDeviceStore.recordingOffsetMs = 0
    send(noteOff(60), 1250)

    expect(player.getTickAtTime.mock.calls).toEqual([[1000], [1125]])
    expect(notesOf(track)[0]).toMatchObject({ tick: 480, duration: 120 })
  })

  it("quantizes the compensated note start and keeps controllers unsnapped", () => {
    const { recorder, midiDeviceStore, player, track, send } = createRecorder(
      (time) => time - 20,
    )
    player.getTickAtTime.mockImplementation((time) => time)
    midiDeviceStore.recordingOffsetMs = 10
    const quantizeTick = vi.fn(snapToSixteenth)

    recorder.start(quantizeTick)
    send(noteOn(60, 100), 205)
    send(
      {
        type: "channel",
        subtype: "controller",
        channel: 0,
        controllerType: 64,
        value: 127,
        deltaTime: 0,
      },
      230,
    )
    send(noteOff(60), 251)

    expect(quantizeTick.mock.calls).toEqual([[175]])
    expect(notesOf(track)[0]).toMatchObject({ tick: 120, duration: 46 })
    expect(track.events).toContainEqual(
      expect.objectContaining({
        subtype: "controller",
        controllerType: 64,
        tick: 200,
      }),
    )
  })

  it("keeps the played duration when a held note snaps forward", () => {
    const { recorder, track, send, moveTo } = createRecorder()

    recorder.start(snapToSixteenth)
    moveTo(70)
    send(noteOn(60, 100))
    moveTo(100)

    expect(notesOf(track)[0]).toMatchObject({ tick: 120, duration: 30 })

    moveTo(105)
    send(noteOff(60))

    expect(notesOf(track)[0]).toMatchObject({ tick: 120, duration: 35 })
  })

  it("clears the isRecording flag of recorded events on stop", () => {
    const { recorder, track, send } = createRecorder()

    recorder.start()
    send(noteOn(60, 100))
    recorder.stop()

    expect(notesOf(track)[0].isRecording).toBe(false)
  })

  it("ignores MIDI input after stop", () => {
    const { recorder, track, send } = createRecorder()

    recorder.start()
    recorder.stop()
    send(noteOn(60, 100))

    expect(notesOf(track)).toHaveLength(0)
  })

  it("records into tracks matching the channel with channelRouting", () => {
    const { recorder, songStore, midiDeviceStore, track, send } =
      createRecorder()
    songStore.song.addNewTrack()
    const otherTrack = songStore.song.tracks[2]
    otherTrack.channel = 1
    midiDeviceStore.midiInputRouting = "channelRouting"

    recorder.start()
    send(noteOn(60, 100, 1))

    expect(notesOf(track)).toHaveLength(0)
    expect(notesOf(otherTrack)).toHaveLength(1)
  })
})
