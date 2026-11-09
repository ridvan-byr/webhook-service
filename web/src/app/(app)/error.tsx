"use client";

import { ErrorMessage } from "@/components/ErrorMessage";

// Shown inside the app shell, which already has the main region, so it must not add another.
export default function AppError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return (
    <div className="max-w-md py-10">
      <ErrorMessage reset={reset} digest={error.digest} />
    </div>
  );
}
