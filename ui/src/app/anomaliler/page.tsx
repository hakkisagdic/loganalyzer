import { redirect } from "next/navigation";
import { AppShell } from "@/components/AppShell";
import { currentUser } from "@/lib/auth/currentUser";
import { AnomalyView } from "./AnomalyView";

export const dynamic = "force-dynamic";

export default async function AnomaliesPage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Fanomaliler");
  }

  const username = identity.status === "ok" ? identity.user.username || identity.user.subject : undefined;

  return (
    <AppShell username={username}>
      <AnomalyView />
    </AppShell>
  );
}
