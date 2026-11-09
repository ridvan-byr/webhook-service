const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Whether text is an id as the API writes them. Ids come from the address bar, so check before using one in a path. */
export function isGuid(value: string): boolean {
  return GUID.test(value);
}
