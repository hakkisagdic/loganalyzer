import { redirect } from "next/navigation";

import { AppShell } from "@/components/AppShell";
import { Card } from "@/components/ui/Field";
import { EmptyState, ErrorState } from "@/components/ui/States";
import type { EventSearchResult, SourceList, SourceItem } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";
import { NoSessionError, serverApi } from "@/lib/api/server";
import { currentUser } from "@/lib/auth/currentUser";
import {
  MIN_FULL_TEXT_LENGTH,
  PARAM,
  advisePagination,
  judgeQuery,
  readCriteria,
  toSearchBody,
  toSearchParams,
} from "@/lib/events/criteria";

import { ResultsTable } from "./ResultsTable";
import { SavedSearches } from "./SavedSearches";
import { SearchForm } from "./SearchForm";
import styles from "./events.module.css";

export const dynamic = "force-dynamic";

/**
 * Log arama ekranı (T15) — ürünün en çok kullanılacak yeri.
 *
 * <p>
 * Veri <b>sunucuda</b> çekiliyor: tarayıcı <c>Bizigo.Api</c>'yi hiç görmüyor ve
 * erişim token'ı bu dosyanın çalıştığı süreçten dışarı çıkmıyor. Sonuç HTML
 * olarak iniyor.
 * </p>
 *
 * <p>
 * Ekranın şeklini F1'de <b>ölçülen</b> iki kısıt belirliyor ve ikisi de burada
 * görünür: kısa sorgu eşiği (indeks ~10-11 karakterden sonra seçici) ve keyset
 * sayfalamanın kaynak filtresi gereksinimi (filtresiz derin sayfa 1M satır,
 * kaynak filtresiyle 57k).
 * </p>
 */

type RawParams = Record<string, string | string[] | undefined>;

export default async function EventSearchPage({
  searchParams,
}: {
  searchParams: Promise<RawParams>;
}) {
  const params = await searchParams;
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    // YALNIZCA oturumsuz hâl girişe gidiyor. "API cevap vermiyor" da buraya
    // bağlansaydı giriş → sayfa → giriş döngüsü oluşurdu (T13'ün dersi).
    redirect(`/api/auth/login?returnTo=${encodeURIComponent(currentHref(params))}`);
  }

  if (identity.status === "error") {
    return (
      <AppShell>
        <ErrorState title={identity.message} hint={identity.hint} />
      </AppShell>
    );
  }

  const user = identity.user;
  const criteria = readCriteria(params);
  const verdict = judgeQuery(criteria);
  const advice = advisePagination(criteria);

  if (user.sees_nothing) {
    return (
      <AppShell username={user.username || user.subject}>
        <h1>Log arama</h1>
        <ErrorState
          title="Hiçbir gruba eşlenmediğiniz için veri göremiyorsunuz."
          hint="Kontrol düzlemindeki grup → owner_group eşlemesi eksik olabilir; yöneticinize başvurun."
        />
      </AppShell>
    );
  }

  const sources = await loadSources();
  const result = verdict.kind === "too-short" ? undefined : await runSearch();

  return (
    <AppShell username={user.username || user.subject}>
      <h1>Log arama</h1>

      <Card>
        <SearchForm
          criteria={criteria}
          sources={sources.items}
          ownerGroups={user.owner_groups}
          unrestricted={user.unrestricted}
        />
      </Card>

      {sources.error ? (
        // Kaynak listesi gelmediyse arama yine çalışıyor, yalnızca filtre
        // açılır listesi boş. Sessiz bırakmak "kaynağım yok" sanmaya yol açardı.
        <p className={styles.noticeMuted}>
          Kaynak listesi alınamadı ({sources.error}); kaynak ve vendor filtreleri boş görünüyor.
        </p>
      ) : null}

      <SavedSearches currentHref={currentHref(params)} />

      {verdict.kind === "too-short" ? (
        <ShortQueryNotice length={verdict.length} params={params} />
      ) : null}

      {verdict.kind === "forced" ? (
        <p className={styles.noticeWarning} role="status">
          <b>Tam tarama yapılıyor.</b> {verdict.length} karakterlik sorgu indeksten
          faydalanmıyor; sonuç doğru ama 1M satırlık bir tabloda bütün satırlar okunuyor.
        </p>
      ) : null}

      {advice === "suggest" ? (
        <p className={styles.noticeMuted}>
          Kaynak seçmeden arayabilirsiniz, ama <b>derin sayfalama yavaşlar</b>: F1'de
          ölçüldü — filtresiz derin sayfa 1M satır okuyor, kaynak filtresiyle 57k.
        </p>
      ) : null}

      {advice === "warn" ? (
        <p className={styles.noticeWarning} role="status">
          <b>Kaynak filtresi olmadan sayfalıyorsunuz.</b> Keyset ancak
          <code> owner_group</code> + <code>source_id</code> verildiğinde sabit süreli;
          bu sayfa ilerledikçe her istek daha çok satır okuyacak.
        </p>
      ) : null}

      {result === undefined ? null : result.kind === "error" ? (
        <ErrorState title={result.message} hint={result.hint} />
      ) : result.page.events.length === 0 ? (
        <Card padded={false}>
          <EmptyState
            title="Bu ölçütlerle olay bulunamadı."
            description={
              criteria.cursor
                ? "Sayfalamanın sonuna gelmiş olabilirsiniz."
                : "Zaman aralığını genişletin ya da filtreleri gevşetin. Yalnızca kapsamınızdaki gruplar aranıyor."
            }
            action={
              <a className={styles.reset} href="/olaylar">
                Filtreleri sıfırla
              </a>
            }
          />
        </Card>
      ) : (
        <>
          <ResultsTable events={result.page.events} />
          <Pager params={params} next={result.page.next} hasMore={result.page.has_more} />
        </>
      )}
    </AppShell>
  );

  async function loadSources(): Promise<{ items: SourceItem[]; error?: string }> {
    try {
      const list = (await serverApi.get("/v1/sources")) as SourceList;
      return { items: [...list.sources] };
    } catch (error) {
      if (error instanceof NoSessionError) {
        throw error;
      }

      return { items: [], error: error instanceof Error ? error.message : "bilinmeyen hata" };
    }
  }

  async function runSearch(): Promise<
    { kind: "ok"; page: EventSearchResult } | { kind: "error"; message: string; hint?: string }
  > {
    try {
      const page = (await serverApi.post("/v1/events/search", {
        body: toSearchBody(criteria),
      })) as EventSearchResult;

      return { kind: "ok", page };
    } catch (error) {
      if (error instanceof ApiError) {
        return { kind: "error", message: error.message, hint: error.hint };
      }

      return {
        kind: "error",
        message: "Arama çalıştırılamadı.",
        hint: error instanceof Error ? error.message : undefined,
      };
    }
  }
}

