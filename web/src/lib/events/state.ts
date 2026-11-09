import type { EventFormErrors, EventFormInput } from "@/lib/events/validate";

/** What the send-event form knows after an attempt: what is wrong, and what was typed so it is not lost. */
export interface SendEventState {
  message: string | null;
  errors: EventFormErrors;
  values: EventFormInput;
}

/** The form as it first appears: a small example that can be sent as it is. */
export const INITIAL_STATE: SendEventState = {
  message: null,
  errors: {},
  values: { type: "test.ping", payload: '{\n  "hello": "world"\n}', idempotencyKey: "" },
};
