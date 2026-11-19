import { type Observable, ObservableValue } from "@signal-app/observable"

const STORAGE_KEY = "MIDIDeviceStore"
export const MAX_MIDI_RECORDING_OFFSET_MS = 1000

type MIDIInputRouting = "selectedTrack" | "channelRouting"
type PersistedState = {
  enabledOutputs: Record<string, boolean>
  enabledInputs: Record<string, boolean>
  isFactorySoundEnabled: boolean
  midiInputRouting: MIDIInputRouting
  recordingOffsetMs: number
  quantizeWhileRecording: boolean
}

export class MIDIDeviceStore {
  private readonly _enabledOutputs = new ObservableValue<
    Record<string, boolean>
  >({})
  private readonly _enabledInputs = new ObservableValue<
    Record<string, boolean>
  >({})
  private readonly _isFactorySoundEnabled = new ObservableValue<boolean>(true)
  private readonly _midiInputRouting = new ObservableValue<MIDIInputRouting>(
    "selectedTrack",
  )
  private readonly _recordingOffsetMs = new ObservableValue(0)
  private readonly _quantizeWhileRecording = new ObservableValue(false)

  constructor() {
    const persistedState = loadPersistedState()
    this._enabledOutputs.set(persistedState.enabledOutputs)
    this._enabledInputs.set(persistedState.enabledInputs)
    this._isFactorySoundEnabled.set(persistedState.isFactorySoundEnabled)
    this._midiInputRouting.set(persistedState.midiInputRouting)
    this.setRecordingOffsetMs(persistedState.recordingOffsetMs)
    this._quantizeWhileRecording.set(persistedState.quantizeWhileRecording)

    ;[
      this._enabledOutputs.onChanged,
      this._enabledInputs.onChanged,
      this._isFactorySoundEnabled.onChanged,
      this._midiInputRouting.onChanged,
      this._recordingOffsetMs.onChanged,
      this._quantizeWhileRecording.onChanged,
    ].forEach((observable) => {
      observable.subscribe(() => {
        this.persistState()
      })
    })
  }

  get enabledOutputs(): Record<string, boolean> {
    return this._enabledOutputs.value
  }

  private set enabledOutputs(value: Record<string, boolean>) {
    this._enabledOutputs.set(value)
  }

  get onEnabledOutputsChanged(): Observable {
    return this._enabledOutputs.onChanged
  }

  get enabledInputs(): Record<string, boolean> {
    return this._enabledInputs.value
  }

  private set enabledInputs(value: Record<string, boolean>) {
    this._enabledInputs.set(value)
  }

  get onEnabledInputsChanged(): Observable {
    return this._enabledInputs.onChanged
  }

  get isFactorySoundEnabled(): boolean {
    return this._isFactorySoundEnabled.value
  }

  set isFactorySoundEnabled(value: boolean) {
    this._isFactorySoundEnabled.set(value)
  }

  get onIsFactorySoundEnabledChanged(): Observable {
    return this._isFactorySoundEnabled.onChanged
  }

  get midiInputRouting(): MIDIInputRouting {
    return this._midiInputRouting.value
  }

  private set midiInputRouting(value: MIDIInputRouting) {
    this._midiInputRouting.set(value)
  }

  get onMidiInputRoutingChanged(): Observable {
    return this._midiInputRouting.onChanged
  }

  get recordingOffsetMs(): number {
    return this._recordingOffsetMs.value
  }

  get onRecordingOffsetMsChanged(): Observable {
    return this._recordingOffsetMs.onChanged
  }

  get quantizeWhileRecording(): boolean {
    return this._quantizeWhileRecording.value
  }

  get onQuantizeWhileRecordingChanged(): Observable {
    return this._quantizeWhileRecording.onChanged
  }

  private persistState() {
    savePersistedState({
      enabledOutputs: this.enabledOutputs,
      enabledInputs: this.enabledInputs,
      isFactorySoundEnabled: this.isFactorySoundEnabled,
      midiInputRouting: this.midiInputRouting,
      recordingOffsetMs: this.recordingOffsetMs,
      quantizeWhileRecording: this.quantizeWhileRecording,
    })
  }

  setInputEnable = (deviceId: string, enabled: boolean) => {
    this.enabledInputs = {
      ...this.enabledInputs,
      [deviceId]: enabled,
    }
  }

  setOutputEnable = (deviceId: string, enabled: boolean) => {
    this.enabledOutputs = {
      ...this.enabledOutputs,
      [deviceId]: enabled,
    }
  }

  setMidiInputRouting = (routing: "selectedTrack" | "channelRouting") => {
    this.midiInputRouting = routing
  }

  setRecordingOffsetMs = (offset: number) => {
    if (Number.isFinite(offset)) {
      this._recordingOffsetMs.set(
        Math.max(
          -MAX_MIDI_RECORDING_OFFSET_MS,
          Math.min(MAX_MIDI_RECORDING_OFFSET_MS, offset),
        ),
      )
    }
  }

  setQuantizeWhileRecording = (enabled: boolean) => {
    this._quantizeWhileRecording.set(enabled)
  }
}

function savePersistedState(state: PersistedState) {
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(state))
}

function loadPersistedState(): PersistedState {
  const defaultState: PersistedState = {
    enabledOutputs: {},
    enabledInputs: {},
    isFactorySoundEnabled: true,
    midiInputRouting: "selectedTrack",
    recordingOffsetMs: 0,
    quantizeWhileRecording: false,
  }

  try {
    const json = window.localStorage.getItem(STORAGE_KEY)
    if (json === null) {
      return defaultState
    }
    const value = JSON.parse(json) as Partial<PersistedState>
    return {
      enabledOutputs: value.enabledOutputs ?? defaultState.enabledOutputs,
      enabledInputs: value.enabledInputs ?? defaultState.enabledInputs,
      isFactorySoundEnabled:
        value.isFactorySoundEnabled ?? defaultState.isFactorySoundEnabled,
      midiInputRouting: value.midiInputRouting ?? defaultState.midiInputRouting,
      recordingOffsetMs:
        value.recordingOffsetMs ?? defaultState.recordingOffsetMs,
      quantizeWhileRecording: value.quantizeWhileRecording === true,
    }
  } catch {
    return defaultState
  }
}
