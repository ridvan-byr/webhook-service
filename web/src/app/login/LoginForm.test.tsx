import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { describe, expect, it, vi } from "vitest";
import { LoginForm } from "@/app/login/LoginForm";

const reject = vi.fn(async () => ({ message: "That API key was not accepted." }));

describe("LoginForm", () => {
  it("asks for the API key in a labelled field that hides what is typed and is not remembered", () => {
    render(<LoginForm action={reject} />);

    const field = screen.getByLabelText("API key");
    expect(field).toHaveAttribute("type", "password");
    expect(field).toHaveAttribute("name", "apiKey");
    expect(field).toHaveAttribute("autocomplete", "off");
    expect(field).toBeRequired();
    expect(screen.getByRole("button", { name: "Sign in" })).toBeInTheDocument();
  });

  it("says why it failed next to the field, so screen readers tie the two together", async () => {
    render(<LoginForm action={reject} />);

    await userEvent.type(screen.getByLabelText("API key"), "whk_wrong");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("That API key was not accepted.");
    const field = screen.getByLabelText("API key");
    expect(field).toHaveAttribute("aria-invalid", "true");
    expect(field.getAttribute("aria-describedby")).toBe(alert.id);
  });

  it("shows no error before anything was tried", () => {
    render(<LoginForm action={reject} />);

    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByLabelText("API key")).not.toHaveAttribute("aria-invalid");
  });

  it("explains why the visitor is here again when the session ended", () => {
    render(<LoginForm action={reject} expired />);

    expect(screen.getByRole("status")).toHaveTextContent("Your session has ended. Sign in again.");
  });

  it("has one level 1 heading and no accessibility violations", async () => {
    const { container } = render(<LoginForm action={reject} expired />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
