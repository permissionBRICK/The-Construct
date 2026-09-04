import { describe, expect, it } from "vite-plus/test";

import { createVoiceAudioSender } from "./voiceAudioSender";

const controllableSend = () => {
  const sent: Array<number> = [];
  const settle: Array<() => void> = [];
  return {
    sent,
    settle,
    send: (chunk: Uint8Array) => {
      sent.push(chunk[0]!);
      return new Promise<void>((resolve) => settle.push(resolve));
    },
  };
};

const chunk = (id: number) => new Uint8Array([id]);

describe("createVoiceAudioSender", () => {
  it("sends chunks as they arrive while the link keeps up", async () => {
    const transport = controllableSend();
    const sender = createVoiceAudioSender({ send: transport.send, maxInFlight: 2 });

    sender.push(chunk(1));
    transport.settle.shift()!();
    await Promise.resolve();
    sender.push(chunk(2));

    expect(transport.sent).toEqual([1, 2]);
    expect(sender.droppedChunks).toBe(0);
  });

  it("holds chunks back once the in-flight bound is reached", () => {
    const transport = controllableSend();
    const sender = createVoiceAudioSender({ send: transport.send, maxInFlight: 2 });

    for (const id of [1, 2, 3, 4]) sender.push(chunk(id));

    expect(transport.sent).toEqual([1, 2]);
    expect(sender.droppedChunks).toBe(0);
  });

  it("drains the queue as sends settle, oldest first", async () => {
    const transport = controllableSend();
    const sender = createVoiceAudioSender({ send: transport.send, maxInFlight: 1 });

    for (const id of [1, 2, 3]) sender.push(chunk(id));
    expect(transport.sent).toEqual([1]);

    transport.settle.shift()!();
    await Promise.resolve();
    expect(transport.sent).toEqual([1, 2]);

    transport.settle.shift()!();
    await Promise.resolve();
    expect(transport.sent).toEqual([1, 2, 3]);
  });

  it("keeps draining after a failed send", async () => {
    const failures: Array<number> = [];
    const sent: Array<number> = [];
    const sender = createVoiceAudioSender({
      maxInFlight: 1,
      send: (audio) => {
        sent.push(audio[0]!);
        failures.push(audio[0]!);
        return Promise.reject(new Error("offline"));
      },
    });

    sender.push(chunk(1));
    sender.push(chunk(2));
    await Promise.resolve();
    await Promise.resolve();

    expect(sent).toEqual([1, 2]);
    expect(failures).toEqual([1, 2]);
  });

  it("drops the oldest waiting chunk instead of growing without bound", () => {
    const transport = controllableSend();
    const sender = createVoiceAudioSender({
      send: transport.send,
      maxInFlight: 1,
      maxPending: 2,
    });

    for (const id of [1, 2, 3, 4, 5]) sender.push(chunk(id));

    expect(transport.sent).toEqual([1]);
    expect(sender.droppedChunks).toBe(2);

    transport.settle.shift()!();
    return Promise.resolve().then(() => {
      // 2 and 3 were dropped; the newest audio is what still matters.
      expect(transport.sent).toEqual([1, 4]);
    });
  });

  it("ignores pushes after stopping and forgets what was queued", async () => {
    const transport = controllableSend();
    const sender = createVoiceAudioSender({ send: transport.send, maxInFlight: 1 });

    sender.push(chunk(1));
    sender.push(chunk(2));
    sender.stop();
    sender.push(chunk(3));

    transport.settle.shift()!();
    await Promise.resolve();

    expect(transport.sent).toEqual([1]);
  });
});
