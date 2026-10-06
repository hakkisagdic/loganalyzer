import { redirect } from "next/navigation";
import { AppShell } from "@/components/AppShell";
import { currentUser } from "@/lib/auth/currentUser";
import { TopologyView } from "./TopologyView";

export const dynamic = "force-dynamic";

export default async function TopologyPage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Ftopoloji");
  }

  const username = identity.status === "ok" ? identity.user.username || identity.user.subject : undefined;

  return (
    <AppShell username={username}>
      <TopologyView />
    </AppShell>
  );
}
