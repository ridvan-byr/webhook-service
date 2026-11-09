import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// Remove what a test rendered, so tests cannot see each other's pages.
afterEach(() => {
  cleanup();
});
