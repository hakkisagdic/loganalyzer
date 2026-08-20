"use client";

import {
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
  type KeyboardEvent,
  type UIEvent,
} from "react";

import styles from "./ui.module.css";

/**
 * Satır numaralı, vurgulanmış, **satır içi hata işaretli** kod editörü.
 *
 * <p>
 * Ortak kitte, ekranın içinde değil: parser editörü (T19) ilk tüketicisi ama
 * katalog ekranının fark görünümü ve F3'ün Sigma kuralları aynı yüzeye
 * ihtiyaç duyacak. İkinci bir kopya, klavye ve ekran okuyucu davranışının iki
 * yerde ayrışması demek — ve o ayrışma yalnızca klavyeyle çalışan birinin
 * fark edeceği bir şey (T28).
 * </p>
 *
 * <p>
 * <b>Kütüphane yok.</b> Teknik basit: saydam metinli bir <c>&lt;textarea&gt;</c>
 * ile altındaki vurgulanmış <c>&lt;pre&gt;</c> üst üste duruyor. Bedeli, iki
 * elemanın font ölçülerinin birebir aynı kalması zorunluluğu — ortak sınıf
 * (<c>editorSurface</c>) bunu tek yerde tutuyor.
 * </p>
 *
 * <p>
 * <b>Erişilebilirlik:</b> vurgulama katmanı <c>aria-hidden</c> — ekran okuyucu
 * metni <c>&lt;textarea&gt;</c>'den okuyor, iki kez değil. Hata işaretleri
 * yalnızca renkle değil <b>metinle</b> de veriliyor: oluk işareti taşıyor ve
 * hatalar editörün altında ayrıca listeleniyor, çünkü kırmızı bir satır
 * numarası kırmızıyı göremeyen için hiçbir şey söylemiyor.
 * </p>
 */

export interface EditorMarker {
  /** 1'den başlayan satır numarası — sunucunun şema hatasıyla aynı sayım. */
  readonly line: number;
  readonly message: string;
}

export interface CompletionOption {
  readonly name: string;
  readonly hint?: string;
}

export interface CodeEditorProps {
  readonly label: string;
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly hint?: string;
  readonly markers?: readonly EditorMarker[];
  /** Satırı belirteçlere ayıran fonksiyon; verilmezse vurgulama yapılmıyor. */
  readonly tokenize?: (line: string) => readonly { text: string; kind: string }[];
  /**
   * İmleç konumuna göre tamamlama önerileri. `null` dönerse menü kapalı.
   *
   * <p>Saf fonksiyon bekleniyor: menü her tuşta yeniden hesaplanıyor ve ağ
   * isteği ya da yan etki, yazmayı takılmalı hâle getirirdi.</p>
   */
  readonly complete?: (text: string, caret: number) => { prefix: string; options: readonly CompletionOption[] } | null;
  readonly spellCheck?: boolean;
  readonly disabled?: boolean;
}

const tokenClass: Record<string, string> = {
  comment: styles.tokenComment!,
  key: styles.tokenKey!,
  string: styles.tokenString!,
  template: styles.tokenTemplate!,
  number: styles.tokenNumber!,
  keyword: styles.tokenKeyword!,
  punct: styles.tokenPunct!,
  text: styles.tokenText!,
};

