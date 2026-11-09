// @vitest-environment node
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

// vitest.config.mts replaces the real "server-only" package with an empty module so server code can be
// tested. That makes the real guard invisible to the tests, so this checks the guard itself is still there:
// without it a client component importing one of these files would pull the secrets into the browser bundle.
const MODULES_THAT_HANDLE_SECRETS = [
  "lib/config.ts",
  "lib/api/client.ts",
  "lib/api/deliveries.ts",
  "lib/api/events.ts",
  "lib/session/account.ts",
  "lib/session/api-access.ts",
  "lib/session/current.ts",
  "lib/session/crypto.ts",
  "lib/session/sign-in.ts",
];

describe("server-only guard", () => {
  it.each(MODULES_THAT_HANDLE_SECRETS)("%s can only be imported by server code", (file) => {
    const source = readFileSync(join(__dirname, file), "utf8");

    expect(source).toMatch(/^import "server-only";$/m);
  });
});
