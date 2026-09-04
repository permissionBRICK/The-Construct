// @effect-diagnostics nodeBuiltinImport:off globalTimers:off globalDate:off - this module is the
// callback boundary for a native recorder and the ws client; their lifetimes are
// still scope-bound by the Effect stream returned at the bottom of the file.
import {
  VoiceInputError,
  type VoiceInputSource,
  type VoiceInputStreamEvent,
} from "@t3tools/contracts";
import * as Effect from "effect/Effect";
import * as Queue from "effect/Queue";
import * as Stream from "effect/Stream";
import * as NodeChildProcess from "node:child_process";
import * as NodeFS from "node:fs";
import * as NodeOS from "node:os";
import * as NodePath from "node:path";
import type * as NodeStream from "node:stream";
import WebSocket from "ws";

const VOICE_STREAM_URL = "wss://api.anthropic.com/api/ws/speech_to_text/voice_stream";
const KEEP_ALIVE_MS = 8_000;
const SILENCE_TIMEOUT_MS = 15_000;
const MAX_RECORDING_MS = 120_000;
const CLOSE_GRACE_MS = 3_000;
const LEVEL_INTERVAL_MS = 75;
export const FIRST_CLIENT_CHUNK_TIMEOUT_MS = 8_000;
export const NO_CLIENT_AUDIO_MESSAGE = "No microphone audio arrived from the client.";
export const HOST_BRIDGE_UNAVAILABLE_MESSAGE =
  "The host microphone bridge is unavailable. Enable microphone passthrough in Construct and keep its VS Code extension running.";
const KEYTERMS = [
  "VS Code",
  "IDE",
  "webview",
  "IntelliSense",
  "MCP",
  "symlink",
  "grep",
  "regex",
  "localhost",
  "codebase",
  "TypeScript",
  "JSON",
  "OAuth",
  "webhook",
  "gRPC",
  "dotfiles",
  "subagent",
  "worktree",
].join(",");

type VoiceSessionCallbacks = {
  readonly event: (event: VoiceInputStreamEvent) => void;
  readonly error: (error: VoiceInputError) => void;
  readonly complete: () => void;
};

/** How a source hands audio and failures back to the session that owns it. */
export type VoiceAudioSourceHandlers = {
  /** 16 kHz mono S16LE PCM headed for the transcription socket. */
  readonly audio: (chunk: Buffer) => void;
  /** Fatal: the session ends and the client is told why. */
  readonly fail: (message: string) => void;
};

/**
 * Where a session's microphone audio comes from. `start` runs once the
 * transcription socket is open, so a source never produces audio that would be
 * dropped on the floor.
 */
export interface VoiceAudioSource {
  readonly kind: VoiceInputSource;
  /** Whether the server derives the level ring from the audio it sees. */
  readonly emitsLevels: boolean;
  start(handlers: VoiceAudioSourceHandlers): void;
  stop(): void;
  /** Takes a chunk pushed by a client; false when this source has no use for it. */
  push(chunk: Uint8Array): boolean;
}

/** Records on the server through Construct's `rec` shim. */
class HostAudioSource implements VoiceAudioSource {
  readonly kind = "host" as const;
  readonly emitsLevels = true;
  private recorder: NodeChildProcess.ChildProcessByStdio<
    null,
    NodeStream.Readable,
    NodeStream.Readable
  > | null = null;
  private stopped = false;

  start(handlers: VoiceAudioSourceHandlers): void {
    const recorder = NodeChildProcess.spawn(
      "rec",
      [
        "-q",
        "--buffer",
        "1024",
        "-t",
        "raw",
        "-r",
        "16000",
        "-e",
        "signed",
        "-b",
        "16",
        "-c",
        "1",
        "-",
      ],
      { stdio: ["ignore", "pipe", "pipe"] },
    );
    this.recorder = recorder;
    recorder.stdout.on("data", (chunk: Buffer) => handlers.audio(chunk));
    recorder.on("error", (error) =>
      handlers.fail(`Could not start the recorder: ${error.message}`),
    );
    recorder.on("exit", (code) => {
      if (!this.stopped && code !== 0) handlers.fail(HOST_BRIDGE_UNAVAILABLE_MESSAGE);
    });
  }