export function CodeEditor({
  label,
  value,
  onChange,
  hint,
  markers = [],
  tokenize,
  complete,
  spellCheck = false,
  disabled = false,
}: CodeEditorProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-errors`;
  const listId = `${id}-completions`;

  const inputRef = useRef<HTMLTextAreaElement>(null);
  const highlightRef = useRef<HTMLPreElement>(null);
  const gutterRef = useRef<HTMLDivElement>(null);

  const [completion, setCompletion] = useState<{
    prefix: string;
    options: readonly CompletionOption[];
  } | null>(null);
  const [selected, setSelected] = useState(0);

  const lines = useMemo(() => value.split("\n"), [value]);

  /**
   * Satır → hata mesajları. `Map` kuruluyor çünkü oluk her satır için
   * sorguluyor; dizi üzerinde arama, uzun bir YAML'da her tuşta ikinci
   * dereceden iş demekti.
   */
  const markersByLine = useMemo(() => {
    const map = new Map<number, string[]>();

    for (const marker of markers) {
      const existing = map.get(marker.line);
      if (existing) {
        existing.push(marker.message);
      } else {
        map.set(marker.line, [marker.message]);
      }
    }

    return map;
  }, [markers]);

  const syncScroll = useCallback((event: UIEvent<HTMLTextAreaElement>) => {
    const { scrollTop, scrollLeft } = event.currentTarget;

    if (highlightRef.current) {
      highlightRef.current.scrollTop = scrollTop;
      highlightRef.current.scrollLeft = scrollLeft;
    }

    // Oluk yatayda kaymıyor: satır numaraları sabit sütunda kalmalı.
    if (gutterRef.current) {
      gutterRef.current.scrollTop = scrollTop;
    }
  }, []);

  const refresh = useCallback(
    (text: string, caret: number) => {
      const next = complete?.(text, caret) ?? null;
      setCompletion(next);
      setSelected(0);
    },
    [complete],
  );

  function handleChange(event: ChangeEvent<HTMLTextAreaElement>) {
    onChange(event.target.value);
    refresh(event.target.value, event.target.selectionStart);
  }

  const apply = useCallback(
    (option: CompletionOption) => {
      const input = inputRef.current;
      if (!input || !completion) return;

      const caret = input.selectionStart;
      const start = caret - completion.prefix.length;
      const next = value.slice(0, start) + option.name + value.slice(caret);

      onChange(next);
      setCompletion(null);

      // Odak ve imleç eklenen metnin sonuna: seçimden sonra kullanıcının
      // devam edeceği yer orası, listenin kapanmasıyla imlecin kaybolması
      // yazmayı kesintiye uğratırdı.
      const position = start + option.name.length;
      queueMicrotask(() => {
        input.focus();
        input.setSelectionRange(position, position);
      });
    },
    [completion, onChange, value],
  );

  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (!completion) {
      // Tamamlama kapalıyken Tab odağı taşımalı — bir editörde Tab'ı yakalamak,
      // klavyeyle gezen kullanıcıyı alanın içine hapseder.
      return;
    }

    if (event.key === "ArrowDown") {
      event.preventDefault();
      setSelected((current) => (current + 1) % completion.options.length);
      return;
    }

    if (event.key === "ArrowUp") {
      event.preventDefault();
      setSelected((current) => (current - 1 + completion.options.length) % completion.options.length);
      return;
    }

    if (event.key === "Enter" || event.key === "Tab") {
      const option = completion.options[selected];
      if (option) {
        event.preventDefault();
        apply(option);
      }
      return;
    }

    if (event.key === "Escape") {
      event.preventDefault();
      setCompletion(null);
    }
  }

  // Metin dışarıdan değiştiyse (taslak yüklendi, iskelet eklendi) açık kalan
  // menü artık başka bir yeri gösteriyor olurdu.
  useEffect(() => setCompletion(null), [markers]);

  const markerList = [...markersByLine.entries()].sort(([a], [b]) => a - b);
  const describedBy = [hint ? hintId : null, markerList.length > 0 ? errorId : null]
    .filter(Boolean)
    .join(" ");

  return (
    <div className={styles.field}>
      <label className={styles.fieldLabel} htmlFor={id}>
        {label}
      </label>

      <div className={styles.editor}>
        <div className={styles.editorGutter} ref={gutterRef} aria-hidden="true">
          {lines.map((_, index) => {
            const hasError = markersByLine.has(index + 1);

            return (
              <span
                key={index}
                className={`${styles.editorGutterLine} ${hasError ? styles.editorGutterError : ""}`}
              >
                {hasError ? "●" : " "}
                {index + 1}
              </span>
            );
          })}
        </div>

        <div className={styles.editorStack}>
          <pre className={styles.editorHighlight} ref={highlightRef} aria-hidden="true">
            {lines.map((line, index) => (
              <span
                key={index}
                className={markersByLine.has(index + 1) ? styles.editorErrorLine : undefined}
              >
                {tokenize
                  ? tokenize(line).map((token, position) => (
                      <span key={position} className={tokenClass[token.kind] ?? styles.tokenText}>
                        {token.text}
                      </span>
                    ))
                  : line}
                {"\n"}
              </span>
            ))}
          </pre>

          <textarea
            id={id}
            ref={inputRef}
            className={styles.editorInput}
            value={value}
            onChange={handleChange}
            onKeyDown={handleKeyDown}
            onScroll={syncScroll}
            onClick={(event) => refresh(value, event.currentTarget.selectionStart)}
            onBlur={() => setCompletion(null)}
            spellCheck={spellCheck}
            disabled={disabled}
            aria-describedby={describedBy || undefined}
            aria-invalid={markerList.length > 0 ? true : undefined}
            aria-autocomplete={complete ? "list" : undefined}
            aria-controls={completion ? listId : undefined}
            aria-expanded={complete ? completion !== null : undefined}
          />

          {completion ? (
            <ul className={styles.completions} id={listId} role="listbox" aria-label="Şema önerileri">
              {completion.options.slice(0, 12).map((option, index) => (
                <li
                  key={option.name}
                  className={styles.completionItem}
                  role="option"
                  aria-selected={index === selected}
                  // `onMouseDown`, `onClick` değil: tıklama `onBlur`'dan sonra
                  // gelir ve o ana kadar menü kapanmış olurdu.
                  onMouseDown={(event) => {
                    event.preventDefault();
                    apply(option);
                  }}
                >
                  <span className={styles.completionName}>{option.name}</span>
                  {option.hint ? <span className={styles.completionHint}>{option.hint}</span> : null}
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      </div>

      {hint ? (
        <p className={styles.fieldHint} id={hintId}>
          {hint}
        </p>
      ) : null}

      {markerList.length > 0 ? (
        // Oluktaki işaret görsel; asıl bilgi burada ve ekran okuyucuya da
        // ulaşıyor. Renk tek başına anlam taşımamalı (WCAG 1.4.1).
        <ul className={styles.fieldError} id={errorId}>
          {markerList.map(([line, messages]) => (
            <li key={line}>
              <strong>Satır {line}:</strong> {messages.join(" · ")}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
