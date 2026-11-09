import { NextResponse, type NextRequest } from "next/server";
import { buildContentSecurityPolicy, createNonce } from "@/lib/csp";

/**
 * Runs before every page request (in Next.js 16 this file replaces middleware.ts).
 *
 * It creates a nonce for this request and sends the Content-Security-Policy that goes with it. The
 * nonce is also handed to the render as a request header, which is how Next.js puts it on its own
 * scripts. Any x-nonce sent by the client is overwritten here, never trusted.
 *
 * It does not decide who may see what. Whether a request is allowed is decided by the API on every
 * call, with the API key of the signed-in user.
 */
export function proxy(request: NextRequest) {
  const nonce = createNonce();
  const policy = buildContentSecurityPolicy(nonce, {
    development: process.env.NODE_ENV === "development",
  });

  const requestHeaders = new Headers(request.headers);
  requestHeaders.set("x-nonce", nonce);
  requestHeaders.set("Content-Security-Policy", policy);

  const response = NextResponse.next({ request: { headers: requestHeaders } });
  response.headers.set("Content-Security-Policy", policy);

  return response;
}

export const config = {
  matcher: [
    {
      // Everything except the build output and the icon. Prefetches are left out: they render nothing.
      source: "/((?!api|_next/static|_next/image|favicon.ico).*)",
      missing: [
        { type: "header", key: "next-router-prefetch" },
        { type: "header", key: "purpose", value: "prefetch" },
      ],
    },
  ],
};
