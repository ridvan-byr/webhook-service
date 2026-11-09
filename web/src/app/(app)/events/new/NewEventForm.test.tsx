import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { describe, expect, it, vi } from "vitest";
import { NewEventForm } from "@/app/(app)/events/new/NewEventForm";
import { INITIAL_STATE, type SendEventState } from "@/lib/events/state";

// What a screen reader reads out after the name of the field: the text of every element it points to.
const describedBy = (element: HTMLElement) =>
  (element.getAttribute("aria-describedby") ?? "")
    .split(" ")
    .map((id) => document.getElementById(id)?.textContent ?? "")
    .join(" ");

const failing = (state: Partial<SendEventState>) =>
  vi.fn(async (): Promise<SendEventState> => ({ ...INITIAL_STATE, ...state }));

describe("NewEventForm", () => {
  it("has a labelled field for the type, the payload and the optional key", () => {
    render(<NewEventForm action={failing({})} />);

    expect(screen.getByLabelText("Type")).toHaveAttribute("name", "type");
    expect(screen.getByLabelText("Payload (JSON)").tagName).toBe("TEXTAREA");
    expect(screen.getByLabelText("Payload (JSON)")).toHaveAttribute("name", "payload");
    expect(screen.getByLabelText(/Idempotency key/)).toHaveAttribute("name", "idempotencyKey");
    expect(screen.getByRole("button", { name: "Send event" })).toBeInTheDocument();
  });

  it("starts with an example that can be sent as it is", () => {
    render(<NewEventForm action={failing({})} />);

    expect(JSON.parse((screen.getByLabelText("Payload (JSON)") as HTMLTextAreaElement).value)).toBeTruthy();
    expect((screen.getByLabelText("Type") as HTMLInputElement).value).toBe("test.ping");
  });

  it("explains that the payload is sent exactly as typed and what the key is for", () => {
    render(<NewEventForm action={failing({})} />);

    expect(screen.getByText(/exactly as you type it/i)).toBeInTheDocument();
    expect(screen.getByText(/same key is never sent twice/i)).toBeInTheDocument();
  });

  it("ties the hint of a field to it, so a screen reader reads the hint after the name", () => {
    render(<NewEventForm action={failing({})} />);

    expect(describedBy(screen.getByLabelText("Payload (JSON)"))).toContain("exactly as you type it");
    expect(describedBy(screen.getByLabelText(/Idempotency key/))).toContain("never sent twice");
  });

  it("marks each field that has a problem and ties its message to it", async () => {
    const action = failing({
      errors: { type: "Enter a type.", payload: "The payload is not valid JSON." },
      values: { type: "", payload: "{", idempotencyKey: "" },
    });
    render(<NewEventForm action={action} />);

    await userEvent.click(screen.getByRole("button", { name: "Send event" }));

    const type = await screen.findByLabelText("Type");
    const payload = screen.getByLabelText("Payload (JSON)");
    expect(type).toHaveAttribute("aria-invalid", "true");
    expect(payload).toHaveAttribute("aria-invalid", "true");
    expect(describedBy(type)).toContain("Enter a type.");
    expect(describedBy(payload)).toContain("The payload is not valid JSON.");
    expect(screen.getByLabelText(/Idempotency key/)).not.toHaveAttribute("aria-invalid");
  });

  it("keeps what was typed when something is wrong", async () => {
    const action = failing({
      errors: { payload: "The payload is not valid JSON." },
      values: { type: "order.created", payload: "{", idempotencyKey: "order-42" },
    });
    render(<NewEventForm action={action} />);

    await userEvent.click(screen.getByRole("button", { name: "Send event" }));
    await screen.findByText("The payload is not valid JSON.");

    expect((screen.getByLabelText("Type") as HTMLInputElement).value).toBe("order.created");
    expect((screen.getByLabelText("Payload (JSON)") as HTMLTextAreaElement).value).toBe("{");
    expect((screen.getByLabelText(/Idempotency key/) as HTMLInputElement).value).toBe("order-42");
  });

  it("shows a failure that is not about one field as an alert", async () => {
    render(<NewEventForm action={failing({ message: "The event could not be sent. Try again in a moment." })} />);

    await userEvent.click(screen.getByRole("button", { name: "Send event" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("The event could not be sent");
  });

  it("shows no problem before anything was sent", () => {
    render(<NewEventForm action={failing({})} />);

    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByLabelText("Type")).not.toHaveAttribute("aria-invalid");
  });

  it("has one level 1 heading and no accessibility violations", async () => {
    const { container } = render(<NewEventForm action={failing({})} />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
