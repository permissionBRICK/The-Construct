import { VoiceInputError } from "@t3tools/contracts";
import * as Cause from "effect/Cause";
import { describe, expect, it } from "vite-plus/test";

import {
  CLIENT_VOICE_FAILURE_DESCRIPTION,
  describeVoiceInputFailure,
  HOST_VOICE_FAILURE_DESCRIPTION,
} from "./voiceInputFailure";

describe("describeVoiceInputFailure", () => {
  it("keeps the server's reason for a client session that never sent audio", () => {
    const cause = Cause.fail(
      new VoiceInputError({
        message: "No microphone audio arrived from the client.",
        fatal: true,
      }),
    );
    expect(describeVoiceInputFailure(cause, "client")).toBe(
      "No microphone audio arrived from the client.",
    );
  });

  it("keeps the server's reason for an unavailable host bridge", () => {
    const cause = Cause.fail(
      new VoiceInputError({
        message:
          "The host microphone bridge is unavailable. Enable microphone passthrough in Construct and keep its VS Code extension running.",
        fatal: true,
      }),
    );
    expect(describeVoiceInputFailure(cause, "host")).toContain("host microphone bridge");
  });

  it("falls back to a source-specific hint when the failure carries no reason", () => {
    expect(describeVoiceInputFailure(Cause.die(new Error("boom")), "client")).toBe(
      CLIENT_VOICE_FAILURE_DESCRIPTION,
    );
    expect(describeVoiceInputFailure(Cause.interrupt(), "host")).toBe(
      HOST_VOICE_FAILURE_DESCRIPTION,
    );
    expect(describeVoiceInputFailure(Cause.fail({ code: 42 }), "host")).toBe(
      HOST_VOICE_FAILURE_DESCRIPTION,
    );
    expect(
      describeVoiceInputFailure(Cause.fail(new VoiceInputError({ message: "   " })), "client"),
    ).toBe(CLIENT_VOICE_FAILURE_DESCRIPTION);
  });
});
