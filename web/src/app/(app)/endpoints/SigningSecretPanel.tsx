"use client";

import { useState } from "react";

type Copy = "idle" | "copied" | "failed";

/**
 * Shows the signing secret of a new endpoint. The API never shows it again, so this panel is the only
 * chance to save it: it stays until the visitor says it is saved.
 */
export function SigningSecretPanel({
  endpointUrl,
  secret,
  onDismiss,
}: {
  endpointUrl: string;
  secret: string;
  onDismiss: () => void;
}) {
  const [copy, setCopy] = useState<Copy>("idle");

  async function copySecret() {
    try {
      await navigator.clipboard.writeText(secret);
      setCopy("copied");
    } catch {
      // No clipboard (an insecure page, or permission refused). The secret can still be selected by hand.
      setCopy("failed");
    }
  }

  return (
    <section aria-labelledby="secret-heading" className="mb-6 border border-line bg-surface p-4">
      <h2 id="secret-heading" className="font-medium">
        Save your signing secret
      </h2>
      <p className="mt-1 text-muted">
        Endpoint added: <span className="break-all">{endpointUrl}</span>
      </p>
      <p className="mt-2">
        Use this secret to check the signature of every event. It will not be shown again. If you lose it, add the
        endpoint again.
      </p>

      <code className="mt-3 block select-all break-all rounded-md border border-input-line bg-page px-3 py-2 font-mono text-sm">
        {secret}
      </code>

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <button type="button" onClick={copySecret} className="rounded-md border border-line px-3 py-1.5 hover:bg-page">
          Copy secret
        </button>
        <button type="button" onClick={onDismiss} className="rounded-md bg-accent px-3 py-1.5 text-on-accent">
          I have saved it
        </button>
        {/* Always present, so a screen reader announces the change. It never contains the secret. */}
        <p role="status" className="text-sm text-muted">
          {copy === "copied" && "Copied"}
          {copy === "failed" && "Could not copy. Select the secret and copy it yourself."}
        </p>
      </div>
    </section>
  );
}
