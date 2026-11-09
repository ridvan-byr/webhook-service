/** What the add-endpoint form knows after an attempt. */
export interface AddEndpointState {
  /** What is wrong with the address, shown next to the field. */
  error: string | null;
  /** A failure that is not about the address. */
  message: string | null;
  /** What was typed, so it is not lost when something is wrong. */
  value: string;
  /**
   * The endpoint that was just made, with its signing secret. This is the only place the secret ever
   * exists on the panel's side, and it only reaches the person who made the endpoint, in the response to
   * their own request.
   */
  created: { url: string; secret: string } | null;
}

export const INITIAL_STATE: AddEndpointState = { error: null, message: null, value: "", created: null };
