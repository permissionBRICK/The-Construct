import * as Schema from "effect/Schema";

/**
 * Where the microphone audio for a voice session comes from. `host` records on
 * the server through Construct's recorder bridge; `client` means the connected
 * client captures the microphone itself and pushes PCM with `voiceInput.audio`.
 */
export const VoiceInputSource = Schema.Literals(["client", "host"]);
export type VoiceInputSource = typeof VoiceInputSource.Type;

/** Absent `source` means `host`: older clients only know the recorder bridge. */
export const DEFAULT_VOICE_INPUT_SOURCE: VoiceInputSource = "host";

export const VoiceInputStartInput = Schema.Struct({
  sessionId: Schema.String,
  source: Schema.optional(VoiceInputSource),
});
export type VoiceInputStartInput = typeof VoiceInputStartInput.Type;

/**
 * Upper bound for one pushed audio chunk. Clients send ~100 ms of 16 kHz mono
 * S16LE (3200 bytes); the bound keeps a hostile client from spending server
 * memory on a single base64 payload while leaving room for slower browsers that
 * batch a few buffers together.
 */
export const VOICE_INPUT_MAX_CHUNK_BYTES = 64 * 1024;

export const VoiceInputAudioInput = Schema.Struct({
  sessionId: Schema.String,
  chunk: Schema.Uint8ArrayFromBase64.check(Schema.isMaxLength(VOICE_INPUT_MAX_CHUNK_BYTES)),
});
export type VoiceInputAudioInput = typeof VoiceInputAudioInput.Type;

export const VoiceInputAudioResult = Schema.Struct({
  accepted: Schema.Boolean,
});
export type VoiceInputAudioResult = typeof VoiceInputAudioResult.Type;

export const VoiceInputStopInput = Schema.Struct({
  sessionId: Schema.String,
});
export type VoiceInputStopInput = typeof VoiceInputStopInput.Type;

export const VoiceInputStopResult = Schema.Struct({
  stopped: Schema.Boolean,
});
export type VoiceInputStopResult = typeof VoiceInputStopResult.Type;

export const VoiceInputStreamEvent = Schema.Union([
  Schema.Struct({ type: Schema.Literal("listening") }),
  Schema.Struct({
    type: Schema.Literal("level"),
    value: Schema.Number.check(Schema.isBetween({ minimum: 0, maximum: 1 })),
  }),
  Schema.Struct({
    type: Schema.Literal("transcript"),
    text: Schema.String,
    final: Schema.Boolean,
  }),
  Schema.Struct({ type: Schema.Literal("stopped") }),
]);
export type VoiceInputStreamEvent = typeof VoiceInputStreamEvent.Type;

export class VoiceInputError extends Schema.TaggedErrorClass<VoiceInputError>()("VoiceInputError", {
  message: Schema.String,
  fatal: Schema.optional(Schema.Boolean),
}) {}
