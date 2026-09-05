// @effect-diagnostics nodeBuiltinImport:off - integration test filesystem fixtures.
import { afterEach, expect, it, vi } from "vite-plus/test";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { hostname, tmpdir } from "node:os";
import { join } from "node:path";
import * as NodeServices from "@effect/platform-node/NodeServices";
import * as Effect from "effect/Effect";
import * as Layer from "effect/Layer";
import { HttpRouter } from "effect/unstable/http";
import * as ServerConfig from "./config.ts";
import { staticAndDevRouteLayer } from "./http.ts";

afterEach(() => vi.unstubAllEnvs());

it("serves VM-specific installation names without altering static metadata or app identity", async () => {
  const directory = await mkdtemp(join(tmpdir(), "construct-pwa-"));
  const original = await readFile(
    new URL("../../web/public/manifest.webmanifest", import.meta.url),
    "utf8",
  );
  await writeFile(join(directory, "manifest.webmanifest"), original);
  await writeFile(join(directory, "index.html"), "<title>T3</title>");
  const config = Layer.effect(
    ServerConfig.ServerConfig,
    Effect.map(ServerConfig.ServerConfig, (value) => ({ ...value, staticDir: directory })),
  ).pipe(Layer.provide(ServerConfig.layerTest(directory, directory)));
  const app = HttpRouter.toWebHandler(
    staticAndDevRouteLayer.pipe(Layer.provideMerge(config), Layer.provideMerge(NodeServices.layer)),
    { disableLogger: true },
  );
  try {
    for (const [configured, expected] of [
      ["haus-vm", "haus-vm"],
      ["work-vm", "work-vm"],
      ["  home-vm  ", "home-vm"],
      ["", hostname()],
      [undefined, hostname()],
      ['test "VM"', 'test "VM"'],
    ]) {
      vi.stubEnv("CONSTRUCT_INSTANCE_NAME", configured);
      const response = await app.handler(new Request("https://t3.example/manifest.webmanifest"));
      expect(response.status).toBe(200);
      expect(response.headers.get("content-type")).toContain("application/manifest+json");
      expect(response.headers.get("cache-control")).toBe("no-cache");
      const manifest = await response.json();
      expect(manifest).toEqual({
        ...JSON.parse(original),
        name: `T3 Code (${expected})`,
        short_name: `T3 ${expected}`,
      });
      // A conditional fetch must not return a 304 for an old VM label.
      const conditional = await app.handler(
        new Request("https://t3.example/manifest.webmanifest", {
          headers: { "If-None-Match": "*", "If-Modified-Since": "Wed, 01 Jan 2099 00:00:00 GMT" },
        }),
      );
      expect(conditional.status).toBe(200);
    }
    expect(await readFile(join(directory, "manifest.webmanifest"), "utf8")).toBe(original);
    const page = await app.handler(new Request("https://t3.example/"));
    expect(await page.text()).toBe("<title>T3</title>");
  } finally {
    await app.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});