  stop(): void {
    this.stopped = true;
    this.recorder?.kill("SIGTERM");
    this.recorder = null;
  }

  push(): boolean {
    return false;
  }
}

/** Takes microphone audio the connected client captured and pushed over RPC. */
class ClientAudioSource implements VoiceAudioSource {
  readonly kind = "client" as const;
  readonly emitsLevels = false;
  private handlers: VoiceAudioSourceHandlers | null = null;
  private firstChunkTimer: NodeJS.Timeout | null = null;

  start(handlers: VoiceAudioSourceHandlers): void {
    this.handlers = handlers;
    // A client that never gets microphone permission would otherwise sit in a
    // silent "listening" state until the 15 s silence timer ends it with no
    // explanation at all.
    this.firstChunkTimer = setTimeout(
      () => handlers.fail(NO_CLIENT_AUDIO_MESSAGE),
      FIRST_CLIENT_CHUNK_TIMEOUT_MS,
    );
  }

  stop(): void {
    if (this.firstChunkTimer) clearTimeout(this.firstChunkTimer);
    this.firstChunkTimer = null;
    this.handlers = null;
  }

  push(chunk: Uint8Array): boolean {
    // Chunks that race ahead of the transcription socket are dropped: the
    // client only starts pushing once it sees `listening`.
    const handlers = this.handlers;
    if (!handlers || chunk.byteLength === 0) return false;
    if (this.firstChunkTimer) {
      clearTimeout(this.firstChunkTimer);
      this.firstChunkTimer = null;
    }
    handlers.audio(Buffer.from(chunk.buffer, chunk.byteOffset, chunk.byteLength));
    return true;
  }
}

export function createVoiceAudioSource(source: VoiceInputSource | undefined): VoiceAudioSource {
  return source === "client" ? new ClientAudioSource() : new HostAudioSource();
}

function readClaudeAccessToken(): string {
  const configDirectory =
    process.env.CLAUDE_CONFIG_DIR?.trim() || NodePath.join(NodeOS.homedir(), ".claude");
  const credentialsPath = NodePath.join(configDirectory, ".credentials.json");
  let decoded: unknown;
  try {
    decoded = JSON.parse(NodeFS.readFileSync(credentialsPath, "utf8"));
  } catch {
    throw new Error(
      `Claude credentials were not readable at ${credentialsPath}. Sign in with claude first.`,
    );
  }
  const token =
    typeof decoded === "object" &&
    decoded !== null &&
    "claudeAiOauth" in decoded &&
    typeof decoded.claudeAiOauth === "object" &&
    decoded.claudeAiOauth !== null &&
    "accessToken" in decoded.claudeAiOauth &&
    typeof decoded.claudeAiOauth.accessToken === "string"
      ? decoded.claudeAiOauth.accessToken.trim()
      : "";
  if (!token)
    throw new Error(
      "Claude OAuth credentials do not contain an access token. Sign in with claude first.",
    );
  return token;
}

function voiceStreamUrl(): string {
  const query = new URLSearchParams({
    encoding: "linear16",
    sample_rate: "16000",
    channels: "1",
    endpointing_ms: "300",
    utterance_end_ms: "1000",
    language: process.env.CLAUDE_CODE_VOICE_LANGUAGE?.trim() || "en",
    use_conversation_engine: "true",
    forward_interims: "typed",
  });
  return `${VOICE_STREAM_URL}?${query.toString()}`;
}

