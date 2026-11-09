/** Shown in place of a page's content when the API fails. The session is fine; trying again may help. */
export function ApiUnavailable() {
  return (
    <p role="alert" className="rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
      The API could not be reached or did not answer properly. Your session is still good; reload in a moment.
    </p>
  );
}
