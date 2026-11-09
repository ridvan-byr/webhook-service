"use server";

import { redirect } from "next/navigation";
import { resendDelivery } from "@/lib/api/deliveries";
import { isGuid } from "@/lib/api/ids";
import { problemFor } from "@/lib/api/problem";
import { requireApi } from "@/lib/session/api-access";

/** Puts a dead delivery back in the queue. The API decides whether that is allowed. */
export async function resend(id: string) {
  // This is reachable without the page that shows its button, so it checks who is asking itself.
  const api = await requireApi();

  // Only a real id may become part of the address we go back to.
  const back = isGuid(id) ? `/deliveries/${id}` : "/deliveries";
  if (!api) redirect(`${back}?resend=failed`);

  const result = await resendDelivery(api, id);

  if (!result.ok && problemFor(result) === "expired") redirect("/session/expired");
  redirect(`${back}?resend=${result.ok ? "ok" : "failed"}`);
}
