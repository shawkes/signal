import { BasicSoundBank } from "spessasynth_core"
import { expect, it, vi } from "vitest"
import { SoundFont } from "./SoundFont.js"
import { SoundFontSynth } from "./SoundFontSynth.js"
import type { SendableEvent } from "./SynthOutput.js"

const { noteOn } = vi.hoisted(() => ({ noteOn: vi.fn() }))

vi.mock("spessasynth_lib", () => ({
  WorkletSynthesizer: class {
    readonly noteOn = noteOn
    readonly soundBankManager = { addSoundBank: vi.fn() }
    readonly isReady = Promise.resolve()
    readonly connect = vi.fn()

    constructor(private readonly context: AudioContext) {}

    get currentTime() {
      return this.context.currentTime
    }
  },
}))

it("uses the same audio clock for notes and clicks in a scheduling batch", async () => {
  let contextTime = 1
  const context = {
    get currentTime() {
      return contextTime
    },
  } as AudioContext
  const synth = new SoundFontSynth(context)
  const metronome = new SoundFontSynth(context)
  const soundFont = new SoundFont(new ArrayBuffer(0), new BasicSoundBank())
  await Promise.all([
    synth.loadSoundFont(soundFont),
    metronome.loadSoundFont(soundFont),
  ])

  const note: SendableEvent = {
    type: "channel",
    subtype: "noteOn",
    channel: 0,
    noteNumber: 60,
    velocity: 100,
  }
  const click = { ...note, channel: 9, noteNumber: 76 }

  synth.sendEvent(note, 0.05, 1000)
  contextTime = 1.02
  metronome.sendEvent(click, 0.05, 1000)
  metronome.sendEvent(click, 0.05, 1020)

  expect(noteOn.mock.calls).toEqual([
    [0, 60, 100, { time: 1.05 }],
    [9, 76, 100, { time: 1.05 }],
    [9, 76, 100, { time: 1.07 }],
  ])
})
