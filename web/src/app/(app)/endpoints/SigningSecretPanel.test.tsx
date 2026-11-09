import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { describe, expect, it, vi } from "vitest";
import { SigningSecretPanel } from "@/app/(app)/endpoints/SigningSecretPanel";

const SECRET = "whsec_Zm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyMTI=";
const props = { endpointUrl: "https://shop.example.com/hooks", secret: SECRET };

describe("SigningSecretPanel", () => {
  it("shows the secret and says it will not be shown again", () => {
    render(<SigningSecretPanel {...props} onDismiss={() => {}} />);

    expect(screen.getByRole("region", { name: "Save your signing secret" })).toBeInTheDocument();
    expect(screen.getByText(SECRET)).toBeInTheDocument();
    expect(screen.getByText(/will not be shown again/i)).toBeInTheDocument();
    expect(screen.getByText("https://shop.example.com/hooks")).toBeInTheDocument();
  });

  it("copies exactly the secret and says so without moving focus", async () => {
    const user = userEvent.setup();
    render(<SigningSecretPanel {...props} onDismiss={() => {}} />);
    const copy = screen.getByRole("button", { name: "Copy secret" });

    await user.click(copy);

    expect(await navigator.clipboard.readText()).toBe(SECRET);
    expect(screen.getByRole("status")).toHaveTextContent("Copied");
    expect(copy).toHaveFocus();
  });

  it("says so when the browser does not let it copy, and the secret can still be selected", async () => {
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, "writeText").mockRejectedValue(new Error("denied"));
    render(<SigningSecretPanel {...props} onDismiss={() => {}} />);

    await user.click(screen.getByRole("button", { name: "Copy secret" }));

    expect(await screen.findByRole("status")).toHaveTextContent(/could not copy/i);
    expect(screen.getByText(SECRET)).toBeInTheDocument();
  });

  it("goes away when the visitor says the secret is saved", async () => {
    const onDismiss = vi.fn();
    render(<SigningSecretPanel {...props} onDismiss={onDismiss} />);

    await userEvent.click(screen.getByRole("button", { name: "I have saved it" }));

    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it("never puts the secret in a live region, so it is not read out unasked", async () => {
    const user = userEvent.setup();
    render(<SigningSecretPanel {...props} onDismiss={() => {}} />);
    await user.click(screen.getByRole("button", { name: "Copy secret" }));

    expect(screen.getByRole("status").textContent).not.toContain(SECRET);
    expect(document.querySelector("[aria-live]")?.textContent ?? "").not.toContain(SECRET);
  });

  it("has no accessibility violations that can be detected without a rendering engine", async () => {
    const { container } = render(<SigningSecretPanel {...props} onDismiss={() => {}} />);

    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
