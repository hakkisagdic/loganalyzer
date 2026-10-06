import { AppShell } from "@/components/AppShell";
import { LoadingState } from "@/components/ui/States";

export default function Loading() {
  return (
    <AppShell>
      <h1>Anomaliler</h1>
      <LoadingState label="Anomali politikaları ve koşumları yükleniyor" rows={8} />
    </AppShell>
  );
}
