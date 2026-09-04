// @effect-diagnostics nodeBuiltinImport:off globalTimers:off - a throwaway upstream HTTP server stands in for omniloop.
import { NodeHttpServer } from "@effect/platform-node";
import { describe, expect, it } from "@effect/vitest";
import * as Effect from "effect/Effect";
import * as Layer from "effect/Layer";
import { FetchHttpClient, HttpBody, HttpClient, HttpRouter, HttpServer } from "effect/unstable/http";
import * as NodeFS from "node:fs";
import * as NodeHttp from "node:http";
import * as NodeOS from "node:os";
import * as NodePath from "node:path";

import { CONSTRUCT_OMNILOOP_PROXY_PREFIX, omniloopTicketStore } from "./constructOmniloop.ts";
import { constructOmniloopProxyRouteLayer } from "./constructOmniloopProxy.ts";

/** A stand-in omniloop daemon with the routes the dashboard uses. */
function startFakeOmniloop(): Promise<{ port: number; close: () => void; seen: NodeHttp.IncomingMessage[] }> {
  const seen: NodeHttp.IncomingMessage[] = [];
  const server = NodeHttp.createServer((request, response) => {
    seen.push(request);
    const url = new URL(request.url ?? "/", "http://fake");
    if (url.pathname === "/gui/") {
      response.writeHead(302, { Location: "/gui/index.html" + url.search });
      response.end();
    } else if (url.pathname === "/gui/index.html") {
      response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
      response.end(`<script src="/gui/app.js" defer></script><p>${url.search}</p>`);
    } else if (url.pathname === "/gui/app.js") {
      response.writeHead(200, { "Content-Type": "application/javascript" });
      response.end("fetch('/api/workflows'); new EventSource('/sse/workflows');");
    } else if (url.pathname === "/api/workflows/wf_a") {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ workflow: { id: "wf_a", name: "review", status: "running" } }));
    } else if (url.pathname === "/sse/workflows") {
      response.writeHead(200, { "Content-Type": "text/event-stream" });
      response.write("event: hello\ndata: 1\n\n");
      setTimeout(() => response.end("event: bye\ndata: 2\n\n"), 20);
    } else if (url.pathname === "/api/admin/pause" && request.method === "POST") {
      let body = "";
      request.on("data", (chunk) => (body += chunk));
      request.on("end", () => {
        response.writeHead(200, { "Content-Type": "application/json" });
        response.end(JSON.stringify({ body, auth: request.headers.authorization ?? null }));
      });
    } else {
      response.writeHead(404, { "Content-Type": "application/json" });
      response.end('{"error":"not_found"}');
    }
  });
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      resolve({ port, close: () => server.close(), seen });
    });
  });
}

describe("constructOmniloopProxyRouteLayer", () => {
  it.effect("proxies the dashboard under a ticket and rewrites its absolute references", () =>
    Effect.scoped(
      Effect.gen(function* () {
        const upstream = yield* Effect.promise(startFakeOmniloop);
        const home = NodeFS.mkdtempSync(NodePath.join(NodeOS.tmpdir(), "omniloop-proxy-test-"));
        NodeFS.writeFileSync(NodePath.join(home, "config.toml"), `port = ${upstream.port}\ntoken = "ol_cfg_test"\n`);
        const previous = { home: process.env.OMNILOOP_HOME, port: process.env.OMNILOOP_PORT };
        process.env.OMNILOOP_HOME = home;
        delete process.env.OMNILOOP_PORT;
        yield* Effect.addFinalizer(() =>
          Effect.sync(() => {
            upstream.close();
            NodeFS.rmSync(home, { recursive: true, force: true });
            if (previous.home === undefined) delete process.env.OMNILOOP_HOME;
            else process.env.OMNILOOP_HOME = previous.home;
            if (previous.port !== undefined) process.env.OMNILOOP_PORT = previous.port;
          }),
        );

        yield* HttpRouter.serve(constructOmniloopProxyRouteLayer, {
          disableListenLog: true,
          disableLogger: true,
        }).pipe(Layer.build);
        // The route (like the real server) forwards with the fetch-based client, so the
        // test also addresses the router by its real port instead of the test-bound client.
        const address = (yield* HttpServer.HttpServer).address;
        const origin = address._tag === "TcpAddress" ? `http://127.0.0.1:${address.port}` : "";
        const httpClient = yield* HttpClient.HttpClient;
        const { ticket } = omniloopTicketStore.mint();
        const base = `${origin}${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/${ticket}`;
        const proxyBase = `${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/${ticket}`;

        // The dashboard entry point, with its script tag pointed back at the proxy.
        const index = yield* httpClient.get(`${base}/gui/index.html?token=ol_cfg_test`);
        expect(index.status).toBe(200);
        expect(index.headers["content-type"]).toContain("text/html");
        const indexBody = yield* index.text;
        expect(indexBody).toContain(`src="${proxyBase}/gui/app.js"`);
        expect(indexBody).toContain("?token=ol_cfg_test");

        const app = yield* httpClient.get(`${base}/gui/app.js`);
        expect(yield* app.text).toBe(`fetch('${proxyBase}/api/workflows'); new EventSource('${proxyBase}/sse/workflows');`);

        // The daemon's `/gui/` redirect is followed on the server side, so the browser
        // gets the (rewritten) entry point straight away.
        const redirected = yield* httpClient.get(`${base}/gui/`);
        expect(redirected.status).toBe(200);
        expect(yield* redirected.text).toContain(`src="${proxyBase}/gui/app.js"`);

        // JSON and event streams pass through untouched.
        const detail = yield* httpClient.get(`${base}/api/workflows/wf_a`);
        expect(detail.status).toBe(200);
        expect((yield* detail.json) as unknown).toEqual({
          workflow: { id: "wf_a", name: "review", status: "running" },
        });
        const stream = yield* httpClient.get(`${base}/sse/workflows`);
        expect(stream.headers["content-type"]).toContain("text/event-stream");
        expect(yield* stream.text).toBe("event: hello\ndata: 1\n\nevent: bye\ndata: 2\n\n");

        // Admin actions forward the body and the dashboard's own bearer token.
        const admin = yield* httpClient.post(`${base}/api/admin/pause`, {
          headers: { authorization: "Bearer ol_cfg_test" },
          body: HttpBody.text('{"workflow_id":"wf_a"}', "application/json"),
        });
        expect((yield* admin.json) as unknown).toEqual({
          body: '{"workflow_id":"wf_a"}',
          auth: "Bearer ol_cfg_test",
        });

        // No ticket, unknown ticket: nothing reaches the daemon.
        const before = upstream.seen.length;
        expect((yield* httpClient.get(`${origin}${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/nope/gui/index.html`)).status).toBe(404);
        expect((yield* httpClient.get(`${origin}${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/${"x".repeat(32)}/api/workflows/wf_a`)).status).toBe(404);
        expect(upstream.seen.length).toBe(before);
      }),
    // Merged right-to-left: the fetch client replaces the test-bound HttpClient of
    // layerTest, for the route's upstream calls and for the assertions alike.
    ).pipe(Effect.provide(Layer.mergeAll(NodeHttpServer.layerTest, FetchHttpClient.layer))),
  );
});
