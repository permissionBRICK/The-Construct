import { describe, expect, it, vi } from "@effect/vitest";

import {
  createVoiceAudioSource,
  FIRST_CLIENT_CHUNK_TIMEOUT_MS,
  NO_CLIENT_AUDIO_MESSAGE,
  routeVoiceAudio,
  type VoiceAudioSourceHandlers,
} from "./voiceInput.ts";

const handlers = () => {
  const audio: Array<Buffer> = [];
  const failures: Array<string> = [];
  return {
    audio,
    failures,
    handlers: {
      audio: (chunk) => audio.push(chunk),
      fail: (message) => failures.push(message),
    } satisfies VoiceAudioSourceHandlers,
  };
};

describe("voice audio sources", () => {
  it("defaults to the host recorder when the client did not pick a source", () => {
    expect(createVoiceAudioSource(undefined).kind).toBe("host");
    expect(createVoiceAudioSource("host").kind).toBe("host");
    expect(createVoiceAudioSource("client").kind).toBe("client");
  });

  it("emits server-side levels only for host audio", () => {
    expect(createVoiceAudioSource("host").emitsLevels).toBe(true);
    expect(createVoiceAudioSource("client").emitsLevels).toBe(false);
  });

  it("refuses pushed audio for a host session without starting the recorder", () => {
    // Recording happens on the server, so a pushed chunk has nowhere to go.
    expect(createVoiceAudioSource("host").push(new Uint8Array([1, 2]))).toBe(false);
  });

  it("drops client chunks that arrive before the transcription socket is open", () => {
    const source = createVoiceAudioSource("client");
    expect(source.push(new Uint8Array([1, 2]))).toBe(false);
  });

  it("forwards client chunks once the session is listening", () => {
    vi.useFakeTimers();
    try {
      const sink = handlers();
      const source = createVoiceAudioSource("client");
      source.start(sink.handlers);

      expect(source.push(new Uint8Array([1, 2, 3, 4]))).toBe(true);
      expect(sink.audio).toHaveLength(1);
      expect([...sink.audio[0]!]).toEqual([1, 2, 3, 4]);

      // The first chunk arrived, so the "no audio" watchdog must not fire.
      vi.advanceTimersByTime(FIRST_CLIENT_CHUNK_TIMEOUT_MS * 2);
      expect(sink.failures).toEqual([]);

      source.stop();
      expect(source.push(new Uint8Array([5]))).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("ignores empty client chunks", () => {
    const sink = handlers();
    const source = createVoiceAudioSource("client");
    source.start(sink.handlers);
    expect(source.push(new Uint8Array())).toBe(false);
    expect(sink.audio).toEqual([]);
    source.stop();
  });

  it("fails the session when no client audio arrives at all", () => {
    vi.useFakeTimers();
    try {
      const sink = handlers();
      const source = createVoiceAudioSource("client");
      source.start(sink.handlers);

      vi.advanceTimersByTime(FIRST_CLIENT_CHUNK_TIMEOUT_MS - 1);
      expect(sink.failures).toEqual([]);
      vi.advanceTimersByTime(1);
      expect(sink.failures).toEqual([NO_CLIENT_AUDIO_MESSAGE]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("stops the watchdog when the session ends before any audio", () => {
    vi.useFakeTimers();
    try {
      const sink = handlers();
      const source = createVoiceAudioSource("client");
      source.start(sink.handlers);
      source.stop();
      vi.advanceTimersByTime(FIRST_CLIENT_CHUNK_TIMEOUT_MS * 2);
      expect(sink.failures).toEqual([]);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("routeVoiceAudio", () => {
  it("rejects unknown session ids quietly", () => {
    const sessions = new Map<string, { pushAudio: (chunk: Uint8Array) => boolean }>();
    expect(routeVoiceAudio(sessions, "unknown", new Uint8Array([1]))).toBe(false);
  });

  it("hands the chunk to the session that owns the id", () => {
    const seen: Array<{ id: string; bytes: number }> = [];
    const session = (id: string, accepts: boolean) => ({
      pushAudio: (chunk: Uint8Array) => {
        seen.push({ id, bytes: chunk.byteLength });
        return accepts;
      },
    });
    const sessions = new Map([
      ["client-session", session("client-session", true)],
      ["host-session", session("host-session", false)],
    ]);

    expect(routeVoiceAudio(sessions, "client-session", new Uint8Array([1, 2]))).toBe(true);
    expect(routeVoiceAudio(sessions, "host-session", new Uint8Array([1, 2, 3]))).toBe(false);
    expect(seen).toEqual([
      { id: "client-session", bytes: 2 },
      { id: "host-session", bytes: 3 },
    ]);
  });
});
