import { redirect } from "next/navigation";

import { currentUser } from "@/lib/auth/currentUser";

import { AlertRuleLoader } from "../AlertRuleLoader";

export const dynamic = "force-dynamic";

/** Kural düzenleme. Kanal bağlantıları da yükleniyor — yoksa kaydetmede silinirlerdi. */
export default async function EditAlertRulePage({ params }: { params: Promise<{ id: string }> }) {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Falarmlar");
  }

  if (identity.status === "error") {
    return null;
  }

  const { id } = await params;

  return (
    <AlertRuleLoader
      ruleId={id}
      ownerGroups={identity.user.owner_groups}
      unrestricted={identity.user.unrestricted}
    />
  );
}
