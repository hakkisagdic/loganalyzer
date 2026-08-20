import { redirect } from "next/navigation";

import { currentUser } from "@/lib/auth/currentUser";

import { MaintenanceManager } from "./MaintenanceManager";

export const dynamic = "force-dynamic";

export default async function MaintenancePage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Falarmlar%2Fbakim");
  }

  if (identity.status === "error") {
    return null;
  }

  return <MaintenanceManager ownerGroups={identity.user.owner_groups} />;
}
