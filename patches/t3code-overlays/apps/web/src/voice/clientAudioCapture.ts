/**
 * Microphone capture in the client.
 *
 * The server can record on its own machine through Construct's host bridge, but
 * that bridge only exists while a VS Code window with the Construct extension is
 * open. When the client can reach a microphone itself, it captures 16 kHz mono
 * S16LE PCM here and pushes it to the server instead.
 *
 * Everything above the browser calls (resampling, PCM conversion, the level
 * formula, source selection) is a plain function so it can be tested without a
 * WebAudio implementation.
 */

import type { VoiceInputSourcePreference } from "@t3tools/contracts/settings";

/** What the Anthropic speech socket expects, and what the server forwards to it. */
export const VOICE_SAMPLE_RATE = 16_000;
/** ~100 ms of audio per pushed chunk. */
export const VOICE_CHUNK_MS = 100;
const LEVEL_INTERVAL_MS = 75;

export type VoiceInputSourceResolution =
  | { readonly source: "client" | "host"; readonly reason?: undefined }
  /** Nothing can record: the caller must not start a session, and says why. */
  | { readonly source: null; readonly reason: string };

/**
 * Picks the audio source for a voice session. `auto` prefers this device and
 * silently falls back to the host bridge; an explicit `client` preference that
 * cannot be honoured fails loudly rather than recording somewhere the user did
 * not ask for.
 */
export function resolveVoiceInputSource(input: {
  readonly preference: VoiceInputSourcePreference;
  readonly canCaptureHere: boolean;
  readonly serverAcceptsClientAudio: boolean;
  readonly secureContext: boolean;
}): VoiceInputSourceResolution {
  if (input.preference === "host") return { source: "host" };
  const clientUsable = input.canCaptureHere && input.serverAcceptsClientAudio;
  if (input.preference === "auto") return { source: clientUsable ? "client" : "host" };
  if (clientUsable) return { source: "client" };
  if (!input.serverAcceptsClientAudio) {
    return {
      source: null,
      reason:
        "This server does not accept microphone audio from the client. Switch voice input to the Construct host bridge.",
    };
  }
  if (!input.secureContext) {
    return {
      source: null,
      reason:
        "Browsers only allow microphone access on secure origins. Open T3 Code over HTTPS, or use the desktop app.",
    };
  }
  return { source: null, reason: "This browser does not expose a microphone." };
}

/** Whether this client can reach a microphone at all. */
export function canCaptureClientAudio(): boolean {
  return (
    typeof window !== "undefined" &&
    window.isSecureContext === true &&
    typeof navigator !== "undefined" &&
    typeof navigator.mediaDevices?.getUserMedia === "function"
  );
}

export function isSecureContext(): boolean {
  return typeof window !== "undefined" && window.isSecureContext === true;
}

/**
 * Resamples a stream of Float32 blocks to `outputRate`.
 *
 * Browsers may refuse an explicit 16 kHz `AudioContext` (Firefox and Safari
 * usually hand back the device rate), so the blocks arrive at whatever rate the
 * context runs at. The returned function keeps the fractional read position and
 * the previous block's last sample, so interpolation is continuous across block
 * boundaries instead of clicking every 100 ms.
 */
export function createLinearResampler(
  inputRate: number,
  outputRate: number,
): (block: Float32Array) => Float32Array {
  if (!(inputRate > 0) || !(outputRate > 0)) {
    throw new Error("Sample rates must be positive.");
  }
  const ratio = inputRate / outputRate;
  if (ratio === 1) return (block) => block;

  let previous = 0;
  // Read position for the next output sample, relative to the next block's
  // start. Always > -1, so at most one sample of history is needed.
  let position = 0;

  return (block) => {
    if (block.length === 0) return block;
    const limit = block.length - 1;
    const count = position > limit ? 0 : Math.floor((limit - position) / ratio) + 1;
    const output = new Float32Array(count);
    for (let index = 0; index < count; index += 1) {
      const read = position + index * ratio;
      const left = Math.floor(read);
      const fraction = read - left;
      const before = left < 0 ? previous : block[left]!;
      // Reading exactly the last sample has nothing to interpolate towards.
      const after = left + 1 <= limit ? block[left + 1]! : before;
      output[index] = before + (after - before) * fraction;
    }
    position = position + count * ratio - block.length;
    previous = block[limit]!;
    return output;
  };
}

/** Converts normalized float samples to the S16LE bytes the speech socket wants. */
export function floatToPcm16(samples: Float32Array): Uint8Array {
  const bytes = new Uint8Array(samples.length * 2);
  const view = new DataView(bytes.buffer);
  for (let index = 0; index < samples.length; index += 1) {
    const clamped = Math.max(-1, Math.min(1, samples[index]!));
    view.setInt16(index * 2, Math.round(clamped * 32_767), true);
  }
  return bytes;
}

/**
 * The level ring's value, using the same formula the server applies to host
 * audio so the ring behaves identically for both sources.
 */
export function computeAudioLevel(samples: Float32Array): number {
  if (samples.length === 0) return 0;
  let sumSquares = 0;
  for (let index = 0; index < samples.length; index += 1) {
    const sample = samples[index]!;
    sumSquares += sample * sample;
  }
  const rms = Math.sqrt(sumSquares / samples.length);
  return Math.min(1, Math.max(0, (rms - 0.006) * 10));
}

