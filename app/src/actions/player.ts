import { Measure } from "@signal-app/core"
import { useCallback } from "react"
import { usePianoRollTickScroll } from "../features/piano-roll/hooks/usePianoRoll"
import { usePlayer } from "../hooks/usePlayer"
import { useSong } from "../hooks/useSong"
import { useStores } from "../hooks/useStores"

export const useStop = () => {
  const { setScrollLeftInTicks } = usePianoRollTickScroll()
  const { stop, setPosition } = usePlayer()

  return useCallback(() => {
    stop()
    setPosition(0)
    setScrollLeftInTicks(0)
  }, [stop, setPosition, setScrollLeftInTicks])
}

export const useRewindOneBar = () => {
  const { measures, timebase } = useSong()
  const { scrollLeftTicks, setScrollLeftInTicks } = usePianoRollTickScroll()
  const { player } = useStores()

  const getPreviousMeasureTick = useCallback(
    (position: number) =>
      Measure.getPreviousMeasureTick(measures, position, timebase),
    [measures, timebase],
  )

  return useCallback(() => {
    const tick = getPreviousMeasureTick(player.position)
    player.position = tick

    // make sure player doesn't move out of sight to the left
    if (tick < scrollLeftTicks) {
      setScrollLeftInTicks(tick)
    }
  }, [getPreviousMeasureTick, player, scrollLeftTicks, setScrollLeftInTicks])
}

export const useFastForwardOneBar = () => {
  const { transform, scrollLeft, canvasWidth, setScrollLeftInPixels } =
    usePianoRollTickScroll()
  const { measures, timebase } = useSong()
  const { player } = useStores()

  const getNextMeasureTick = useCallback(
    (position: number) =>
      Measure.getNextMeasureTick(measures, position, timebase),
    [measures, timebase],
  )

  return useCallback(() => {
    const tick = getNextMeasureTick(player.position)
    player.position = tick

    // make sure player doesn't move out of sight to the right
    const x = transform.getX(tick)
    const screenX = x - scrollLeft
    if (screenX > canvasWidth * 0.7) {
      setScrollLeftInPixels(x - canvasWidth * 0.7)
    }
  }, [
    getNextMeasureTick,
    player,
    transform,
    scrollLeft,
    canvasWidth,
    setScrollLeftInPixels,
  ])
}
