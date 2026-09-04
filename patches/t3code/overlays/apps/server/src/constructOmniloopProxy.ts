// @effect-diagnostics globalDate:off - ticket expiry is wall-clock time.
import * as Effect from "effect/Effect";
import {
  HttpClient,
  HttpClientRequest,
  HttpRouter,
  HttpServerRequest,
  HttpServerResponse,
} from "effect/unstable/http";

import {
  CONSTRUCT_OMNILOOP_PROXY_PREFIX,
  omniloopBaseUrl,
  omniloopTicketStore,
  readOmniloopConfig,
} from "./constructOmniloop.ts";

/**
 * Reverse proxy for the omniloop dashboard: `/construct/omniloop/<ticket>/<path>`
 * → `http://127.0.0.1:<port>/<path>`. The ticket in the path is the credential
 * (see constructOmniloop.ts); nothing else about the request is trusted.
 *
 * The dashboard is a static app that addresses `/api/...`, `/sse/...` and
 * `/gui/...` absolutely, so its HTML/JS files (and any redirect) are rewritten
 * to sit under the ticket prefix. Everything else, including the SSE streams,
 * is piped through untouched.
 */
const REQUEST_HEADERS_TO_FORWARD = ["accept", "authorization", "content-type", "cache-control", "last-event-id"];
const RESPONSE_HEADERS_TO_FORWARD = ["content-type", "cache-control", "etag", "last-modified"];
const REWRITTEN_CONTENT_TYPES = /^(text\/html|application\/javascript|text\/javascript)/i;

export function splitOmniloopProxyPath(pathname: string): { ticket: string; rest: string } | null {
  if (!pathname.startsWith(`${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/`)) return null;
  const remainder = pathname.slice(CONSTRUCT_OMNILOOP_PROXY_PREFIX.length + 1);
  const slash = remainder.indexOf("/");
  const ticket = slash === -1 ? remainder : remainder.slice(0, slash);
  if (!/^[A-Za-z0-9_-]{16,}$/.test(ticket)) return null;
  return { ticket, rest: slash === -1 ? "/" : remainder.slice(slash) };
}

/** Point the dashboard's absolute references at the ticket prefix. */
export function rewriteOmniloopAsset(body: string, base: string): string {
  return body.replace(/(["'`])\/(api|sse|gui)\//g, `$1${base}/$2/`);
}

export function rewriteOmniloopLocation(location: string, base: string): string {
  return location.startsWith("/") ? `${base}${location}` : location;
}

export const constructOmniloopProxyRouteLayer = HttpRouter.add(
  "*",
  `${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/*`,
  Effect.gen(function* () {
    const request = yield* HttpServerRequest.HttpServerRequest;
    const url = new URL(request.url, "http://construct.invalid");
    const target = splitOmniloopProxyPath(url.pathname);
    if (target === null || !omniloopTicketStore.isValid(target.ticket)) {
      return HttpServerResponse.text("Not Found", { status: 404 });
    }
    const base = `${CONSTRUCT_OMNILOOP_PROXY_PREFIX}/${target.ticket}`;
    const { port } = readOmniloopConfig();
    const upstream = `${omniloopBaseUrl(port)}${target.rest}${url.search}`;

    const forwardedHeaders: Record<string, string> = {};
    for (const name of REQUEST_HEADERS_TO_FORWARD) {
      const value = request.headers[name];
      if (typeof value === "string") forwardedHeaders[name] = value;
    }
    const method = request.method === "HEAD" || request.method === "TRACE" ? "GET" : request.method;
    let outgoing = HttpClientRequest.make(method)(upstream).pipe(HttpClientRequest.setHeaders(forwardedHeaders));
    if (method !== "GET" && method !== "OPTIONS") {
      const body = yield* request.arrayBuffer;
      outgoing = HttpClientRequest.bodyUint8Array(outgoing, new Uint8Array(body), forwardedHeaders["content-type"]);
    }

    const httpClient = yield* HttpClient.HttpClient;
    const response = yield* httpClient.execute(outgoing);
    const headers: Record<string, string> = {};
    for (const name of RESPONSE_HEADERS_TO_FORWARD) {
      const value = response.headers[name];
      if (typeof value === "string") headers[name] = value;
    }
    const location = response.headers["location"];
    if (typeof location === "string") headers["location"] = rewriteOmniloopLocation(location, base);

    const contentType = headers["content-type"] ?? "";
    if (target.rest.startsWith("/gui") && REWRITTEN_CONTENT_TYPES.test(contentType)) {
      const text = yield* response.text;
      return HttpServerResponse.text(rewriteOmniloopAsset(text, base), {
        status: response.status,
        headers,
        contentType,
      });
    }
    return HttpServerResponse.stream(response.stream, { status: response.status, headers });
  }).pipe(
    Effect.catch((cause) =>
      Effect.logWarning("omniloop proxy request failed", { cause }).pipe(
        Effect.as(HttpServerResponse.text("Omniloop is not reachable on this VM.", { status: 502 })),
      ),
    ),
  ),
);
