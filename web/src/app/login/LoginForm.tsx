"use client";

import { useActionState } from "react";

export interface SignInState {
  message: string | null;
}

type Action = (previous: SignInState, formData: FormData) => Promise<SignInState>;

export function LoginForm({ action, expired = false }: { action: Action; expired?: boolean }) {
  const [state, formAction, pending] = useActionState(action, { message: null });
  const errorId = "api-key-error";

  return (
    <div className="mx-auto w-full max-w-md px-4 py-16">
      <h1 className="text-2xl font-semibold">Sign in</h1>
      <p className="mt-2 text-muted">Paste the API key of your tenant. It is checked against the API and never shown again.</p>

      {expired && (
        <p role="status" className="mt-4 rounded-md border border-line bg-surface px-3 py-2">
          Your session has ended. Sign in again.
        </p>
      )}

      <form action={formAction} className="mt-6 space-y-4">
        <div>
          <label htmlFor="apiKey" className="block font-medium">
            API key
          </label>
          <input
            id="apiKey"
            name="apiKey"
            type="password"
            autoComplete="off"
            spellCheck={false}
            required
            aria-invalid={state.message ? true : undefined}
            aria-describedby={state.message ? errorId : undefined}
            className="mt-1 w-full rounded-md border border-input-line bg-surface px-3 py-2"
          />
          {state.message && (
            <p id={errorId} role="alert" className="mt-2 rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
              {state.message}
            </p>
          )}
        </div>
        <button
          type="submit"
          disabled={pending}
          className="rounded-md bg-accent px-4 py-2 text-on-accent disabled:opacity-60"
        >
          {pending ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </div>
  );
}
