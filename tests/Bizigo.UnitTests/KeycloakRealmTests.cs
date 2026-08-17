using System.Text.Json;

namespace Bizigo.UnitTests;

/// <summary>
/// Realm dosyasının <b>Keycloak tarafından kabul edilebilir</b> ve claim
/// sözleşmesini gerçekten taşıyor olması.
///
/// <para>
/// Bu sınıf, F1'in uçtan uca doğrulamasında art arda çıkan ve her biri bir
/// öncekini düzeltmeden görünmeyen kimlik hatalarının bekçisi. Hiçbiri konteyner
/// gerektirmiyor — hepsi dosyada okunabilir bir sözleşme ihlaliydi:
/// </para>
///
/// <list type="number">
/// <item><c>_comment</c> alanları — Keycloak bilinmeyen alanı reddedip
/// <b>hiç başlamıyordu</b>.</item>
/// <item><c>postLogoutRedirectUris</c> istemci alanı değil; <c>attributes</c>
/// içine giriyor. Yine başlamama.</item>
/// <item>İstemciler var olmayan scope'lara referans veriyordu; import onları
/// <b>sessizce</b> düşürüyor ve token yarım kalıyordu.</item>
/// <item>Yönetici kullanıcıda <c>firstName</c>/<c>lastName</c> yoktu; Keycloak
/// hesabı "not fully set up" sayıp girişi reddediyordu.</item>
/// <item><c>KC_HOSTNAME</c> ayarlı değildi; issuer isteğin host'undan türeyince
/// collector'ın gönderdiği her satır 401 ile düşüyordu.</item>
/// </list>
/// </summary>
public sealed class KeycloakRealmTests
{
    private static readonly JsonDocument Realm =
        JsonDocument.Parse(File.ReadAllText(RepositoryLayout.RealmFile));

    private static JsonElement Root => Realm.RootElement;

    private static IEnumerable<JsonElement> Clients => Root.GetProperty("clients").EnumerateArray();

    private static IEnumerable<JsonElement> Users => Root.GetProperty("users").EnumerateArray();