/** Turns a getUserMedia rejection into something worth showing a user. */
export function describeCaptureError(error: unknown): string {
  const name =
    typeof error === "object" && error !== null && "name" in error && typeof error.name === "string"
      ? error.name
      : "";
  switch (name) {
    case "NotAllowedError":
    case "SecurityError":
      return "Microphone access was denied.";
    case "NotFoundError":
    case "OverconstrainedError":
      return "No microphone found.";
    case "NotReadableError":
      return "The microphone is already in use by another application.";
    default:
      return error instanceof Error && error.message
        ? `Could not start the microphone: ${error.message}`
        : "Could not start the microphone.";
  }
}

/**
 * Batches render quanta into fixed-size blocks on the audio thread. Conversion
 * and resampling stay on the main thread so there is one tested implementation
 * of each, rather than a second copy inlined in this source string.
 */
const CAPTURE_WORKLET_SOURCE = `
class T3VoiceCaptureProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.size = options.processorOptions.blockSize;
    this.buffer = new Float32Array(this.size);
    this.filled = 0;
  }

  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (!channel) return true;
    for (let index = 0; index < channel.length; index += 1) {
      this.buffer[this.filled] = channel[index];
      this.filled += 1;
      if (this.filled === this.size) {
        this.port.postMessage(this.buffer.slice(0));
        this.filled = 0;
      }
    }
    return true;
  }
}

registerProcessor("t3-voice-capture", T3VoiceCaptureProcessor);
`;

export type ClientAudioCapture = {
  /** Idempotent: releases the microphone, the graph and the audio context. */
  stop(): void;
};

export type ClientAudioCaptureOptions = {
  readonly onChunk: (chunk: Uint8Array) => void;
  readonly onLevel: (level: number) => void;
  /** Called once with a user-facing reason; the capture is dead afterwards. */
  readonly onError: (message: string) => void;
};

/**
 * Starts capturing the microphone. Returns immediately — the permission prompt
 * and the audio graph come up in the background — so a caller can open its
 * server session in parallel.
 */
export function startClientAudioCapture(options: ClientAudioCaptureOptions): ClientAudioCapture {
  let stopped = false;
  let stream: MediaStream | null = null;
  let context: AudioContext | null = null;
  let sourceNode: MediaStreamAudioSourceNode | null = null;
  let workletNode: AudioWorkletNode | null = null;
  let moduleUrl: string | null = null;
  let lastLevelAt = 0;

  const release = () => {
    if (workletNode) {
      workletNode.port.close();
      workletNode.disconnect();
      workletNode = null;
    }
    sourceNode?.disconnect();
    sourceNode = null;
    for (const track of stream?.getTracks() ?? []) track.stop();
    stream = null;
    if (context && context.state !== "closed") void context.close().catch(() => {});
    context = null;
    if (moduleUrl) {
      URL.revokeObjectURL(moduleUrl);
      moduleUrl = null;
    }
  };

  const fail = (message: string) => {
    if (stopped) return;
    stopped = true;
    release();
    options.onError(message);
  };

  void (async () => {
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          channelCount: 1,
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true,
        },
      });
      if (stopped) return release();

      // Browsers that honour the hint hand back 16 kHz directly; the rest keep
      // the device rate and the resampler below deals with it.
      try {
        context = new AudioContext({ sampleRate: VOICE_SAMPLE_RATE });
      } catch {
        context = new AudioContext();
      }
      if (context.state === "suspended") await context.resume();
      if (stopped) return release();

      moduleUrl = URL.createObjectURL(
        new Blob([CAPTURE_WORKLET_SOURCE], { type: "text/javascript" }),
      );
      await context.audioWorklet.addModule(moduleUrl);
      if (stopped) return release();

      const resample = createLinearResampler(context.sampleRate, VOICE_SAMPLE_RATE);
      const blockSize = Math.max(128, Math.round((context.sampleRate * VOICE_CHUNK_MS) / 1000));
      sourceNode = context.createMediaStreamSource(stream);
      workletNode = new AudioWorkletNode(context, "t3-voice-capture", {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        channelCount: 1,
        processorOptions: { blockSize },
      });
      workletNode.port.addEventListener("message", (event) => {
        if (stopped) return;
        const samples = resample((event as MessageEvent<Float32Array>).data);
        if (samples.length === 0) return;
        const now = Date.now();
        if (now - lastLevelAt >= LEVEL_INTERVAL_MS) {
          lastLevelAt = now;
          options.onLevel(computeAudioLevel(samples));
        }
        options.onChunk(floatToPcm16(samples));
      });
      // A port reached through addEventListener stays paused until started.
      workletNode.port.start();
      sourceNode.connect(workletNode);
      // The processor writes nothing, so the destination stays silent; the
      // connection exists only so browsers keep pulling the node.
      workletNode.connect(context.destination);
      if (stopped) release();
    } catch (error) {
      fail(describeCaptureError(error));
    }
  })();

  return {
    stop: () => {
      if (stopped) return;
      stopped = true;
      release();
    },
  };
}
