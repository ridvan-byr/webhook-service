"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { readApiBaseUrl, readSessionSecret } from "@/lib/config";
import { performSignIn } from "@/lib/session/sign-in";
import { getSignInLimiter } from "@/lib/session/sign-in-limiter";

export async function signIn(_previous: { message: string | null }, formData: FormData) {
  const apiKey = formData.get("apiKey");
  const jar = await cookies();

  const result = await performSignIn(typeof apiKey === "string" ? apiKey : "", {
    baseUrl: readApiBaseUrl(),
    secret: readSessionSecret(),
    now: new Date(),
    production: process.env.NODE_ENV === "production",
    setCookie: (name, value, options) => jar.set(name, value, options),
    limiter: getSignInLimiter(),
    // Never says what was typed, only that the limit was reached.
    onLimitReached: () => console.warn("Too many failed sign-ins. Sign-in is refused for a while."),
  });

  if (!result.ok) return { message: result.message };

  redirect("/deliveries");
}
