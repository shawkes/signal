/** Map a performance timestamp at the audio output to the player's render clock. */
export function getRenderTimeForOutputTime(
  context: Pick<
    AudioContext,
    | "state"
    | "currentTime"
    | "getOutputTimestamp"
    | "baseLatency"
    | "outputLatency"
  >,
  outputTime: number,
): number {
  if (context.state !== "running") {
    return outputTime
  }

  const timestamp = context.getOutputTimestamp?.()
  if (
    timestamp?.contextTime !== undefined &&
    timestamp.performanceTime !== undefined &&
    timestamp.performanceTime > 0
  ) {
    const renderTime = context.currentTime
    const now = performance.now()
    const audibleTime =
      timestamp.performanceTime + (renderTime - timestamp.contextTime) * 1000
    return outputTime - Math.max(0, audibleTime - now)
  }

  return (
    outputTime -
    ((context.baseLatency ?? 0) + (context.outputLatency ?? 0)) * 1000
  )
}
