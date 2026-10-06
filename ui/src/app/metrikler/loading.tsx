import { AppShell } from "@/components/AppShell";
import { LoadingState } from "@/components/ui/States";

export default function Loading() {
  return (
    <AppShell>
      <h1>Metrikler</h1>
      <LoadingState label="Metrikler yükleniyor" rows={8} />
    </AppShell>
  );
}
