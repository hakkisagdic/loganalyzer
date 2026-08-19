import { redirect } from "next/navigation";

import { currentUser } from "@/lib/auth/currentUser";

import { ChannelManager } from "./ChannelManager";

export const dynamic = "force-dynamic";

export default async function ChannelsPage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Falarmlar%2Fkanallar");
  }

  if (identity.status === "error") {
    return null;
  }

  return <ChannelManager ownerGroups={identity.user.owner_groups} />;
}
