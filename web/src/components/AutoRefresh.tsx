"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";

const INTERVAL_MS = 5_000;

/** Fetches the page again every few seconds, so a delivery that changes state shows up without reloading. */
export function AutoRefresh() {
  const router = useRouter();

  useEffect(() => {
    const timer = setInterval(() => {
      // A tab nobody is looking at does not need fresh data, and would only load the API.
      if (!document.hidden) router.refresh();
    }, INTERVAL_MS);

    return () => clearInterval(timer);
  }, [router]);

  return null;
}
