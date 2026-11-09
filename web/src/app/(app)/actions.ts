"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { expiredSessionCookie } from "@/lib/session/cookie";

/** Ends the session by overwriting the cookie. The key itself stays valid; it is only forgotten here. */
export async function signOut() {
  const { name, value, options } = expiredSessionCookie({ production: process.env.NODE_ENV === "production" });
  (await cookies()).set(name, value, options);
  redirect("/login");
}
