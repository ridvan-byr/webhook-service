import { render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { describe, expect, it } from "vitest";
import { AttemptsTable } from "@/components/AttemptsTable";
import type { Attempt } from "@/lib/api/deliveries";

const attempt = (overrides: Partial<Attempt> = {}): Attempt => ({
  id: "a1a1a1a1-0000-4000-8000-000000000001",
  startedAt: "2026-01-05T09:03:07+00:00",
  durationMs: 2450,
  responseStatus: 500,
  responseSnippet: "Internal Server Error",
  error: null,
  ...overrides,
});

describe("AttemptsTable", () => {
  it("says so when nothing was tried yet", () => {
    render(<AttemptsTable attempts={[]} />);

    expect(screen.getByText("No attempts yet.")).toBeInTheDocument();
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("lists each attempt with when it was, how long it took and what came back", () => {
    render(<AttemptsTable attempts={[attempt()]} />);

    const headers = screen.getAllByRole("columnheader").map((h) => h.textContent);
    expect(headers).toEqual(["#", "Started", "Took", "Response"]);

    const row = screen.getAllByRole("row")[1];
    expect(within(row).getByText("1")).toBeInTheDocument();
    expect(within(row).getByText("2026-01-05 09:03:07 UTC")).toBeInTheDocument();
    expect(within(row).getByText("2.5 s")).toBeInTheDocument();
    expect(within(row).getByText("HTTP 500")).toBeInTheDocument();
    expect(within(row).getByText("Internal Server Error")).toBeInTheDocument();
  });

  it("shows the error when there was no response at all", () => {
    render(<AttemptsTable attempts={[attempt({ responseStatus: null, responseSnippet: null, error: "Timed out" })]} />);

    expect(screen.getByText("Timed out")).toBeInTheDocument();
    expect(screen.queryByText(/HTTP/)).toBeNull();
  });

  it("numbers the attempts in the order given", () => {
    render(<AttemptsTable attempts={[attempt({ id: "a" }), attempt({ id: "b" }), attempt({ id: "c" })]} />);

    const numbers = screen.getAllByRole("row").slice(1).map((r) => within(r).getAllByRole("cell")[0].textContent);

    expect(numbers).toEqual(["1", "2", "3"]);
  });

  it("shows what the receiver answered as text, never as markup", () => {
    const { container } = render(
      <AttemptsTable attempts={[attempt({ responseSnippet: "<script>alert(1)</script><b>hi</b>" })]} />,
    );

    expect(container.querySelector("script, b")).toBeNull();
    expect(screen.getByText("<script>alert(1)</script><b>hi</b>")).toBeInTheDocument();
  });

  it("has no accessibility violations that can be detected without a rendering engine", async () => {
    const { container } = render(<AttemptsTable attempts={[attempt()]} />);

    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
