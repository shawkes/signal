import { useCallback } from "react"
import { usePianoRollQuantizer } from "../features/piano-roll/hooks/usePianoRoll"
import { usePlayer } from "./usePlayer"
import { useStores } from "./useStores"

export const useToggleRecording = () => {
  const { midiDeviceStore, midiRecorder } = useStores()
  const { forceQuantizeRound } = usePianoRollQuantizer()
  const { play, stop } = usePlayer()

  return useCallback(() => {
    if (midiRecorder.isRecording) {
      midiRecorder.stop()
      stop()
    } else {
      midiRecorder.start(
        midiDeviceStore.quantizeWhileRecording ? forceQuantizeRound : undefined,
      )
      play()
    }
  }, [forceQuantizeRound, midiDeviceStore, midiRecorder, play, stop])
}
