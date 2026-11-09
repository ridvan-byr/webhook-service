import { render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AutoRefresh } from "@/components/AutoRefresh";

const refresh = vi.hoisted(() => vi.fn());
vi.mock("next/navigation", () => ({ useRouter: () => ({ refresh }) }));

function setHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
}

beforeEach(() => {
  vi.useFakeTimers();
  refresh.mockClear();
  setHidden(false);
});

afterEach(() => {
  vi.useRealTimers();
  setHidden(false);
});

describe("AutoRefresh", () => {
  it("fetches the page again every five seconds", () => {
    render(<AutoRefresh />);

    vi.advanceTimersByTime(4_999);
    expect(refresh).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(refresh).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(10_000);
    expect(refresh).toHaveBeenCalledTimes(3);
  });

  it("leaves a tab that nobody is looking at alone", () => {
    render(<AutoRefresh />);
    setHidden(true);

    vi.advanceTimersByTime(20_000);

    expect(refresh).not.toHaveBeenCalled();
  });

  it("stops when the page goes away", () => {
    const { unmount } = render(<AutoRefresh />);

    unmount();
    vi.advanceTimersByTime(20_000);

    expect(refresh).not.toHaveBeenCalled();
  });

  it("shows nothing", () => {
    const { container } = render(<AutoRefresh />);

    expect(container).toBeEmptyDOMElement();
  });
});
