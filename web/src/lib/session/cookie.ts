/** How long a session lasts. It is fixed: using the panel does not extend it. */
export const SESSION_LIFETIME_SECONDS = 8 * 60 * 60;

/**
 * The name and settings of the session cookie.
 *
 * - httpOnly: JavaScript in the page cannot read it, so a script that got into the page cannot steal it.
 * - sameSite strict: the browser does not send it along with a request that comes from another site.
 * - In production it is Secure (HTTPS only) and called __Host-session, which makes the browser refuse it
 *   unless it is Secure, has path "/" and has no Domain attribute, so a sibling subdomain cannot plant or
 *   overwrite it. That prefix cannot be used over plain http, so development uses a plain name.
 */
export function sessionCookie({ production }: { production: boolean }) {
  return {
    name: production ? "__Host-session" : "session",
    options: {
      httpOnly: true,
      secure: production,
      sameSite: "strict" as const,
      path: "/",
      maxAge: SESSION_LIFETIME_SECONDS,
    },
  };
}

/**
 * The cookie that removes the session: same name and attributes, empty, already expired.
 *
 * It must keep Secure in production. A browser ignores any change to a __Host- cookie that is not Secure,
 * so deleting it the plain way (cookies().delete) leaves the session alive while looking like a sign-out.
 */
export function expiredSessionCookie(environment: { production: boolean }) {
  const { name, options } = sessionCookie(environment);
  return { name, value: "", options: { ...options, maxAge: 0 } };
}
