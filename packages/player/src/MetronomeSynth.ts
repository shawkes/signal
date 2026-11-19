import {
  DEFAULT_PERCUSSION,
  SPESSA_BUFSIZE,
  SpessaSynthProcessor,
} from "spessasynth_core"
import { getScheduledAudioTime } from "./getScheduledAudioTime.js"
import type { SoundFont } from "./SoundFont.js"
import type { SendableEvent, SynthOutput } from "./SynthOutput.js"

export const METRONOME_ACCENT = { noteNumber: 76, velocity: 100 } as const
export const METRONOME_BEAT = { noteNumber: 77, velocity: 70 } as const

const CLICK_DURATION_SECONDS = 2

export class MetronomeSynth implements SynthOutput {
  private clicks: Map<number, AudioBuffer> | null = null

  constructor(private readonly context: AudioContext) {}

  get isLoaded(): boolean {
    return this.clicks !== null
  }

  loadSoundFont(soundFont: SoundFont): void {
    this.clicks = new Map(
      [METRONOME_ACCENT, METRONOME_BEAT].map(({ noteNumber, velocity }) => [
        noteNumber,
        this.renderClick(soundFont, noteNumber, velocity),
      ]),
    )
  }

  private renderClick(
    soundFont: SoundFont,
    noteNumber: number,
    velocity: number,
  ): AudioBuffer {
    const { sampleRate } = this.context
    const length = Math.ceil(sampleRate * CLICK_DURATION_SECONDS)
    const buffer = this.context.createBuffer(2, length, sampleRate)
    const left = buffer.getChannelData(0)
    const right = buffer.getChannelData(1)

    // Render the original SoundFont click and its effects tail once at startup.
    const synth = new SpessaSynthProcessor(sampleRate)
    synth.soundBankManager.addSoundBank(soundFont.parsed, "metronome")
    synth.noteOn(DEFAULT_PERCUSSION, noteNumber, velocity)

    for (let offset = 0; offset < length; offset += SPESSA_BUFSIZE) {
      synth.process(
        left,
        right,
        offset,
        Math.min(SPESSA_BUFSIZE, length - offset),
      )
    }

    return buffer
  }

  activate(): void {
    this.context.resume()
  }

  sendEvent(
    event: SendableEvent,
    delayTime: number,
    timestampNow: number,
  ): void {
    if (
      event.type !== "channel" ||
      event.subtype !== "noteOn" ||
      event.velocity === 0
    ) {
      return
    }
    const buffer = this.clicks?.get(event.noteNumber)
    if (buffer === undefined) {
      return
    }

    const startTime = getScheduledAudioTime(
      this.context,
      delayTime,
      timestampNow,
    )
    const source = this.context.createBufferSource()
    source.buffer = buffer
    source.connect(this.context.destination)
    source.onended = () => source.disconnect()
    source.start(startTime)
  }
}
