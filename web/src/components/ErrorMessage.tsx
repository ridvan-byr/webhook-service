"use client";

import Link from "next/link";

/**
 * What a visitor sees when something unexpected breaks. It never shows what the error said: that can contain
 * addresses, keys or details of the server. The reference (a hash Next.js makes) lets the operator find the
 * real error in the server log.
 */
export function ErrorMessage({ reset, digest }: { reset: () => void; digest?: string }) {
  return (
    <>
      <h1 className="text-2xl font-semibold">Something went wrong</h1>
      <p className="mt-2 text-muted">The page could not be shown. Your session is not affected.</p>
      {digest && <p className="mt-2 text-sm text-muted">{`Reference: ${digest}`}</p>}
      <div className="mt-4 flex flex-wrap items-center gap-4">
        <button type="button" onClick={reset} className="rounded-md bg-accent px-4 py-2 text-on-accent">
          Try again
        </button>
        <Link href="/deliveries" className="underline underline-offset-2">
          Back to deliveries
        </Link>
      </div>
    </>
  );
}
