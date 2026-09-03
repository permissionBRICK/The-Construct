/**
 * Paces client-captured audio chunks onto the wire.
 *
 * The microphone produces a chunk every ~100 ms whether or not the link keeps
 * up, so the sender caps how much audio can be outstanding at once and drops
 * the oldest waiting chunk instead of growing a queue forever. Late audio is
 * worthless to a live transcript anyway.
 */

/** Chunks handed to the transport at the same time. */
export const MAX_IN_FLIGHT_VOICE_CHUNKS = 8;
/** Chunks allowed to wait behind them (~0.8 s of audio) before the oldest goes. */
export const MAX_PENDING_VOICE_CHUNKS = 8;

export type VoiceAudioSender = {
  push(chunk: Uint8Array): void;
  /** Drops everything still waiting; in-flight sends are left to settle. */
  stop(): void;
  /** Chunks dropped because the link could not keep up. */
  readonly droppedChunks: number;
};

export function createVoiceAudioSender(options: {
  readonly send: (chunk: Uint8Array) => Promise<unknown>;
  readonly maxInFlight?: number;
  readonly maxPending?: number;
}): VoiceAudioSender {
  const maxInFlight = options.maxInFlight ?? MAX_IN_FLIGHT_VOICE_CHUNKS;
  const maxPending = options.maxPending ?? MAX_PENDING_VOICE_CHUNKS;
  const pending: Array<Uint8Array> = [];
  let inFlight = 0;
  let dropped = 0;
  let stopped = false;

  const pump = () => {
    if (stopped) return;
    while (inFlight < maxInFlight && pending.length > 0) {
      const chunk = pending.shift()!;
      inFlight += 1;
      void options.send(chunk).then(
        () => {
          inFlight -= 1;
          pump();
        },
        () => {
          // A failed push is not worth surfacing on its own: the session's own
          // stream reports whatever the server made of the audio it did get.
          inFlight -= 1;
          pump();
        },
      );
    }
  };

  return {
    push: (chunk) => {
      if (stopped) return;
      pending.push(chunk);
      while (pending.length > maxPending) {
        pending.shift();
        dropped += 1;
      }
      pump();
    },
    stop: () => {
      stopped = true;
      pending.length = 0;
    },
    get droppedChunks() {
      return dropped;
    },
  };
}
