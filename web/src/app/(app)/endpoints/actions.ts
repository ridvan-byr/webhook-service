"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { createEndpoint, setEndpointActive } from "@/lib/api/endpoints";
import { problemFor } from "@/lib/api/problem";
import { INITIAL_STATE, type AddEndpointState } from "@/lib/endpoints/state";
import { validateEndpointUrl } from "@/lib/endpoints/validate";
import { requireApi } from "@/lib/session/api-access";

const REFUSED =
  "The API did not accept that address. Addresses must use https and must not point to a private network, " +
  "unless the service is set up to allow them.";

/** Registers an address and hands back the signing secret, which exists only in this response. */
export async function addEndpoint(_previous: AddEndpointState, formData: FormData): Promise<AddEndpointState> {
  // This is reachable without the page that shows the form, so it checks who is asking itself.
  const api = await requireApi();

  const raw = formData.get("url");
  const value = typeof raw === "string" ? raw : "";

  if (!api) {
    return { ...INITIAL_STATE, value, message: "The API could not be reached. Your session is still good; try again in a moment." };
  }

  const checked = validateEndpointUrl(value);
  if (!checked.ok) return { ...INITIAL_STATE, value, error: checked.error };

  const result = await createEndpoint(api, checked.url);

  if (result.ok) {
    revalidatePath("/endpoints");
    return { ...INITIAL_STATE, created: { url: result.data.url, secret: result.data.signingSecret } };
  }

  if (problemFor(result) === "expired") redirect("/session/expired");

  if (result.kind === "rejected" && result.status === 400) return { ...INITIAL_STATE, value, error: REFUSED };

  // Whatever the API wrote is not shown: it may say more than a visitor needs.
  return { ...INITIAL_STATE, value, message: "The endpoint could not be added. Try again in a moment." };
}

/** Switches an endpoint on or off. An inactive endpoint gets no new events. */
export async function setActive(id: string, isActive: boolean) {
  // Like every Server Action, this is reachable without its page, so it checks who is asking itself.
  const api = await requireApi();
  if (!api) redirect("/endpoints?toggle=failed");

  // Only exactly true is "on". Anything else a caller sends is "off", never passed on as it came.
  const result = await setEndpointActive(api, id, isActive === true);

  if (result.ok) {
    revalidatePath("/endpoints");
    redirect("/endpoints");
  }

  if (problemFor(result) === "expired") redirect("/session/expired");
  redirect("/endpoints?toggle=failed");
}
