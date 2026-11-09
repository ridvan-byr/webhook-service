import { describe, expect, it } from "vitest";
import { MAX_KEY_LENGTH, MAX_PAYLOAD_LENGTH, MAX_TYPE_LENGTH, validateEventForm } from "@/lib/events/validate";

const valid = { type: "order.created", payload: '{"id": 42}', idempotencyKey: "" };

describe("validateEventForm", () => {
  it("accepts a type and a JSON payload, and keeps the payload exactly as it was typed", () => {
    const payload = '{\n  "id":   42,\n  "big": 12345678901234567890\n}';

    expect(validateEventForm({ ...valid, payload })).toEqual({
      ok: true,
      value: { type: "order.created", payloadText: payload, idempotencyKey: undefined },
    });
  });

  it("does not even trim the payload: spaces and line breaks around it are part of what is signed", () => {
    const payload = '  \n{"a": 1}\n\n';

    expect(validateEventForm({ ...valid, payload })).toMatchObject({ ok: true, value: { payloadText: payload } });
  });

  it("trims the type and the idempotency key, and treats an empty key as none", () => {
    const result = validateEventForm({ ...valid, type: "  order.created  ", idempotencyKey: "  order-42 " });

    expect(result).toMatchObject({ ok: true, value: { type: "order.created", idempotencyKey: "order-42" } });
    expect(validateEventForm({ ...valid, idempotencyKey: "   " })).toMatchObject({
      ok: true,
      value: { idempotencyKey: undefined },
    });
  });

  it.each(["{}", "[]", "0", "false", '"text"', "[1, 2]"])("accepts %s as a payload: any JSON value except null", (payload) => {
    expect(validateEventForm({ ...valid, payload }).ok).toBe(true);
  });

  it.each(["", "   ", "\n"])("asks for a type when it is %j", (type) => {
    expect(validateEventForm({ ...valid, type })).toEqual({ ok: false, errors: { type: "Enter a type, for example order.created." } });
  });

  it("refuses a type that is too long", () => {
    const result = validateEventForm({ ...valid, type: "x".repeat(MAX_TYPE_LENGTH + 1) });

    expect(result).toMatchObject({ ok: false, errors: { type: expect.stringContaining(`${MAX_TYPE_LENGTH}`) } });
    expect(validateEventForm({ ...valid, type: "x".repeat(MAX_TYPE_LENGTH) }).ok).toBe(true);
  });

  it.each(["", "   "])("asks for a payload when it is %j", (payload) => {
    expect(validateEventForm({ ...valid, payload })).toEqual({ ok: false, errors: { payload: "Enter the payload as JSON." } });
  });

  it.each(["{", "{'a': 1}", "hello", "1 2", '{"a": 1,}', "undefined"])("says %j is not valid JSON", (payload) => {
    expect(validateEventForm({ ...valid, payload })).toEqual({ ok: false, errors: { payload: "The payload is not valid JSON." } });
  });

  it("does not accept null, which the API treats as no payload", () => {
    expect(validateEventForm({ ...valid, payload: " null " })).toEqual({
      ok: false,
      errors: { payload: "The payload must be a JSON value other than null." },
    });
  });

  it("refuses a payload that is too long", () => {
    const payload = `"${"x".repeat(MAX_PAYLOAD_LENGTH)}"`;

    expect(validateEventForm({ ...valid, payload })).toMatchObject({ ok: false, errors: { payload: expect.stringContaining("at most") } });
  });

  it("refuses an idempotency key that is too long", () => {
    const result = validateEventForm({ ...valid, idempotencyKey: "k".repeat(MAX_KEY_LENGTH + 1) });

    expect(result).toMatchObject({ ok: false, errors: { idempotencyKey: expect.stringContaining(`${MAX_KEY_LENGTH}`) } });
  });

  it("reports every problem at once, so the form can mark them all", () => {
    const result = validateEventForm({ type: "", payload: "{", idempotencyKey: "k".repeat(MAX_KEY_LENGTH + 1) });

    expect(result.ok).toBe(false);
    expect(Object.keys(result.ok ? {} : result.errors).sort()).toEqual(["idempotencyKey", "payload", "type"]);
  });
});