class VoiceSession {
  readonly id: string;
  private readonly callbacks: VoiceSessionCallbacks;
  private readonly source: VoiceAudioSource;
  private socket: WebSocket | null = null;
  private keepAlive: NodeJS.Timeout | null = null;
  private silenceTimer: NodeJS.Timeout | null = null;
  private maximumTimer: NodeJS.Timeout | null = null;
  private closeTimer: NodeJS.Timeout | null = null;
  private committed: string[] = [];
  private interim = "";
  private lastLevelAt = 0;
  private stopping = false;
  private ended = false;

  constructor(id: string, source: VoiceAudioSource, callbacks: VoiceSessionCallbacks) {
    this.id = id;
    this.source = source;
    this.callbacks = callbacks;
  }

  /** Returns false when this session has no use for client-pushed audio. */
  pushAudio(chunk: Uint8Array): boolean {
    if (this.stopping || this.ended) return false;
    return this.source.push(chunk);
  }

  start(): void {
    const token = readClaudeAccessToken();
    const socket = new WebSocket(voiceStreamUrl(), {
      headers: {
        Authorization: `Bearer ${token}`,
        "x-app": "vscode",
        "anthropic-client-platform": "claude_code_vscode",
        "x-config-keyterms": KEYTERMS,
      },
    });
    this.socket = socket;

    socket.on("open", () => {
      if (this.stopping) {
        socket.send(JSON.stringify({ type: "CloseStream" }));
        return;
      }
      this.callbacks.event({ type: "listening" });
      socket.send(JSON.stringify({ type: "KeepAlive" }));
      this.source.start({
        audio: (chunk) => {
          if (this.stopping) return;
          if (this.source.emitsLevels) this.emitAudioLevel(chunk);
          if (socket.readyState === WebSocket.OPEN) socket.send(chunk);
        },
        fail: (message) => this.fail(message, true),
      });
      this.keepAlive = setInterval(() => {
        if (socket.readyState === WebSocket.OPEN)
          socket.send(JSON.stringify({ type: "KeepAlive" }));
      }, KEEP_ALIVE_MS);
      this.maximumTimer = setTimeout(() => this.stop(), MAX_RECORDING_MS);
      this.resetSilenceTimer();
    });

    socket.on("message", (raw) => {
      let message: { type?: string; data?: string; description?: string; message?: string };
      try {
        message = JSON.parse(raw.toString()) as typeof message;
      } catch {
        return;
      }
      if (message.type === "TranscriptInterim" || message.type === "TranscriptText") {
        if (message.data) {
          this.interim = message.data;
          this.callbacks.event({ type: "transcript", text: this.fullTranscript(), final: false });
          this.resetSilenceTimer();
        }
        return;
      }
      if (message.type === "TranscriptEndpoint") {
        const segment = this.interim.trim();
        if (segment) this.committed.push(segment);
        this.interim = "";
        this.callbacks.event({ type: "transcript", text: this.fullTranscript(), final: true });
        this.resetSilenceTimer();
        return;
      }
      if (message.type === "TranscriptError") {
        this.fail(message.description || "Claude voice transcription failed.", true);
      } else if (message.type === "error") {
        this.fail(message.message || "Claude voice transcription failed.", true);
      }
    });
    socket.on("error", (error) => {
      if (!this.stopping) this.fail(`Claude voice WebSocket error: ${error.message}`, true);
    });
    socket.on("close", () => this.finish());
  }

