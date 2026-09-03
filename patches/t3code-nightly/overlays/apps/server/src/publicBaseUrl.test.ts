import { describe, expect, it } from "@effect/vitest";

import { parsePublicBaseUrl } from "./publicBaseUrl.ts";

describe("parsePublicBaseUrl", () => {
  it("treats absent and blank values as unset", () => {
    expect(parsePublicBaseUrl(undefined)).toEqual({ kind: "unset" });
    expect(parsePublicBaseUrl("")).toEqual({ kind: "unset" });
    expect(parsePublicBaseUrl("   ")).toEqual({ kind: "unset" });
  });

  it("keeps the origin and drops everything below it", () => {
    expect(parsePublicBaseUrl("https://agent-vm.mshome.net:5178")).toEqual({
      kind: "ok",
      origin: "https://agent-vm.mshome.net:5178",
    });
    expect(parsePublicBaseUrl("  https://agent-vm.mshome.net:5178/pair?x=1#y  ")).toEqual({
      kind: "ok",
      origin: "https://agent-vm.mshome.net:5178",
    });
    expect(parsePublicBaseUrl("http://agent-vm.mshome.net:5177")).toEqual({
      kind: "ok",
      origin: "http://agent-vm.mshome.net:5177",
    });
  });

  it("reports values it cannot use instead of guessing", () => {
    expect(parsePublicBaseUrl("agent-vm.mshome.net:5178")).toEqual({
      kind: "invalid",
      raw: "agent-vm.mshome.net:5178",
    });
    expect(parsePublicBaseUrl("ws://agent-vm.mshome.net:5178")).toEqual({
      kind: "invalid",
      raw: "ws://agent-vm.mshome.net:5178",
    });
    expect(parsePublicBaseUrl("not a url")).toEqual({ kind: "invalid", raw: "not a url" });
  });
});
