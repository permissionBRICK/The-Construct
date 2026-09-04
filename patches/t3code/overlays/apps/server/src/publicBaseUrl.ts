/**
 * The origin humans and browsers should use to reach this server.
 *
 * Construct serves T3 Code behind a TLS reverse proxy, so the origin the server
 * binds to (plain HTTP on the loopback port) is not the one that belongs in a
 * pairing URL, a QR code or a browser tab. `T3CODE_PUBLIC_BASE_URL` names the
 * public origin; unset means "use the bind origin", which is the unpatched
 * behaviour.
 */
export type PublicBaseUrlSetting =
  | { readonly kind: "unset" }
  | { readonly kind: "invalid"; readonly raw: string }
  | { readonly kind: "ok"; readonly origin: string };

/** Any path, query or fragment is dropped: callers append their own. */
export function parsePublicBaseUrl(raw: string | undefined): PublicBaseUrlSetting {
  const trimmed = raw?.trim();
  if (!trimmed) return { kind: "unset" };
  let url: URL;
  try {
    url = new URL(trimmed);
  } catch {
    return { kind: "invalid", raw: trimmed };
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    return { kind: "invalid", raw: trimmed };
  }
  return { kind: "ok", origin: url.origin };
}

export function readPublicBaseUrl(): PublicBaseUrlSetting {
  return parsePublicBaseUrl(process.env.T3CODE_PUBLIC_BASE_URL);
}

/** The configured public origin, or undefined when unset or unusable. */
export function publicBaseUrlOrigin(): string | undefined {
  const setting = readPublicBaseUrl();
  return setting.kind === "ok" ? setting.origin : undefined;
}
