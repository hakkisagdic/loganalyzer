import { redirect } from "next/navigation";

import { currentUser } from "@/lib/auth/currentUser";

import { AlertRuleLoader } from "../AlertRuleLoader";

export const dynamic = "force-dynamic";

/**
 * Yeni kural.
 *
 * <p>Kapsam listesi <b>sunucudan</b> geliyor: kullanıcının seçebileceği
 * gruplar, istemcinin söylediği değil kimliğin taşıdığı kümedir. Sunucu
 * kaydederken aynı kuralı ayrıca zorluyor (T23 kabul kriteri) — ekran onu
 * tekrarlamıyor, görünür kılıyor.</p>
 */
export default async function NewAlertRulePage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Falarmlar%2Fyeni");
  }

  if (identity.status === "error") {
    // Düzen katmanı hatayı zaten gösteriyor; ikinci bir kopya çizmiyoruz.
    return null;
  }

  return (
    <AlertRuleLoader
      ruleId={null}
      ownerGroups={identity.user.owner_groups}
      unrestricted={identity.user.unrestricted}
    />
  );
}
