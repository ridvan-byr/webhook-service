"use server";

import { redirect } from "next/navigation";
import { createEvent } from "@/lib/api/events";
import { isGuid } from "@/lib/api/ids";
import { problemFor } from "@/lib/api/problem";
import type { SendEventState } from "@/lib/events/state";
import { validateEventForm } from "@/lib/events/validate";
import { requireApi } from "@/lib/session/api-access";

const text = (data: FormData, name: string) => {
  const value = data.get(name);
  return typeof value === "string" ? value : "";
};

/** Sends an event on behalf of the signed-in user and shows the result. */
export async function sendEvent(_previous: SendEventState, formData: FormData): Promise<SendEventState> {
  // This is reachable without the page that shows the form, so it checks who is asking itself.
  const api = await requireApi();

  const values = { type: text(formData, "type"), payload: text(formData, "payload"), idempotencyKey: text(formData, "idempotencyKey") };

  if (!api) {
    return { message: "The API could not be reached. Your session is still good; try again in a moment.", errors: {}, values };
  }

  const checked = validateEventForm(values);
  if (!checked.ok) return { message: null, errors: checked.errors, values };

  const result = await createEvent(api, checked.value);

  if (result.ok) {
    const { eventId, duplicate } = result.data;
    redirect(isGuid(eventId) ? `/events/${eventId}?sent=${duplicate ? "duplicate" : "ok"}` : "/deliveries");
  }

  if (problemFor(result) === "expired") redirect("/session/expired");

  if (result.kind === "rejected" && result.status === 409) {
    return {
      message: null,
      errors: { idempotencyKey: "This key was already used for a different event. Use a new key." },
      values,
    };
  }

  // Whatever the API wrote is not shown: it may say more than a visitor needs.
  return { message: "The event could not be sent. Try again in a moment.", errors: {}, values };
}
