import { afterEach, describe, expect, it, vi } from "vitest"
import { Player } from "./Player.js"
import type { PlayerEvent } from "./PlayerEvent.js"

const makePlayer = () => {
  vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "performance"] })

  let animationFrame: FrameRequestCallback | undefined
  vi.stubGlobal("window", {
    setInterval,
    requestAnimationFrame: (callback: FrameRequestCallback) => {
      animationFrame = callback
      return 1
    },
    cancelAnimationFrame: () => {
      animationFrame = undefined
    },
  })

  const note: PlayerEvent = {
    type: "channel",
    subtype: "noteOn",
    channel: 0,
    noteNumber: 60,
    velocity: 100,
    tick: 96,
    trackId: null,
  }
  const sendEvent = vi.fn()
  const player = new Player(
    { activate: () => {}, sendEvent },
    {
      timebase: 480,
      endOfSong: 960,
      getEvents: (start, end) =>
        note.tick >= start && note.tick < end ? [note] : [],
      getCurrentStateEvents: () => [],
    },
    () => performance.now() - 20,
  )

  return {
    player,
    sendEvent,
    advanceAndRender: (milliseconds: number) => {
      vi.advanceTimersByTime(milliseconds)
      animationFrame?.(performance.now())
    },
  }
}

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe("Player audible position", () => {
  it("updates on a frame using output time while notes are scheduled ahead", () => {
    const { player, sendEvent, advanceAndRender } = makePlayer()
    player.play()

    advanceAndRender(100)
    expect(sendEvent).toHaveBeenCalledTimes(1)
    expect(sendEvent.mock.calls[0][1]).toBeCloseTo(0.05)
    expect(player.position).toBeCloseTo(28.8)

    // The note at tick 96 reaches the output at 170 ms.
    advanceAndRender(70)
    expect(player.position).toBeCloseTo(96)
  })

  it("keeps a requested seek visible until the queued jump is audible", () => {
    const { player, advanceAndRender } = makePlayer()
    player.play()
    advanceAndRender(100)

    player.position = 240
    advanceAndRender(10)
    expect(player.position).toBe(240)
    advanceAndRender(50)
    expect(player.position).toBe(240)

    advanceAndRender(111)
    expect(player.position).toBeCloseTo(240.96)
  })

  it("does not overwrite a position set immediately after stop", () => {
    const { player, advanceAndRender } = makePlayer()
    player.play()
    advanceAndRender(150)

    player.stop()
    player.position = 0
    advanceAndRender(20)
    expect(player.position).toBe(0)

    advanceAndRender(50)
    expect(player.isPlaying).toBe(false)
    expect(player.position).toBe(0)
  })
})
