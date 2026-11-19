import { afterEach, beforeEach, expect, it, vi } from "vitest"
import { MIDIDeviceStore } from "./MIDIDeviceStore"

const storage = new Map<string, string>()

beforeEach(() => {
  storage.clear()
  vi.stubGlobal("window", {
    localStorage: {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value),
    },
  })
})

afterEach(() => vi.unstubAllGlobals())

it("defaults the recording offset to zero for older saved settings", () => {
  storage.set(
    "MIDIDeviceStore",
    JSON.stringify({ midiInputRouting: "channelRouting" }),
  )

  const store = new MIDIDeviceStore()
  expect(store.recordingOffsetMs).toBe(0)
  expect(store.midiInputRouting).toBe("channelRouting")
})

it("restores signed recording offsets and ignores invalid edits", () => {
  const store = new MIDIDeviceStore()
  store.setRecordingOffsetMs(-37.5)
  store.setRecordingOffsetMs(Number.NaN)
  expect(new MIDIDeviceStore().recordingOffsetMs).toBe(-37.5)

  store.setRecordingOffsetMs(125)
  expect(new MIDIDeviceStore().recordingOffsetMs).toBe(125)
})

it("keeps older recording offsets when enabling recording quantization", () => {
  storage.set("MIDIDeviceStore", JSON.stringify({ recordingOffsetMs: 10 }))

  const store = new MIDIDeviceStore()
  expect(store.quantizeWhileRecording).toBe(false)

  store.setQuantizeWhileRecording(true)
  const restoredStore = new MIDIDeviceStore()
  expect(restoredStore.quantizeWhileRecording).toBe(true)
  expect(restoredStore.recordingOffsetMs).toBe(10)

  restoredStore.setQuantizeWhileRecording(false)
  expect(new MIDIDeviceStore().quantizeWhileRecording).toBe(false)
})
