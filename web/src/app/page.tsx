import { redirect } from "next/navigation";

// Deliveries are the question this panel answers first: what failed?
export default function Home() {
  redirect("/deliveries");
}
