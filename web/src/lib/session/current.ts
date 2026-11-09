import "server-only";
import { cookies } from "next/headers";
import { cache } from "react";
import { readApiBaseUrl, readSessionSecret } from "@/lib/config";
import { loadAccount } from "@/lib/session/account";
import { sessionCookie } from "@/lib/session/cookie";

/**
 * The account behind the request being handled. Several parts of one page can ask for it; React runs the
 * work once per request and shares the answer, so the API is asked once, not once per component.
 */
export const getAccount = cache(async () => {
  const { name } = sessionCookie({ production: process.env.NODE_ENV === "production" });
  const jar = await cookies();

  return loadAccount(jar.get(name)?.value, {
    secret: readSessionSecret(),
    now: new Date(),
    baseUrl: readApiBaseUrl(),
  });
});
