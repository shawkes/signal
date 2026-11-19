import styled from "@emotion/styled"
import { MAX_MIDI_RECORDING_OFFSET_MS } from "@signal-app/core"
import {
  Alert,
  Checkbox,
  CircularProgress,
  DialogContent,
  DialogSectionTitle,
  Label,
  RadioButton,
  TextField,
} from "@signal-app/ui"
import BluetoothIcon from "mdi-react/BluetoothIcon"
import {
  type FC,
  useCallback,
  useId,
  useMemo,
  useState,
  useSyncExternalStore,
} from "react"
import { useStores } from "../../../hooks/useStores"
import { Localized } from "../../../localize/useLocalization"
import { useBLEMIDIDevice } from "../hooks/useBLEMIDIDevice"
import { type Device, useMIDIDevice } from "../hooks/useMIDIDevice"

interface ListItem {
  device: Device
  onCheck: (isChecked: boolean) => void
}

const DeviceRow: FC<ListItem> = ({ device, onCheck }) => {
  return (
    <Label
      style={{
        display: "flex",
        alignItems: "center",
        marginBottom: "1rem",
      }}
    >
      <Checkbox
        checked={device.isEnabled}
        onCheckedChange={(state) => onCheck(state === true)}
        style={{ marginRight: "0.5rem" }}
        label={
          <>
            {device.name}
            {!device.isConnected && " (disconnected)"}
          </>
        }
      />
    </Label>
  )
}

const DeviceList = styled.div``

const Notice = styled.div`
  color: var(--color-text-secondary);
`

const RecordingSettingsGroup = styled.div`
  margin-top: 2rem;
`

const OffsetInput = styled(TextField)`
  width: 7rem;
  height: 2rem;
  padding: 0 0.5rem;
`

const SettingHint = styled(Notice)`
  margin-top: 0.5rem;
  font-size: 0.8rem;
`

const RecordingSettings: FC = () => {
  const { midiDeviceStore } = useStores()
  const offset = useSyncExternalStore(
    midiDeviceStore.onRecordingOffsetMsChanged.subscribe,
    useCallback(() => midiDeviceStore.recordingOffsetMs, [midiDeviceStore]),
  )
  const quantizeWhileRecording = useSyncExternalStore(
    midiDeviceStore.onQuantizeWhileRecordingChanged.subscribe,
    useCallback(
      () => midiDeviceStore.quantizeWhileRecording,
      [midiDeviceStore],
    ),
  )
  const [draft, setDraft] = useState<string | null>(null)
  const offsetHintId = useId()
  const quantizeHintId = useId()

  return (
    <RecordingSettingsGroup>
      <Label>
        <Localized name="midi-recording-offset" />
        <OffsetInput
          aria-describedby={offsetHintId}
          type="number"
          min={-MAX_MIDI_RECORDING_OFFSET_MS}
          max={MAX_MIDI_RECORDING_OFFSET_MS}
          step="any"
          value={draft ?? offset}
          onChange={({ currentTarget }) => {
            setDraft(currentTarget.value)
            midiDeviceStore.setRecordingOffsetMs(currentTarget.valueAsNumber)
          }}
          onBlur={() => setDraft(null)}
        />
      </Label>
      <SettingHint id={offsetHintId}>
        <Localized name="midi-recording-offset-description" />
      </SettingHint>
      <Checkbox
        aria-describedby={quantizeHintId}
        checked={quantizeWhileRecording}
        onCheckedChange={(checked) =>
          midiDeviceStore.setQuantizeWhileRecording(checked === true)
        }
        style={{ marginTop: "1rem" }}
        label={<Localized name="midi-quantize-while-recording" />}
      />
      <SettingHint id={quantizeHintId}>
        <Localized name="midi-quantize-while-recording-description" />
      </SettingHint>
    </RecordingSettingsGroup>
  )
}

const SectionTitle = styled.div`
  font-weight: bold;
  margin: 2rem 0 0.75rem 0;
  display: flex;
  align-items: center;
  gap: 0.5rem;
`

