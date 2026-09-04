import {
  WS_METHODS,
  type EnvironmentId,
  type VoiceInputSource,
  type VoiceInputStreamEvent,
} from "@t3tools/contracts";
import { request, runStream } from "@t3tools/client-runtime/rpc";
import {
  createRuntimeCommand,
  runInEnvironment,
  runStreamInEnvironment,
} from "@t3tools/client-runtime/state/runtime";
import * as Effect from "effect/Effect";
import * as Stream from "effect/Stream";

import { connectionAtomRuntime } from "../connection/runtime";

export const startVoiceInput = createRuntimeCommand(connectionAtomRuntime, {
  label: "voice-input:start",
  execute: (target: {
    readonly environmentId: EnvironmentId;
    readonly sessionId: string;
    /** Absent leaves the choice to the server, which records on the host. */
    readonly source?: VoiceInputSource;
    readonly onEvent: (event: VoiceInputStreamEvent) => void;
  }) =>
    runStreamInEnvironment(
      target.environmentId,
      runStream(WS_METHODS.voiceInputStart, {
        sessionId: target.sessionId,
        ...(target.source === undefined ? {} : { source: target.source }),
      }),
    ).pipe(Stream.runForEach((event) => Effect.sync(() => target.onEvent(event)))),
});

export const stopVoiceInput = createRuntimeCommand(connectionAtomRuntime, {
  label: "voice-input:stop",
  execute: (target: { readonly environmentId: EnvironmentId; readonly sessionId: string }) =>
    runInEnvironment(
      target.environmentId,
      request(WS_METHODS.voiceInputStop, { sessionId: target.sessionId }),
    ),
});

/**
 * Pushes one chunk of client-captured microphone audio. The server answers
 * `{ accepted: false }` for a session it does not own or that records on the
 * host, so a stale sender cannot write into anyone else's transcript.
 */
export const sendVoiceAudio = createRuntimeCommand(connectionAtomRuntime, {
  label: "voice-input:audio",
  execute: (target: {
    readonly environmentId: EnvironmentId;
    readonly sessionId: string;
    readonly chunk: Uint8Array;
  }) =>
    runInEnvironment(
      target.environmentId,
      request(WS_METHODS.voiceInputAudio, {
        sessionId: target.sessionId,
        chunk: target.chunk,
      }),
    ),
});
