import { AppShell } from "@/components/AppShell";
import { LoadingState } from "@/components/ui/States";

export default function Loading() {
  return (
    <AppShell>
      <h1>İzler</h1>
      <LoadingState label="İzler yükleniyor" rows={8} />
    </AppShell>
  );
}
