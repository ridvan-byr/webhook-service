"use client";

import { useActionState } from "react";
import { INITIAL_STATE, type SendEventState } from "@/lib/events/state";

type Action = (previous: SendEventState, formData: FormData) => Promise<SendEventState>;

const FIELD = "mt-1 w-full rounded-md border border-input-line bg-surface px-3 py-2";

function FieldError({ id, message }: { id: string; message: string | undefined }) {
  if (!message) return null;
  return (
    <p id={id} className="mt-2 rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
      {message}
    </p>
  );
}

export function NewEventForm({ action }: { action: Action }) {
  const [state, formAction, pending] = useActionState(action, INITIAL_STATE);
  const { errors, values } = state;

  // A field points to its hint and, when something is wrong, to the message as well.
  const describedBy = (field: keyof typeof errors, hint?: string) =>
    [hint, errors[field] ? `${field}-error` : undefined].filter(Boolean).join(" ") || undefined;

  return (
    <div className="max-w-2xl">
      <h1 className="text-lg font-medium">Send test event</h1>
      <p className="mt-1 text-muted">
        Send an event to your active endpoints to see how it is delivered. It is a real event: receivers get it.
      </p>

      {state.message && (
        <p role="alert" className="mt-4 rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
          {state.message}
        </p>
      )}

      <form action={formAction} className="mt-6 space-y-5">
        <div>
          <label htmlFor="type" className="block font-medium">
            Type
          </label>
          <input
            id="type"
            name="type"
            type="text"
            defaultValue={values.type}
            autoComplete="off"
            spellCheck={false}
            aria-invalid={errors.type ? true : undefined}
            aria-describedby={describedBy("type")}
            className={FIELD}
          />
          <FieldError id="type-error" message={errors.type} />
        </div>

        <div>
          <label htmlFor="payload" className="block font-medium">
            Payload (JSON)
          </label>
          <p id="payload-hint" className="text-sm text-muted">
            Sent exactly as you type it, with the same spacing, because the signature covers those characters.
          </p>
          <textarea
            id="payload"
            name="payload"
            rows={8}
            defaultValue={values.payload}
            spellCheck={false}
            aria-invalid={errors.payload ? true : undefined}
            aria-describedby={describedBy("payload", "payload-hint")}
            className={`${FIELD} font-mono text-sm`}
          />
          <FieldError id="payload-error" message={errors.payload} />
        </div>

        <div>
          <label htmlFor="idempotencyKey" className="block font-medium">
            Idempotency key (optional)
          </label>
          <p id="idempotencyKey-hint" className="text-sm text-muted">
            An event with the same key is never sent twice. Leave it empty to send a new event every time.
          </p>
          <input
            id="idempotencyKey"
            name="idempotencyKey"
            type="text"
            defaultValue={values.idempotencyKey}
            autoComplete="off"
            spellCheck={false}
            aria-invalid={errors.idempotencyKey ? true : undefined}
            aria-describedby={describedBy("idempotencyKey", "idempotencyKey-hint")}
            className={FIELD}
          />
          <FieldError id="idempotencyKey-error" message={errors.idempotencyKey} />
        </div>

        <button type="submit" disabled={pending} className="rounded-md bg-accent px-4 py-2 text-on-accent disabled:opacity-60">
          {pending ? "Sending…" : "Send event"}
        </button>
      </form>
    </div>
  );
}
