"use client";

import { ErrorMessage } from "@/components/ErrorMessage";

// For a failure outside the signed-in screens, such as the sign-in page. It is the whole page, so it brings
// its own main region.
export default function RootError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return (
    <main id="main" className="mx-auto w-full max-w-md flex-1 px-4 py-16">
      <ErrorMessage reset={reset} digest={error.digest} />
    </main>
  );
}
