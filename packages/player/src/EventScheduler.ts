import { tickToMillisec } from "./tick.js"

export type SchedulableEvent = {
  tick: number
}

export interface EventSchedulerLoop {
  begin: number
  end: number
}

interface Jump {
  from: number
  to: number
}

interface PlaybackAnchor {
  timestamp: number
  tick: number
  bpm: number
}

const PLAYBACK_HISTORY_MS = 2000

export interface EventSchedulerSource<E extends SchedulableEvent> {
  timebase: number
  endOfSong: number
  getEvents(startTick: number, endTick: number): readonly E[]

  /*
   to restore synthesizer state (e.g. pitch bend)
   collect all previous state events
   and send them to the synthesizer
  */
  getCurrentStateEvents(tick: number): readonly Omit<E, "tick">[]
}

export interface SchedulerResult<E extends SchedulableEvent> {
  events: readonly WithTimestamp<E>[]
  shouldStop: boolean
}

type WithTimestamp<E> = {
  event: E
  timestamp: number
}

/**
 * Class for reading chronological events.
 * Perform lookahead to schedule accurately.
 * https://www.html5rocks.com/ja/tutorials/audio/scheduling/
 */
export class EventScheduler<E extends SchedulableEvent> {
  lookAheadTime = 100

  // Number of ticks per 1/4 beat
  timebase = 480

  loop: EventSchedulerLoop | null = null

  private _currentTick = 0
  private _scheduledTick = 0
  private _prevTime: number | undefined = undefined
  private _isStopScheduled = false
  private _scheduledSeekTick: number | null = null
  private _enqueuedEvents: readonly Omit<E, "tick">[] = []
  private readonly playbackAnchors: PlaybackAnchor[] = []

  constructor(
    private readonly eventSource: EventSchedulerSource<E>,
    private readonly createLoopEndEvents: () => readonly Omit<E, "tick">[],
    private readonly createStopEvents: () => readonly Omit<
      E,
      "tick"
    >[] = () => [],
    tick = 0,
    lookAheadTime = 100,
  ) {
    this._currentTick = tick
    this._scheduledTick = tick
    this.timebase = this.eventSource.timebase
    this.lookAheadTime = lookAheadTime
  }

  get scheduledTick() {
    return this._scheduledTick
  }

  millisecToTick(ms: number, bpm: number) {
    return (((ms / 1000) * bpm) / 60) * this.timebase
  }

  getTickAtTime(timestamp: number): number {
    for (let i = this.playbackAnchors.length - 1; i >= 0; i--) {
      const anchor = this.playbackAnchors[i]
      if (anchor.timestamp <= timestamp) {
        return (
          anchor.tick +
          this.millisecToTick(timestamp - anchor.timestamp, anchor.bpm)
        )
      }
    }
    return this.playbackAnchors[0]?.tick ?? this._currentTick
  }

  private rememberPlaybackAnchor(anchor: PlaybackAnchor) {
    const last = this.playbackAnchors[this.playbackAnchors.length - 1]
    // The scheduler may already have queued a jump that has yet to play.
    if (last !== undefined && last.timestamp > anchor.timestamp) {
      return
    }
    if (last?.timestamp === anchor.timestamp) {
      this.playbackAnchors[this.playbackAnchors.length - 1] = anchor
    } else {
      this.playbackAnchors.push(anchor)
    }

    const cutoff = anchor.timestamp - PLAYBACK_HISTORY_MS
    while (
      this.playbackAnchors.length > 1 &&
      this.playbackAnchors[1].timestamp < cutoff
    ) {
      this.playbackAnchors.shift()
    }
  }

  scheduleSeek(tick: number) {
    this._scheduledSeekTick = tick
  }

  scheduleStop() {
    this._isStopScheduled = true
  }

  // enqueue events to be prepended to the next readNextEvents() result.
  enqueueEvents(events: readonly Omit<E, "tick">[]) {
    this._enqueuedEvents = this._enqueuedEvents.concat(events)
  }

  readNextEvents(bpm: number, timestamp: number): SchedulerResult<E> {
    const timeAtTick = (tick: number, currentTick: number) =>
      timestamp +
      Math.max(0, tickToMillisec(tick - currentTick, bpm, this.timebase))

    const withTimestamp =
      (currentTick: number) =>
      (e: E): WithTimestamp<E> => ({
        event: e,
        timestamp: timeAtTick(e.tick, currentTick),
      })

    const getEventsInRange = (
      startTick: number,
      endTick: number,
      currentTick: number,
    ) =>
      this.eventSource
        .getEvents(startTick, endTick)
        .map(withTimestamp(currentTick))

    if (this._prevTime === undefined) {
      this._prevTime = timestamp
    }
    const delta = timestamp - this._prevTime
    const deltaTick = Math.max(0, this.millisecToTick(delta, bpm))
    const nowTick = this._currentTick + deltaTick
    const lookAheadTick = this.millisecToTick(this.lookAheadTime, bpm)
    this.rememberPlaybackAnchor({ timestamp, tick: nowTick, bpm })

    // Process from the last scheduled point to the lookahead time
    const startTick = this._scheduledTick
    const endTick = nowTick + lookAheadTick

    this._prevTime = timestamp

    if (this._isStopScheduled) {
      this._isStopScheduled = false
      this._currentTick = nowTick
      this._scheduledTick = endTick

      const events = this.createStopEvents().map((e) =>
        withTimestamp(nowTick)({ ...e, tick: endTick } as E),
      )
      return {
        events,
        shouldStop: true,
      }
    }

    // If a seek has been scheduled, jump to that tick instead of continuing
    // from the current position. If a loop is set, jump to the loop start.
    let jump: Jump | null = null

    if (this._scheduledSeekTick !== null) {
      jump = {
        from: endTick,
        to: this._scheduledSeekTick,
      }
      this._scheduledSeekTick = null
    }
    if (
      this.loop !== null &&
      startTick < this.loop.end &&
      endTick >= this.loop.end
    ) {
      jump = {
        from: this.loop.end,
        to: this.loop.begin,
      }
    }

    const prependedEvents: WithTimestamp<E>[] = []

    if (this._enqueuedEvents.length > 0) {
      prependedEvents.push(
        ...this._enqueuedEvents.map((e) =>
          withTimestamp(nowTick)({ ...e, tick: nowTick } as E),
        ),
      )
      this._enqueuedEvents = []
    }

    if (jump) {
      this.rememberPlaybackAnchor({
        timestamp: timeAtTick(jump.from, nowTick),
        tick: jump.to,
        bpm,
      })
      const offset = endTick - jump.from
      const endTick2 = jump.to + offset
      const currentTick = jump.to - (jump.from - nowTick)
      this._currentTick = currentTick
      this._scheduledTick = endTick2

      const events = [
        ...prependedEvents,
        ...getEventsInRange(startTick, jump.from, nowTick),
        ...this.createLoopEndEvents().map((e) =>
          withTimestamp(currentTick)({ ...e, tick: jump.to } as E),
        ),
        ...this.eventSource
          .getCurrentStateEvents(jump.to)
          .map((e) => withTimestamp(currentTick)({ ...e, tick: jump.to } as E)),
        ...getEventsInRange(jump.to, endTick2, currentTick),
      ]
      return {
        events,
        shouldStop: false,
      }
    } else {
      this._currentTick = nowTick
      this._scheduledTick = endTick

      return {
        events: [
          ...prependedEvents,
          ...getEventsInRange(startTick, endTick, nowTick),
        ],
        shouldStop: this._scheduledTick >= this.eventSource.endOfSong,
      }
    }
  }
}
