import type { NextConfig } from "next";

const isProduction = process.env.NODE_ENV === "production";

// Headers that are the same for every response. The Content-Security-Policy is not here: it carries a
// nonce that is new for every request, so it is set in src/proxy.ts.
const securityHeaders = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "Referrer-Policy", value: "no-referrer" },
  // Also forbidden by the CSP (frame-ancestors); this is for browsers that only know the older header.
  { key: "X-Frame-Options", value: "DENY" },
  { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=(), payment=(), usb=()" },
  { key: "Cross-Origin-Opener-Policy", value: "same-origin" },
  // Only where the panel is served over HTTPS; sending it on a local http address does nothing.
  ...(isProduction ? [{ key: "Strict-Transport-Security", value: "max-age=31536000" }] : []),
];

const nextConfig: NextConfig = {
  // The build writes a small server of its own with only the packages it uses. The container image runs that
  // instead of carrying every installed package.
  output: "standalone",

  // Not announcing the framework and its version saves an attacker one step.
  poweredByHeader: false,

  // The "Cache Components" model and "partial prefetching" that a new project turns on by default are
  // left off on purpose. This panel shows the data of one signed-in tenant and its pages carry a nonce
  // that changes with every request, so nothing may be prepared in advance or shared between visitors.
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },

  async headers() {
    return [{ source: "/:path*", headers: securityHeaders }];
  },
};

export default nextConfig;
