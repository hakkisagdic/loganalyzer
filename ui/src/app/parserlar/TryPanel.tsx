"use client";

import { Badge, Card } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { toNumber } from "@/lib/api/numbers";
import {
  DISPATCH_TIER_LABELS,
  dispatchTier,
  parseStatusLabel,
  statusTone,
  type ParseOutcome,
  type ParserDispatch,
  type ParserTry,
} from "@/lib/parsers/types";

import styles from "./parsers.module.css";

export interface TryPanelProps {
  readonly result: ParserTry | null;
  readonly loading: boolean;
  readonly error: string | null;
  readonly hasLine: boolean;
}

/**
 * Canlı test sonucu (T19).
 *
 * <p>
 * İki sonuç <b>yan yana</b>: taslağın bu satırı nasıl çözdüğü ve aynı satırın
 * <b>bugünkü katalogda</b> ne yaptığı. Yalnızca ilkini göstermek, "taslağım
 * çalışıyor" ile "bu satır zaten başka bir parser'a düşüyor" arasındaki farkı
 * gizlerdi — ikincisi çoğu zaman taslağın hiç gerekmediği anlamına geliyor.
 * </p>
 */
export function TryPanel({ result, loading, error, hasLine }: TryPanelProps) {
  if (error) {
    return (
      <Card>
        <ErrorState title="Deneme başarısız" hint={error} />
      </Card>
    );
  }

  if (loading && !result) {
    return (
      <Card>
        <LoadingState label="Satır motorda deneniyor…" rows={3} />
      </Card>
    );
  }

  if (!hasLine) {
    return (
      <Card>
        <EmptyState
          title="Örnek satır yok"
          description="Ham arşivden gerçek bir satır çekin. Uydurma örnekle yazılan parser üretimde çuvallıyor — cihazın gerçekte yazdığı boşluklar, tırnaklar ve kodlama ancak gerçek satırda görünüyor."
        />
      </Card>
    );
  }

  if (!result) {
    return (
      <Card>
        <EmptyState title="Sonuç yok" description="Satır girildiğinde deneme kendiliğinden koşuyor." />
      </Card>
    );
  }

  return (
    <div className={styles.stack}>
      {result.result ? (
        <Card>
          <div className={styles.stack}>
            <div className={styles.toolbar}>
              <h2>Taslağın sonucu</h2>
              {loading ? <Badge>güncelleniyor…</Badge> : null}
            </div>
            <Outcome outcome={result.result} />
          </div>
        </Card>
      ) : (
        <Card>
          <EmptyState
            title="Taslak derlenemedi"
            description="Örnek satır ancak derlenen bir parser'la denenebiliyor. Şema hatalarını giderin; kapı raporu hangi satır olduğunu söylüyor."
          />
        </Card>
      )}

      {result.dispatch ? <DispatchCard dispatch={result.dispatch} /> : null}
    </div>
  );
}

/**
 * Dispatcher kademesi — ticket'ın taşıyıcı gözlemi.
 *
 * <p>
 * Envanter bağı yerine literal filtreye düşen satır, parser doğru olsa bile
 * <b>envanterin eksik</b> olduğunu söylüyor. Bu, sonucun kendisi kadar
 * bilgilendirici ve başka hiçbir yerde görünmüyor.
 * </p>
 */
function DispatchCard({ dispatch }: { readonly dispatch: ParserDispatch }) {
  const tier = dispatchTier(dispatch.tier);

  return (
    <Card>
      <div className={styles.stack}>
        <div className={styles.toolbar}>
          <h2>Bugünkü katalogda ne oluyor</h2>
          <Badge tone={tier === "inventory_bound" ? "success" : tier === "candidate" ? "warning" : "neutral"}>
            {DISPATCH_TIER_LABELS[tier]}
          </Badge>
        </div>

        <p className={styles.gateDetail}>{dispatch.reason}</p>

        <p className={styles.muted}>
          {toNumber(dispatch.attempts)} parser denendi.
          {tier === "candidate" && toNumber(dispatch.attempts) > 3
            ? " Deneme sayısının yüksek olması literal ön filtrenin yeterince daraltmadığını gösteriyor."
            : ""}
        </p>

        <Outcome outcome={dispatch.result} />
      </div>
    </Card>
  );
}

