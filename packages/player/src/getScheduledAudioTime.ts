const renderTimeByContext = new WeakMap<
  AudioContext,
  { timestamp: number; contextTime: number }
>()

export function getScheduledAudioTime(
  context: AudioContext,
  delayTime: number,
  timestampNow: number,
): number {
  // The audio clock can advance between song and metronome dispatches.
  // Share its first sample for all events in the same scheduler batch.
  let renderTime = renderTimeByContext.get(context)
  if (renderTime?.timestamp !== timestampNow) {
    renderTime = { timestamp: timestampNow, contextTime: context.currentTime }
    renderTimeByContext.set(context, renderTime)
  }
  return renderTime.contextTime + delayTime
}
