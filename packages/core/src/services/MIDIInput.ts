import { type AnyEvent, deserializeSingleEvent, Stream } from "midifile-ts"
import type { MIDIDeviceStore } from "../stores/MIDIDeviceStore"

export interface MIDIInputEvent {
  message: AnyEvent
  // Milliseconds in the same time origin as performance.now().
  timeStamp: number
}

interface MIDIMessageEvent {
  data: Uint8Array
  timeStamp?: number
}

export class MIDIInput {
  private listeners: ((e: MIDIInputEvent) => void)[] = []

  constructor(
    private readonly midiDeviceStore: Pick<MIDIDeviceStore, "enabledInputs">,
  ) {}

  connect(midiAccess: Pick<MIDIAccess, "inputs">) {
    for (const input of midiAccess.inputs.values()) {
      input.onmidimessage = (event) => {
        if (this.midiDeviceStore.enabledInputs[input.id]) {
          const data = event.data
          if (data !== null) {
            this.onMidiMessage?.({ data, timeStamp: event.timeStamp })
          }
        }
      }
    }
  }

  readonly onMidiMessage = (e: MIDIMessageEvent) => {
    const timeStamp = e.timeStamp ?? performance.now()
    const stream = new Stream(e.data)
    const message = deserializeSingleEvent(stream)
    const event = { data: e.data, message, timeStamp }
    this.listeners.forEach((callback) => callback(event))
  }

  on(event: "midiMessage", callback: (e: MIDIInputEvent) => void) {
    this.listeners.push(callback)
    return () => {
      this.off(event, callback)
    }
  }

  off(_event: "midiMessage", callback: (e: MIDIInputEvent) => void) {
    this.listeners = this.listeners.filter((cb) => cb !== callback)
  }
}
