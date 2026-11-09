import { NextResponse, type NextRequest } from "next/server";
import { expiredSessionCookie } from "@/lib/session/cookie";

/**
 * Where a visitor lands when the session in their cookie is no longer good (it ran out, was tampered with,
 * or its key was revoked). Pages cannot remove a cookie, but a route can, so this does it and sends the
 * visitor on to sign in. Without it the proxy and the pages would send the visitor back and forth.
 */
export function GET(request: NextRequest) {
  const { name, value, options } = expiredSessionCookie({ production: process.env.NODE_ENV === "production" });

  const response = NextResponse.redirect(new URL("/login?reason=expired", request.url));
  response.cookies.set(name, value, options);
  return response;
}
