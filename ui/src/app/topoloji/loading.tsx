import { AppShell } from "@/components/AppShell";
import { LoadingState } from "@/components/ui/States";

export default function Loading() {
  return (
    <AppShell>
      <h1>Topoloji</h1>
      <LoadingState label="Topoloji grafiği ve düğümleri yükleniyor" rows={8} />
    </AppShell>
  );
}
