import {
  Beat,
  convertTrackEvents,
  getStatusEvents,
  isEventInRange,
  noteOnMidiEvent,
  Range,
  type SongStore,
  type TrackId,
} from "@signal-app/core"
import {
  type IEventSource,
  METRONOME_ACCENT,
  METRONOME_BEAT,
  type PlayerEvent,
} from "@signal-app/player"

export const METRONOME_TRACK_ID = 99999 as TrackId

export class EventSource implements IEventSource {
  constructor(private readonly songStore: SongStore) {}

  get timebase(): number {
    return this.songStore.song.timebase
  }

  get endOfSong(): number {
    return this.songStore.song.endOfSong
  }

  getEvents(startTick: number, endTick: number): PlayerEvent[] {
    const { song } = this.songStore
    const range = Range.create(startTick, endTick)
    const beatEvents = Beat.createInRange(song.measures, song.timebase, range)
      .filter(isEventInRange(range))
      .map(beatToEvent)
    return (song.allEvents as PlayerEvent[])
      .filter(isEventInRange(range))
      .concat(beatEvents)
  }

  getCurrentStateEvents(tick: number) {
    return this.songStore.song.tracks.flatMap((t) => {
      const statusEvents = getStatusEvents(tick)(t.events)
      return convertTrackEvents(statusEvents, t.channel, t.id)
    })
  }
}

function beatToEvent(beat: Beat): PlayerEvent {
  const { noteNumber, velocity } =
    beat.beat === 0 ? METRONOME_ACCENT : METRONOME_BEAT
  return {
    ...noteOnMidiEvent(0, 9, noteNumber, velocity),
    tick: beat.tick,
    trackId: METRONOME_TRACK_ID,
  }
}