const IconButton = styled.button`
  display: flex;
  align-items: center;
  justify-content: center;
  width: 1.5rem;
  height: 1.5rem;
  border-radius: 999px;
  border: none;
  background: none;
  color: var(--color-text-secondary);
  cursor: pointer;
  padding: 0;

  &:hover {
    background: var(--color-highlight);
    color: var(--color-text);
  }

  svg {
    width: 1rem;
    height: 1rem;
  }
`

const Divider = styled.div`
  height: 1px;
  background: var(--color-divider);
  margin: 1rem 0;
`

export const MIDIDeviceView: FC = () => {
  const {
    inputDevices: midiInputDevices,
    outputDevices,
    isLoading: midiIsLoading,
    requestError: midiRequestError,
    midiInputRouting,
    setInputEnable: setMidiInputEnable,
    setOutputEnable,
    setMidiInputRouting,
  } = useMIDIDevice()

  const {
    inputDevices: btInputDevices,
    isBluetoothSupported,
    isLoading: btIsLoading,
    requestError: btRequestError,
    requestBluetoothMIDIDevice,
    setInputEnable: setBTInputEnable,
  } = useBLEMIDIDevice()

  const isLoading = midiIsLoading || btIsLoading
  const requestError = midiRequestError || btRequestError

  const inputDevices: Device[] = useMemo(
    () => [...midiInputDevices, ...btInputDevices],
    [midiInputDevices, btInputDevices],
  )

  const setInputEnable = useCallback(
    (deviceId: string, isEnabled: boolean) => {
      if (btInputDevices.some((d) => d.id === deviceId)) {
        setBTInputEnable(deviceId, isEnabled)
      } else {
        setMidiInputEnable(deviceId, isEnabled)
      }
    },
    [setBTInputEnable, setMidiInputEnable, btInputDevices],
  )

  return (
    <>
      <DialogSectionTitle>
        <Localized name="midi-settings" />
      </DialogSectionTitle>
      <DialogContent>
        {isLoading && <CircularProgress />}
        {requestError && (
          <Alert severity="warning">{requestError.message}</Alert>
        )}
        {!isLoading && (
          <>
            <SectionTitle>
              <Localized name="inputs" />
              {isBluetoothSupported && (
                <IconButton
                  onClick={requestBluetoothMIDIDevice}
                  title="Connect Bluetooth MIDI Device"
                >
                  <BluetoothIcon />
                </IconButton>
              )}
            </SectionTitle>
            <DeviceList>
              {inputDevices.length === 0 && (
                <Notice>
                  <Localized name="no-inputs" />
                </Notice>
              )}
              {inputDevices.map((device) => (
                <DeviceRow
                  key={device.id}
                  device={device}
                  onCheck={async (checked) => {
                    setInputEnable(device.id, checked)
                  }}
                />
              ))}
            </DeviceList>
            <RecordingSettings />
            <SectionTitle>
              <Localized name="midi-routing" />
            </SectionTitle>
            <RadioButton
              label={<Localized name="midi-routing-selected-track" />}
              description={
                <Localized name="midi-routing-selected-track-description" />
              }
              isSelected={midiInputRouting === "selectedTrack"}
              onClick={() => setMidiInputRouting("selectedTrack")}
            />
            <RadioButton
              label={<Localized name="midi-routing-channel" />}
              description={
                <Localized name="midi-routing-channel-description" />
              }
              isSelected={midiInputRouting === "channelRouting"}
              onClick={() => setMidiInputRouting("channelRouting")}
            />
            <Divider />
            <SectionTitle>
              <Localized name="outputs" />
            </SectionTitle>
            <DeviceList>
              {outputDevices.map((device) => (
                <DeviceRow
                  key={device.id}
                  device={device}
                  onCheck={(checked) => setOutputEnable(device.id, checked)}
                />
              ))}
            </DeviceList>
          </>
        )}
      </DialogContent>
    </>
  )
}
