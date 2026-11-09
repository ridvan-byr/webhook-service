import type { Metadata } from "next";
import { NewEventForm } from "./NewEventForm";
import { sendEvent } from "./actions";

export const metadata: Metadata = { title: "Send test event" };

export default function NewEventPage() {
  return <NewEventForm action={sendEvent} />;
}
