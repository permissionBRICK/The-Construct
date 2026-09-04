import { describe, expect, it } from "vite-plus/test";

import {
  computeAudioLevel,
  createLinearResampler,
  describeCaptureError,
  floatToPcm16,
  resolveVoiceInputSource,
  VOICE_SAMPLE_RATE,
} from "./clientAudioCapture";

describe("createLinearResampler", () => {
  it("passes blocks through untouched when the rates already match", () => {
    const resample = createLinearResampler(VOICE_SAMPLE_RATE, VOICE_SAMPLE_RATE);
    const block = new Float32Array([0.1, 0.2, 0.3]);
    expect(resample(block)).toBe(block);
  });

  it("decimates an integer ratio exactly", () => {
    const resample = createLinearResampler(32_000, 16_000);
    expect([...resample(new Float32Array([0, 1, 2, 3, 4, 5]))]).toEqual([0, 2, 4]);
  });

  it("interpolates when upsampling", () => {
    const resample = createLinearResampler(16_000, 32_000);
    expect([...resample(new Float32Array([0, 1]))]).toEqual([0, 0.5, 1]);
  });

  it("stays continuous across block boundaries", () => {
    const whole = new Float32Array(24);
    for (let index = 0; index < whole.length; index += 1) whole[index] = index;

    const single = createLinearResampler(48_000, 16_000);
    const expected = [...single(whole)];

    const streamed = createLinearResampler(48_000, 16_000);
    const chunked = [
      ...streamed(whole.slice(0, 7)),
      ...streamed(whole.slice(7, 13)),
      ...streamed(whole.slice(13)),
    ];

    expect(chunked).toEqual(expected);
    // 48 kHz in, 16 kHz out: a third of the samples come back out.
    expect(chunked).toHaveLength(8);
  });

  it("produces roughly a chunk's worth of 16 kHz samples from a 44.1 kHz block", () => {
    const resample = createLinearResampler(44_100, VOICE_SAMPLE_RATE);
    const block = new Float32Array(4410); // 100 ms at 44.1 kHz
    expect(resample(block).length).toBe(1600);
  });

  it("ignores empty blocks", () => {
    const resample = createLinearResampler(48_000, 16_000);
    expect(resample(new Float32Array()).length).toBe(0);
  });

  it("refuses nonsensical rates", () => {
    expect(() => createLinearResampler(0, 16_000)).toThrow();
    expect(() => createLinearResampler(48_000, -1)).toThrow();
  });
});

describe("floatToPcm16", () => {
  it("writes signed 16-bit little-endian samples", () => {
    const bytes = floatToPcm16(new Float32Array([0, 1, -1]));
    expect(bytes).toHaveLength(6);
    const view = new DataView(bytes.buffer);
    expect(view.getInt16(0, true)).toBe(0);
    expect(view.getInt16(2, true)).toBe(32_767);
    expect(view.getInt16(4, true)).toBe(-32_767);
    // Little-endian on every platform, not host order.
    expect(Array.from(bytes.slice(2, 4))).toEqual([0xff, 0x7f]);
  });

  it("clamps samples outside the normalized range", () => {
    const view = new DataView(floatToPcm16(new Float32Array([4, -4])).buffer);
    expect(view.getInt16(0, true)).toBe(32_767);
    expect(view.getInt16(2, true)).toBe(-32_767);
  });

  it("emits two bytes per sample so a 100 ms chunk stays 3200 bytes", () => {
    expect(floatToPcm16(new Float32Array(1600))).toHaveLength(3200);
  });
});

describe("computeAudioLevel", () => {
  it("is zero for silence and for anything under the noise floor", () => {
    expect(computeAudioLevel(new Float32Array())).toBe(0);
    expect(computeAudioLevel(new Float32Array([0, 0, 0]))).toBe(0);
    expect(computeAudioLevel(new Float32Array([0.005, -0.005]))).toBe(0);
  });

  it("matches the server's formula", () => {
    const samples = new Float32Array([0.05, -0.05, 0.05, -0.05]);
    expect(computeAudioLevel(samples)).toBeCloseTo((0.05 - 0.006) * 10, 6);
  });

  it("saturates at one", () => {
    expect(computeAudioLevel(new Float32Array([1, -1]))).toBe(1);
  });
});

describe("describeCaptureError", () => {
  it("explains the errors a user can act on", () => {
    expect(describeCaptureError({ name: "NotAllowedError" })).toBe("Microphone access was denied.");
    expect(describeCaptureError({ name: "NotFoundError" })).toBe("No microphone found.");
    expect(describeCaptureError({ name: "NotReadableError" })).toBe(
      "The microphone is already in use by another application.",
    );
  });

  it("keeps the underlying reason for anything else", () => {
    expect(describeCaptureError(new Error("boom"))).toBe("Could not start the microphone: boom");
    expect(describeCaptureError("boom")).toBe("Could not start the microphone.");
  });
});

describe("resolveVoiceInputSource", () => {
  const capable = {
    canCaptureHere: true,
    serverAcceptsClientAudio: true,
    secureContext: true,
  } as const;

  it("always records on the host when asked to", () => {
    expect(resolveVoiceInputSource({ ...capable, preference: "host" }).source).toBe("host");
    expect(
      resolveVoiceInputSource({
        preference: "host",
        canCaptureHere: false,
        serverAcceptsClientAudio: false,
        secureContext: false,
      }).source,
    ).toBe("host");
  });

  it("prefers this device on auto and falls back to the host bridge", () => {
    expect(resolveVoiceInputSource({ ...capable, preference: "auto" }).source).toBe("client");
    expect(
      resolveVoiceInputSource({ ...capable, preference: "auto", canCaptureHere: false }).source,
    ).toBe("host");
    expect(
      resolveVoiceInputSource({ ...capable, preference: "auto", serverAcceptsClientAudio: false })
        .source,
    ).toBe("host");
  });

  it("refuses to start, with a reason, when this device was demanded but cannot record", () => {
    const insecure = resolveVoiceInputSource({
      preference: "client",
      canCaptureHere: false,
      serverAcceptsClientAudio: true,
      secureContext: false,
    });
    expect(insecure.source).toBeNull();
    expect(insecure.reason).toContain("HTTPS");

    const noMicrophoneApi = resolveVoiceInputSource({
      preference: "client",
      canCaptureHere: false,
      serverAcceptsClientAudio: true,
      secureContext: true,
    });
    expect(noMicrophoneApi.source).toBeNull();
    expect(noMicrophoneApi.reason).toContain("microphone");

    const oldServer = resolveVoiceInputSource({
      ...capable,
      preference: "client",
      serverAcceptsClientAudio: false,
    });
    expect(oldServer.source).toBeNull();
    expect(oldServer.reason).toContain("host bridge");
  });
});
