import { describe, expect, it } from "vitest"
import { EventScheduler } from "./EventScheduler"

describe("EventScheduler", () => {
  it("readNextEvents", () => {
    const events = [{ tick: 0 }, { tick: 100 }, { tick: 110 }]
    const s = new EventScheduler(
      {
        timebase: 480,
        endOfSong: 999999,
        getEvents: (start, end) => filterEventsWithRange(events, start, end),
        getCurrentStateEvents: () => [],
      },
      () => [],
      () => [],
      0,
      100,
    )

    // The first event is read within the look ahead time
    {
      const { events: result } = s.readNextEvents(120, 0)
      expect(result.length).toBe(1)
      expect(result[0].timestamp).toBe(0)
      expect(result[0].event).toBe(events[0])
    }

    // No events are returned if no time has passed since the last read
    {
      const { events: result } = s.readNextEvents(120, 0)
      expect(result.length).toBe(0)
    }

    // Subsequent events are returned after time has passed
    {
      const { events: result } = s.readNextEvents(120, 120)
      expect(result.length).toBe(2)
      expect(result[0].event).toBe(events[1])
      expect(result[0].timestamp).toBe(120)
      expect(result[1].event).toBe(events[2])
      expect(result[1].timestamp).toBe(120)
    }
  })

  it("maps delayed MIDI timestamps without including scheduling lookahead", () => {
    const scheduler = createScheduler(480)

    expect(scheduler.getTickAtTime(975)).toBe(480)
    scheduler.readNextEvents(120, 1000)
    scheduler.readNextEvents(120, 1050)

    expect(scheduler.scheduledTick).toBeCloseTo(624)
    expect(scheduler.getTickAtTime(975)).toBe(480)
    expect(scheduler.getTickAtTime(1025)).toBeCloseTo(504)
    expect(scheduler.getTickAtTime(1075)).toBeCloseTo(552)
  })

  it("retains earlier timing when the scheduler changes tempo", () => {
    const scheduler = createScheduler(480)

    scheduler.readNextEvents(120, 1000)
    scheduler.readNextEvents(240, 1050)

    expect(scheduler.getTickAtTime(1025)).toBeCloseTo(504)
    expect(scheduler.getTickAtTime(1075)).toBeCloseTo(624)
  })

  it("maps a loop to the old position until its scheduled jump", () => {
    const scheduler = createScheduler(420)
    scheduler.loop = { begin: 240, end: 480 }

    scheduler.readNextEvents(120, 1000)
    scheduler.readNextEvents(120, 1050)

    expect(scheduler.getTickAtTime(1050)).toBeCloseTo(468)
    expect(scheduler.getTickAtTime(1062.5)).toBeCloseTo(240)
    expect(scheduler.getTickAtTime(1075)).toBeCloseTo(252)
  })

  it("maps a seek to the old position until its scheduled jump", () => {
    const scheduler = createScheduler(480)

    scheduler.readNextEvents(120, 1000)
    scheduler.scheduleSeek(0)
    scheduler.readNextEvents(120, 1050)
    scheduler.readNextEvents(120, 1100)

    expect(scheduler.getTickAtTime(1125)).toBeCloseTo(600)
    expect(scheduler.getTickAtTime(1150)).toBeCloseTo(0)
    expect(scheduler.getTickAtTime(1175)).toBeCloseTo(24)
  })
})

const createScheduler = (tick: number) =>
  new EventScheduler(
    {
      timebase: 480,
      endOfSong: 999999,
      getEvents: () => [],
      getCurrentStateEvents: () => [],
    },
    () => [],
    () => [],
    tick,
  )

const filterEventsWithRange = <T extends { tick: number }>(
  events: T[],
  start: number,
  end: number,
): T[] => events.filter((e) => e.tick >= start && e.tick < end)
