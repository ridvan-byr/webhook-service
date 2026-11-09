import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { describe, expect, it, vi } from "vitest";
import AppError from "@/app/(app)/error";
import RootError from "@/app/error";
import { ErrorMessage } from "@/components/ErrorMessage";

describe("ErrorMessage", () => {
  it("says something went wrong under one level 1 heading and offers to try again", async () => {
    const reset = vi.fn();
    render(<ErrorMessage reset={reset} />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Something went wrong");

    await userEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(reset).toHaveBeenCalledTimes(1);
  });

  it("gives a way out to a screen that works", () => {
    render(<ErrorMessage reset={() => {}} />);

    expect(screen.getByRole("link", { name: "Back to deliveries" })).toHaveAttribute("href", "/deliveries");
  });

  it("shows the reference of the error so it can be found in the logs, and nothing else about it", () => {
    render(<ErrorMessage reset={() => {}} digest="1234567890" />);

    expect(screen.getByText("Reference: 1234567890")).toBeInTheDocument();
  });

  it("shows no reference when there is none", () => {
    render(<ErrorMessage reset={() => {}} />);

    expect(screen.queryByText(/reference/i)).toBeNull();
  });

  it("has no accessibility violations that can be detected without a rendering engine", async () => {
    const { container } = render(<ErrorMessage reset={() => {}} digest="abc" />);

    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});

describe("error pages", () => {
  const error = Object.assign(new Error("connect ECONNREFUSED 10.0.0.5:5005 with key whk_secret"), { digest: "d1g3st" });

  it("never show what the error said: it can contain addresses, keys or stack details", () => {
    render(<AppError error={error} reset={() => {}} />);

    expect(document.body.textContent).not.toContain("ECONNREFUSED");
    expect(document.body.textContent).not.toContain("whk_secret");
    expect(screen.getByText("Reference: d1g3st")).toBeInTheDocument();
  });

  it("inside the signed-in screens, go into the main region that is already there instead of adding a second one", () => {
    const { container } = render(<AppError error={error} reset={() => {}} />);

    expect(container.querySelector("main, #main")).toBeNull();
    expect(screen.getByRole("heading", { level: 1 })).toBeInTheDocument();
  });

  it("outside the signed-in screens, are the whole page and bring their own main region", () => {
    render(<RootError error={error} reset={() => {}} />);

    expect(screen.getAllByRole("main")).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Something went wrong");
  });
});