    /// <summary>
    /// Keycloak'ın import'u <b>bilinmeyen alanı reddediyor</b> — tolere etmiyor,
    /// yok saymıyor: sunucu hiç başlamıyor. Yorumlar bu yüzden JSON'da değil,
    /// yanındaki README'de duruyor.
    /// </summary>
    [Fact]
    public void Realm_dosyasinda_yorum_alani_yok()
    {
        var offenders = new List<string>();
        Walk(Root, string.Empty, offenders);

        Assert.True(
            offenders.Count == 0,
            "Keycloak bunları reddedip başlamayı bırakır: " + string.Join(", ", offenders));

        static void Walk(JsonElement node, string path, List<string> found)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in node.EnumerateObject())
                    {
                        if (property.Name.StartsWith('_'))
                        {
                            found.Add($"{path}.{property.Name}");
                        }

                        Walk(property.Value, $"{path}.{property.Name}", found);
                    }

                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in node.EnumerateArray())
                    {
                        Walk(item, $"{path}[{index++}]", found);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// <c>postLogoutRedirectUris</c> <c>ClientRepresentation</c> alanı değil;
    /// Keycloak onu <c>attributes["post.logout.redirect.uris"]</c> altında
    /// <c>##</c> ayraçlı tek dize olarak tutuyor. Üst düzeyde yazmak import'u
    /// düşürüyor.
    /// </summary>
    [Fact]
    public void Istemcilerde_ust_duzey_postLogoutRedirectUris_yok()
    {
        foreach (var client in Clients)
        {
            Assert.False(
                client.TryGetProperty("postLogoutRedirectUris", out _),
                $"{client.GetProperty("clientId")}: attributes['post.logout.redirect.uris'] kullanılmalı.");
        }
    }

    /// <summary>
    /// Realm dosyası <c>clientScopes</c> verdiği için Keycloak <b>yerleşik
    /// scope'ları hiç oluşturmuyor</b>. Var olmayan bir scope'a referans vermek
    /// hata üretmiyor — sessizce düşüyor ve token eksik claim'le çıkıyor.
    /// </summary>
    [Fact]
    public void Istemciler_yalnizca_var_olan_scopelara_referans_veriyor()
    {
        var defined = Root.GetProperty("clientScopes")
            .EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        // Keycloak her realm'de kendiliğinden oluşturuyor.
        defined.Add("offline_access");

        foreach (var client in Clients)
        {
            if (!client.TryGetProperty("defaultClientScopes", out var scopes))
            {
                continue;
            }

            foreach (var scope in scopes.EnumerateArray().Select(s => s.GetString()!))
            {
                Assert.True(
                    defined.Contains(scope),
                    $"{client.GetProperty("clientId")}: '{scope}' realm'de tanımlı değil, "
                    + "import onu sessizce düşürür.");
            }
        }
    }

    /// <summary>
    /// Claim sözleşmesinin tamamı tek scope'ta. <c>sub</c> ve
    /// <c>preferred_username</c> normalde Keycloak'ın <c>basic</c>/<c>profile</c>
    /// scope'larından gelir; onlar oluşmadığı için elle yazılmak zorunda.
    /// Eksik olduklarında yetkilendirme çalışmaya devam ediyor (roller yerinde)
    /// ama <b>denetim kimliği</b> kayboluyor — en sessiz kırılma biçimi.
    /// </summary>
    [Fact]
    public void Paylasilan_scope_bes_claimi_de_uretiyor()
    {
        var scope = Root.GetProperty("clientScopes")
            .EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "bizigo-claims");

        var mappers = scope.GetProperty("protocolMappers")
            .EnumerateArray()
            .Select(m => m.GetProperty("protocolMapper").GetString()!)
            .ToArray();

        Assert.Contains("oidc-usermodel-realm-role-mapper", mappers);  // roles
        Assert.Contains("oidc-group-membership-mapper", mappers);      // groups
        Assert.Contains("oidc-audience-mapper", mappers);              // aud
        Assert.Contains("oidc-sub-mapper", mappers);                   // sub
        Assert.Contains("oidc-usermodel-property-mapper", mappers);    // preferred_username
    }

    /// <summary>
    /// <c>full.path=true</c> kapatılırsa iç içe gruplarda ad çakışması olur:
    /// <c>network/core</c> ile <c>platform/core</c> ayırt edilemez ve kapsam
    /// kapısı yanlış grubu eşler.
    /// </summary>
    [Fact]
    public void Grup_claimi_tam_yol_veriyor()
    {
        var mapper = Root.GetProperty("clientScopes")
            .EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "bizigo-claims")
            .GetProperty("protocolMappers")
            .EnumerateArray()
            .Single(m => m.GetProperty("protocolMapper").GetString() == "oidc-group-membership-mapper");

        Assert.Equal("true", mapper.GetProperty("config").GetProperty("full.path").GetString());
    }

    /// <summary>
    /// Keycloak'ın varsayılan kullanıcı profili <c>firstName</c> ve
    /// <c>lastName</c>'i zorunlu tutuyor; eksikken hesap "not fully set up"
    /// sayılıyor ve parola akışı <c>invalid_grant</c> veriyor. Servis hesapları
    /// muaf — onların insan adı yok.
    /// </summary>
    [Fact]
    public void Insan_kullanicilarinin_adi_soyadi_var()
    {
        foreach (var user in Users)
        {
            if (user.TryGetProperty("serviceAccountClientId", out _))
            {
                continue;
            }

            var username = user.GetProperty("username").GetString();

            Assert.True(
                user.TryGetProperty("firstName", out _) && user.TryGetProperty("lastName", out _),
                $"{username}: firstName/lastName eksik — Keycloak girişi reddeder.");
        }
    }

    /// <summary>
    /// Kapsam ayrımının gerçek gruplarla gösterilebilmesi için <b>iki farklı
    /// gruptaki iki kullanıcı</b> şart. Tek kullanıcıyla kapsam filtresinin
    /// çalıştığı gösterilemez — hepsini görüyor olsa da test geçerdi.
    /// </summary>
    [Fact]
    public void Farkli_gruplarda_iki_analist_var()
    {
        var groups = Users
            .Where(u => u.TryGetProperty("groups", out _))
            .SelectMany(u => u.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(groups.Length >= 2, "Kapsam ayrımı tek grupla gösterilemez: " + string.Join(", ", groups));
        Assert.All(groups, g => Assert.StartsWith("/", g, StringComparison.Ordinal));
    }

    /// <summary>
    /// Collector kimliği sızarsa veri <b>yazılabilir, okunamaz</b> olmalı.
    /// Rol ayrımının tek sebebi bu; servis hesabına okuma rolü eklemek onu
    /// anlamsız kılar.
    /// </summary>
    [Fact]
    public void Collector_servis_hesabi_yalnizca_ingest_rolu_tasiyor()
    {
        var account = Users.Single(u =>
            u.TryGetProperty("serviceAccountClientId", out var c) && c.GetString() == "bizigo-collector");

        var roles = account.GetProperty("realmRoles").EnumerateArray().Select(r => r.GetString()!).ToArray();

        Assert.Equal(["ingest"], roles);
    }

    /// <summary>
    /// <c>KC_HOSTNAME</c> issuer'ı sabitliyor. Olmadığında Keycloak issuer'ı
    /// isteğin geldiği host'tan türetiyor: collector ağ içinden token alıyor,
    /// API dışarıdan doğruluyor ve <b>her satır 401 ile düşüyor</b>.
    /// </summary>
    [Fact]
    public void Compose_issueri_sabitliyor()
    {
        var compose = File.ReadAllText(RepositoryLayout.ComposeFile);

        Assert.Contains("KC_HOSTNAME", compose, StringComparison.Ordinal);
    }
}
