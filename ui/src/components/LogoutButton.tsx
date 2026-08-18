"use client";

import { useState } from "react";

import { Button } from "./ui/Button";

/**
 * Çıkış — hem Next oturumunu hem Keycloak oturumunu sonlandırıyor.
 *
 * <p>
 * İki adım: BFF önce sunucudaki oturumu siliyor ve çerezi düşürüyor, sonra
 * Keycloak'ın <c>end_session</c> adresini döndürüyor; tarayıcı oraya gidiyor.
 * Tek adım (doğrudan Keycloak'a yönlendirme) Next oturumunu ayakta bırakır ve
 * kullanıcı geri döndüğünde hâlâ girmiş olurdu.
 * </p>
 */
export function LogoutButton() {
  const [busy, setBusy] = useState(false);

  async function logout() {
    setBusy(true);

    try {
      const response = await fetch("/api/auth/logout", {
        method: "POST",
        credentials: "same-origin",
      });

      const body = (await response.json()) as { redirectTo?: string };
      window.location.assign(body.redirectTo ?? "/");
    } catch {
      // Keycloak'a ulaşılamadıysa bile yerel oturum silinmiş oluyor; kullanıcıyı
      // en azından giriş sayfasına alıyoruz.
      setBusy(false);
      window.location.assign("/giris");
    }
  }

  return (
    <Button variant="ghost" onClick={logout} disabled={busy}>
      {busy ? "Çıkılıyor…" : "Çıkış"}
    </Button>
  );
}
