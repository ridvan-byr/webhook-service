import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { describe, expect, it, vi } from "vitest";
import { AddEndpointForm } from "@/app/(app)/endpoints/AddEndpointForm";
import { INITIAL_STATE, type AddEndpointState } from "@/lib/endpoints/state";

const answering = (state: Partial<AddEndpointState>) =>
  vi.fn(async (): Promise<AddEndpointState> => ({ ...INITIAL_STATE, ...state }));

describe("AddEndpointForm", () => {
  it("has a labelled field for the address and a button", () => {
    render(<AddEndpointForm action={answering({})} />);

    const field = screen.getByLabelText("Endpoint address");
    expect(field).toHaveAttribute("name", "url");
    expect(field).toHaveAttribute("type", "url");
    expect(screen.getByRole("button", { name: "Add endpoint" })).toBeInTheDocument();
  });

  it("leaves checking the address to the page, so its message is the one shown and not the browser's own bubble", () => {
    const { container } = render(<AddEndpointForm action={answering({})} />);

    expect(container.querySelector("form")).toHaveAttribute("novalidate");
  });

  it("marks the field and ties the message to it when the address is wrong, keeping what was typed", async () => {
    render(<AddEndpointForm action={answering({ error: "That is not a web address.", value: "ftp://x" })} />);

    await userEvent.click(screen.getByRole("button", { name: "Add endpoint" }));

    const field = await screen.findByLabelText("Endpoint address");
    expect(field).toHaveAttribute("aria-invalid", "true");
    const ids = (field.getAttribute("aria-describedby") ?? "").split(" ");
    expect(ids.map((id) => document.getElementById(id)?.textContent).join(" ")).toContain("That is not a web address.");
    expect((field as HTMLInputElement).value).toBe("ftp://x");
  });

  it("shows a failure that is not about the address as an alert", async () => {
    render(<AddEndpointForm action={answering({ message: "The endpoint could not be added. Try again in a moment." })} />);

    await userEvent.click(screen.getByRole("button", { name: "Add endpoint" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("could not be added");
  });

  it("shows no problem and no secret before anything was added", () => {
    render(<AddEndpointForm action={answering({})} />);

    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByRole("region", { name: "Save your signing secret" })).toBeNull();
  });

  it("shows the secret of the new endpoint, and hides it for good once it is saved", async () => {
    const created = { url: "https://shop.example.com/hooks", secret: "whsec_abc" };
    render(<AddEndpointForm action={answering({ created })} />);

    await userEvent.click(screen.getByRole("button", { name: "Add endpoint" }));
    expect(await screen.findByText("whsec_abc")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "I have saved it" }));

    expect(screen.queryByText("whsec_abc")).toBeNull();
    expect(screen.queryByRole("region", { name: "Save your signing secret" })).toBeNull();
  });

  it("has no accessibility violations that can be detected without a rendering engine", async () => {
    const { container } = render(<AddEndpointForm action={answering({})} />);

    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