function Outcome({ outcome }: { readonly outcome: ParseOutcome }) {
  const sections: { key: string; title: string; values: Record<string, string> }[] = [
    { key: "core", title: "core", values: outcome.core },
    { key: "ocsf", title: "OCSF", values: outcome.ocsf },
    { key: "otel", title: "OTel", values: outcome.otel },
    { key: "fields", title: "Ham alanlar", values: outcome.fields },
  ];

  return (
    <div className={styles.stack}>
      <div className={styles.statusRow}>
        <Badge tone={statusTone(outcome.status)}>{parseStatusLabel(outcome.status)}</Badge>

        {outcome.parser_id ? (
          <span className={styles.muted}>
            <code>{outcome.parser_id}</code>
            {outcome.parser_version ? ` @ ${outcome.parser_version}` : ""}
          </span>
        ) : null}

        {outcome.timestamp ? (
          <span className={styles.muted}>
            zaman: <code>{outcome.timestamp}</code>
          </span>
        ) : (
          <span className={styles.muted}>zaman çözülmedi</span>
        )}
      </div>

      {/*
        Zaman aşımı DURUMDAN AYRI ve ayrı kalmak zorunda: sıfırdan farklıysa
        sonuç "uymadı" değil "ÖLÇÜLEMEDİ" demek. `matchTimeout` duvar saatini
        ölçüyor, yani yüklü bir makinede sağlıklı bir parser da düşüyor
        (T08 raporu #10). İkisini karıştırmak sağlıklı bir parser'ı karantinaya
        sokar — bu yüzden rozet değil, tam cümle.
      */}
      {outcome.timed_out ? (
        <p className={styles.timeout} role="alert">
          <strong>Ölçülemedi.</strong> En az bir pattern zaman aşımına uğradı. Bu, satırın parser'a{" "}
          <em>uymadığı</em> anlamına <strong>gelmiyor</strong>: <code>matchTimeout</code> duvar saatini
          ölçüyor ve makine yüklüyken sağlıklı bir pattern de aşabiliyor. Sonucu "uymadı" diye okumayın —
          önce yeniden deneyin, tekrarlıyorsa pattern'in doğrusal motorda derlendiğini doğrulayın.
        </p>
      ) : null}

      {outcome.tags.length > 0 ? (
        <p className={styles.tagRow}>
          {outcome.tags.map((tag) => (
            <Badge key={tag} tone="warning">
              {tag}
            </Badge>
          ))}
        </p>
      ) : null}

      {outcome.issues.length > 0 ? (
        <ul className={styles.issueList}>
          {outcome.issues.map((issue, index) => (
            <li key={`${issue.step}-${index}`}>
              <code>{issue.step}</code>: {issue.message}
            </li>
          ))}
        </ul>
      ) : null}

      {sections
        .filter((section) => Object.keys(section.values).length > 0)
        .map((section) => (
          <section key={section.key}>
            <h3 className={styles.sectionTitle}>{section.title}</h3>
            <DataTable
              caption={`${section.title} alanları`}
              rowKey={([name]) => name}
              rows={Object.entries(section.values).sort(([a], [b]) => a.localeCompare(b, "tr"))}
              columns={[
                { key: "ad", header: "Alan", width: "30%", render: ([name]) => <code>{name}</code> },
                { key: "deger", header: "Değer", freeText: true, render: ([, value]) => value },
              ]}
            />
          </section>
        ))}

      {sections.every((section) => Object.keys(section.values).length === 0) ? (
        <p className={styles.muted}>Hiçbir alan çözülmedi.</p>
      ) : null}
    </div>
  );
}
