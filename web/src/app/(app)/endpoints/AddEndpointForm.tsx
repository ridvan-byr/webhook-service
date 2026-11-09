"use client";

import { useActionState, useState } from "react";
import { INITIAL_STATE, type AddEndpointState } from "@/lib/endpoints/state";
import { SigningSecretPanel } from "./SigningSecretPanel";

type Action = (previous: AddEndpointState, formData: FormData) => Promise<AddEndpointState>;

export function AddEndpointForm({ action }: { action: Action }) {
  const [state, formAction, pending] = useActionState(action, INITIAL_STATE);
  // The secret that was saved. A newer endpoint has a different secret, so it shows again.
  const [dismissed, setDismissed] = useState<string | null>(null);

  const showSecret = state.created !== null && state.created.secret !== dismissed;

  return (
    <div className="mb-6">
      {state.created && showSecret && (
        <SigningSecretPanel
          endpointUrl={state.created.url}
          secret={state.created.secret}
          onDismiss={() => setDismissed(state.created?.secret ?? null)}
        />
      )}

      {state.message && (
        <p role="alert" className="mb-3 rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
          {state.message}
        </p>
      )}

      <form action={formAction} noValidate className="max-w-xl">
        <label htmlFor="url" className="block font-medium">
          Endpoint address
        </label>
        <p id="url-hint" className="text-sm text-muted">
          Where events are sent, for example https://example.com/hooks.
        </p>
        <div className="mt-1 flex gap-2">
          <input
            id="url"
            name="url"
            type="url"
            defaultValue={state.value}
            autoComplete="off"
            spellCheck={false}
            aria-invalid={state.error ? true : undefined}
            aria-describedby={state.error ? "url-hint url-error" : "url-hint"}
            className="min-w-0 flex-1 rounded-md border border-input-line bg-surface px-3 py-2"
          />
          <button type="submit" disabled={pending} className="rounded-md bg-accent px-4 py-2 text-on-accent disabled:opacity-60">
            {pending ? "Adding…" : "Add endpoint"}
          </button>
        </div>
        {state.error && (
          <p id="url-error" className="mt-2 rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
            {state.error}
          </p>
        )}
      </form>
    </div>
  );
}
