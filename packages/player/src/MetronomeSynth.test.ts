import { readFileSync } from "node:fs"
import { expect, it, vi } from "vitest"
import {
  METRONOME_ACCENT,
  METRONOME_BEAT,
  MetronomeSynth,
} from "./MetronomeSynth.js"
import { SoundFont } from "./SoundFont.js"
import type { SendableEvent } from "./SynthOutput.js"

it("renders and reuses the bundled click sounds on the shared audio clock", async () => {
  const createBuffer = vi.fn((channels: number, length: number) => {
    const data = Array.from(
      { length: channels },
      () => new Float32Array(length),
    )
    return { getChannelData: (channel: number) => data[channel] }
  })
  const createBufferSource = vi.fn(() => ({
    buffer: null as AudioBuffer | null,
    connect: vi.fn(),
    disconnect: vi.fn(),
    onended: null as (() => void) | null,
    start: vi.fn(),
  }))
  const resume = vi.fn()
  let currentTime = 1
  const context = {
    createBuffer,
    createBufferSource,
    resume,
    sampleRate: 48000,
    destination: {},
    get currentTime() {
      return currentTime
    },
  } as unknown as AudioContext
  const synth = new MetronomeSynth(context)
  const click: SendableEvent = {
    type: "channel",
    subtype: "noteOn",
    channel: 9,
    ...METRONOME_ACCENT,
  }
  synth.sendEvent(click, 0.25, 1000)
  expect(createBufferSource).not.toHaveBeenCalled()

  const data = readFileSync(
    new URL(
      "../../../electron/assets/soundfonts/A320U_drums.sf2",
      import.meta.url,
    ),
  )
  const soundFont = await SoundFont.load(Uint8Array.from(data).buffer)
  synth.loadSoundFont(soundFont)
  expect(synth.isLoaded).toBe(true)
  expect(createBuffer.mock.calls).toEqual([
    [2, 96000, 48000],
    [2, 96000, 48000],
  ])

  synth.activate()
  expect(resume).toHaveBeenCalledOnce()

  synth.sendEvent(click, 0.25, 1000)
  currentTime = 1.125
  synth.sendEvent({ ...click, ...METRONOME_BEAT }, 0.25, 1000)
  synth.sendEvent(click, 0.25, 1125)

  const [accent, beat, nextAccent] = createBufferSource.mock.results.map(
    ({ value }) => value,
  )
  expect(accent.buffer).toBe(nextAccent.buffer)
  expect(accent.buffer).not.toBe(beat.buffer)
  expect(
    accent.buffer?.getChannelData(0).some((value: number) => value !== 0),
  ).toBe(true)
  expect(
    beat.buffer?.getChannelData(1).some((value: number) => value !== 0),
  ).toBe(true)
  expect(accent.start).toHaveBeenCalledWith(1.25)
  expect(beat.start).toHaveBeenCalledWith(1.25)
  expect(nextAccent.start).toHaveBeenCalledWith(1.375)

  accent.onended?.()
  expect(accent.disconnect).toHaveBeenCalledOnce()
})