/** Kayıtlı aramanın ve giriş dönüşünün ihtiyaç duyduğu tam adres. */
function currentHref(params: RawParams): string {
  const search = new URLSearchParams();

  for (const [key, value] of Object.entries(params)) {
    for (const entry of Array.isArray(value) ? value : value === undefined ? [] : [value]) {
      search.append(key, entry);
    }
  }

  return search.size > 0 ? `/olaylar?${search.toString()}` : "/olaylar";
}

/**
 * Kısa sorgu uyarısı — sorgu <b>koşulmadan</b> gösteriliyor.
 *
 * <p>Sessizce kabul etmek yasak (T15 kabul kriteri): yazılan her kısa kelime
 * 1M satırda tam tarama demek. Israr mümkün ama açık bir eylem gerektiriyor,
 * yani maliyet bilinerek ödeniyor.</p>
 */
function ShortQueryNotice({ length, params }: { length: number; params: RawParams }) {
  const forced = new URLSearchParams();

  for (const [key, value] of Object.entries(params)) {
    for (const entry of Array.isArray(value) ? value : value === undefined ? [] : [value]) {
      forced.append(key, entry);
    }
  }

  forced.set(PARAM.force, "1");

  return (
    <ErrorState
      title={`Arama metni çok kısa (${length} karakter).`}
      hint={`Tam metin indeksi ~${MIN_FULL_TEXT_LENGTH} karakterden sonra seçici oluyor. Daha kısa bir sorgu 1M satırlık tabloda bütün satırları okur; bu yüzden sorgu çalıştırılmadı. Sınır alfabeden bağımsız — "kullanıcı" (9) de atlamıyor.`}
      action={
        <a className={styles.reset} href={`/olaylar?${forced.toString()}`}>
          Yine de ara (tam tarama)
        </a>
      }
    />
  );
}

/**
 * Keyset sayfalama. <b>Offset yok</b>: derin sayfada çöküyor (F1). "Sonraki"
 * bir bağlantı, çünkü imleç de ölçütlerin bir parçası ve adres çubuğunda
 * durması gerekiyor.
 */
function Pager({
  params,
  next,
  hasMore,
}: {
  params: RawParams;
  next: EventSearchResult["next"];
  hasMore: boolean;
}) {
  const criteria = readCriteria(params);
  const first = toSearchParams({ ...criteria, cursor: undefined });

  const nextParams = next
    ? toSearchParams({
        ...criteria,
        cursor: { afterTimestamp: next.after_timestamp, afterEventId: next.after_event_id },
      })
    : undefined;

  return (
    <nav className={styles.pager} aria-label="Sayfalama">
      {criteria.cursor ? (
        <a className={styles.reset} href={`/olaylar?${first.toString()}`}>
          ← İlk sayfa
        </a>
      ) : (
        <span />
      )}

      {hasMore && nextParams ? (
        <a className={styles.pagerNext} href={`/olaylar?${nextParams.toString()}`}>
          Sonraki sayfa →
        </a>
      ) : (
        <span className={styles.muted}>Son sayfa</span>
      )}
    </nav>
  );
}