  stop(): void {
    if (this.stopping || this.ended) return;
    this.stopping = true;
    this.source.stop();
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify({ type: "CloseStream" }));
      this.closeTimer = setTimeout(() => this.finish(), CLOSE_GRACE_MS);
    } else {
      this.finish();
    }
  }

  abort(): void {
    this.stopping = true;
    this.finish(false);
  }

  private fullTranscript(): string {
    return [...this.committed, this.interim.trim()].filter(Boolean).join(" ");
  }

  private emitAudioLevel(chunk: Buffer): void {
    const now = Date.now();
    if (now - this.lastLevelAt < LEVEL_INTERVAL_MS) return;
    this.lastLevelAt = now;
    let sumSquares = 0;
    let sampleCount = 0;
    for (let offset = 0; offset + 1 < chunk.length; offset += 2) {
      const sample = chunk.readInt16LE(offset) / 32_768;
      sumSquares += sample * sample;
      sampleCount += 1;
    }
    const rms = sampleCount > 0 ? Math.sqrt(sumSquares / sampleCount) : 0;
    const value = Math.min(1, Math.max(0, (rms - 0.006) * 10));
    this.callbacks.event({ type: "level", value });
  }

  private resetSilenceTimer(): void {
    if (this.silenceTimer) clearTimeout(this.silenceTimer);
    this.silenceTimer = setTimeout(() => this.stop(), SILENCE_TIMEOUT_MS);
  }

  private fail(message: string, fatal: boolean): void {
    if (this.ended) return;
    this.callbacks.error(new VoiceInputError({ message, fatal }));
    this.abort();
  }

  private finish(notify = true): void {
    if (this.ended) return;
    this.ended = true;
    this.source.stop();
    if (this.socket && this.socket.readyState !== WebSocket.CLOSED) this.socket.terminate();
    this.socket = null;
    for (const timer of [this.keepAlive, this.silenceTimer, this.maximumTimer, this.closeTimer]) {
      if (timer) clearTimeout(timer);
    }
    this.keepAlive = this.silenceTimer = this.maximumTimer = this.closeTimer = null;
    activeVoiceSessions.delete(this.id);
    if (notify) this.callbacks.complete();
  }
}

const activeVoiceSessions = new Map<string, VoiceSession>();

/**
 * Routes one pushed chunk to its session. Unknown ids and host-source sessions
 * are rejected quietly: a client that pushes into a session it does not own
 * learns nothing beyond "not accepted".
 */
export function routeVoiceAudio(
  sessions: ReadonlyMap<string, { pushAudio: (chunk: Uint8Array) => boolean }>,
  sessionId: string,
  chunk: Uint8Array,
): boolean {
  return sessions.get(sessionId)?.pushAudio(chunk) ?? false;
}

export function pushVoiceAudio(
  sessionId: string,
  chunk: Uint8Array,
): Effect.Effect<{ accepted: boolean }> {
  return Effect.sync(() => ({ accepted: routeVoiceAudio(activeVoiceSessions, sessionId, chunk) }));
}

export function stopVoiceInput(sessionId: string): Effect.Effect<{ stopped: boolean }> {
  return Effect.sync(() => {
    const session = activeVoiceSessions.get(sessionId);
    session?.stop();
    return { stopped: session !== undefined };
  });
}

export function startVoiceInput(
  sessionId: string,
  source: VoiceInputSource | undefined,
): Stream.Stream<VoiceInputStreamEvent, VoiceInputError> {
  return Stream.callback<VoiceInputStreamEvent, VoiceInputError>((queue) =>
    Effect.acquireRelease(
      Effect.sync(() => {
        for (const active of activeVoiceSessions.values()) active.stop();
        const session = new VoiceSession(sessionId, createVoiceAudioSource(source), {
          event: (event) => void Effect.runFork(Queue.offer(queue, event)),
          error: (error) => void Effect.runFork(Queue.fail(queue, error)),
          complete: () =>
            void Effect.runFork(
              Queue.offer(queue, { type: "stopped" }).pipe(Effect.andThen(Queue.end(queue))),
            ),
        });
        activeVoiceSessions.set(sessionId, session);
        try {
          session.start();
        } catch (error) {
          session.abort();
          const message = error instanceof Error ? error.message : String(error);
          void Effect.runFork(Queue.fail(queue, new VoiceInputError({ message, fatal: true })));
        }
        return session;
      }),
      (session) => Effect.sync(() => session.stop()),
    ).pipe(Effect.forkScoped),
  );
}
