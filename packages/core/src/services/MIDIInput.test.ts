import { expect, it, vi } from "vitest"
import { type MIDIInputEvent, MIDIInput as MIDIInputService } from "./MIDIInput"

const noteOn = Uint8Array.of(0x90, 60, 100)

it("preserves the Web MIDI timestamp instead of the callback time", () => {
  const input = { id: "keyboard", onmidimessage: null } as MIDIInput
  const access = { inputs: new Map([[input.id, input]]) }
  const service = new MIDIInputService({ enabledInputs: { [input.id]: true } })
  const listener = vi.fn()
  service.on("midiMessage", listener)
  service.connect(access)

  input.onmidimessage?.({ data: noteOn, timeStamp: 123 } as MIDIMessageEvent)

  expect(listener).toHaveBeenCalledWith(
    expect.objectContaining({ timeStamp: 123 }),
  )
})

it("timestamps messages without a host timestamp on the performance clock", () => {
  const service = new MIDIInputService({ enabledInputs: {} })
  const listener = vi.fn<(event: MIDIInputEvent) => void>()
  service.on("midiMessage", listener)

  const before = performance.now()
  service.onMidiMessage({ data: noteOn })

  const [event] = listener.mock.calls[0]
  expect(event.timeStamp).toBeGreaterThanOrEqual(before)
  expect(event.timeStamp).toBeLessThanOrEqual(performance.now())
})
