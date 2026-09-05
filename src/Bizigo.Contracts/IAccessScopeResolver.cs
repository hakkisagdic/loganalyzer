using System.Security.Claims;

namespace Bizigo.Contracts;

/// <summary>
/// Kimlikten kapsama çeviren <b>kapı</b> (K17).
///
/// <para>
/// <b>Neden bir arayüz, ve neden burada.</b> Çevrimin kendisi
/// <c>Bizigo.ControlPlane</c>'de yaşıyor ve orada kalmalı — eşleme tablosu
/// Postgres'te. Ama çevrimin <b>çağıranları</b> artık iki taşımada: REST uçları
/// (<c>ICurrentUser</c>) ve MCP araç çağrıları (M08). İkinci çağıran
/// <c>Bizigo.Mcp</c>'de ve o proje bilerek EF Core tanımıyor
/// (<c>Bizigo.Mcp.csproj</c> yorumundaki ölçüm: bir konsol aracı bir veri
/// katmanını taşımamalı).
/// </para>
///
/// <para>
/// Alternatif MCP tarafında ikinci bir çevrim yazmaktı ve bu deponun §9'u onu
/// adıyla yasaklıyor: <i>"İkinci kopya yazma."</i> Ayrışan iki çevrim, aynı
/// kişinin REST'ten ve MCP'den <b>farklı veri görmesi</b> demek — üstelik
/// sessizce.
/// </para>
///
/// <para>
/// Arayüz bilerek <b>tek metotlu ve saf</b>: <see cref="ClaimsPrincipal"/>
/// alıyor, <see cref="AccessScope"/> veriyor. HTTP bilmiyor, MCP bilmiyor.
/// </para>
/// </summary>
public interface IAccessScopeResolver
{
    /// <summary>
    /// Kimliğin kapsam karşılığı.
    ///
    /// <para>
    /// <b>Kapalı başlar:</b> <paramref name="principal"/> yoksa ya da kimlik
    /// doğrulanmamışsa <see cref="AccessScope.Denied"/>. "Kimlik yoksa her şeyi
    /// gör" bu üründe yapılabilecek en pahalı hata olurdu.
    /// </para>
    /// </summary>
    AccessScope Resolve(ClaimsPrincipal? principal);
}
