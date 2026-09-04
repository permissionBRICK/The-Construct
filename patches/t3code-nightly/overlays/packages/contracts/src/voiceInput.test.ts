import * as Schema from "effect/Schema";
import { describe, expect, it } from "vite-plus/test";

import {
  VOICE_INPUT_MAX_CHUNK_BYTES,
  VoiceInputAudioInput,
  VoiceInputStartInput,
} from "./voiceInput.ts";

/** Base64 for `byteLength` zero bytes, without needing a Node Buffer here. */
const base64OfZeroBytes = (byteLength: number): string => {
  const groups = Math.floor(byteLength / 3);
  const remainder = byteLength % 3;
  return `${"A".repeat(groups * 4)}${remainder === 0 ? "" : remainder === 1 ? "AA==" : "AAA="}`;
};

const decodeStart = Schema.decodeUnknownSync(VoiceInputStartInput);
const decodeAudio = Schema.decodeUnknownSync(VoiceInputAudioInput);
const encodeAudio = Schema.encodeSync(VoiceInputAudioInput);

describe("VoiceInputStartInput", () => {
  it("leaves the source absent for clients that only know the host bridge", () => {
    expect(decodeStart({ sessionId: "session-1" }).source).toBeUndefined();
  });

  it("carries an explicit source", () => {
    expect(decodeStart({ sessionId: "session-1", source: "client" }).source).toBe("client");
    expect(decodeStart({ sessionId: "session-1", source: "host" }).source).toBe("host");
  });

  it("rejects a source it does not know", () => {
    expect(() => decodeStart({ sessionId: "session-1", source: "bluetooth" })).toThrow();
  });
});

describe("VoiceInputAudioInput", () => {
  it("round-trips a PCM chunk through base64", () => {
    const pcm = new Uint8Array([0, 1, 255, 128, 64, 32]);
    const wire = encodeAudio({ sessionId: "session-1", chunk: pcm });
    expect(wire.chunk).toBe("AAH/gEAg");

    const decoded = decodeAudio(wire);
    expect(decoded.sessionId).toBe("session-1");
    expect([...decoded.chunk]).toEqual([...pcm]);
  });

  it("accepts a full 100 ms chunk of 16 kHz mono S16LE", () => {
    const chunk = new Uint8Array(3200);
    expect(decodeAudio(encodeAudio({ sessionId: "session-1", chunk })).chunk.byteLength).toBe(3200);
  });

  it("rejects chunks past the size bound in both directions", () => {
    const oversized = new Uint8Array(VOICE_INPUT_MAX_CHUNK_BYTES + 1);
    expect(() => encodeAudio({ sessionId: "session-1", chunk: oversized })).toThrow();
    expect(() =>
      decodeAudio({ sessionId: "session-1", chunk: base64OfZeroBytes(oversized.byteLength) }),
    ).toThrow();
  });

  it("rejects a payload that is not base64", () => {
    expect(() => decodeAudio({ sessionId: "session-1", chunk: "not base64!!" })).toThrow();
  });
});
