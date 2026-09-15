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
    /// BFF'in dönüş adresi realm dosyasında <b>birebir</b> yazılı olmak zorunda
    /// (K31).
    ///
    /// <para>
    /// Next tarafındaki yol <c>ui/src/app/signin-oidc/route.ts</c>; Keycloak
    /// listedekiyle tam eşleşmeyen bir <c>redirect_uri</c>'yi reddediyor ve
    /// hatayı kendi sayfasında gösteriyor — uygulamaya hiç dönmüyor, yani
    /// uygulama tarafında hiçbir log satırı çıkmıyor. İki dosyayı birbirine
    /// bağlayan tek şey bu test.
    /// </para>
    ///
    /// <para>Aynı gerekçe çıkış için: <c>post.logout.redirect.uris</c>
    /// eşleşmezse Keycloak "Invalid redirect uri" gösteriyor ve kullanıcı çıkış
    /// akışının ortasında kalıyor.</para>
    /// </summary>
    [Fact]
    public void Bizigo_ui_next_donus_adresini_taniyor()
    {
        var ui = Clients.Single(c => c.GetProperty("clientId").GetString() == "bizigo-ui");

        var redirects = ui.GetProperty("redirectUris")
            .EnumerateArray()
            .Select(u => u.GetString())
            .ToArray();

        Assert.Contains("http://localhost:3000/signin-oidc", redirects);

        var postLogout = ui.GetProperty("attributes")
            .GetProperty("post.logout.redirect.uris")
            .GetString()!
            .Split("##", StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("http://localhost:3000/*", postLogout);
    }

    /// <summary>
    /// API'nin OIDC dönüş adresi listede <b>kalmamalı</b>.
    ///
    /// <para>
    /// K31 ile <c>Bizigo.Api</c>'den cookie+OIDC işleyicileri kaldırıldı;
    /// <c>http://localhost:5080/signin-oidc</c> artık hiçbir şeyi karşılamıyor.
    /// Duran bir <c>redirect_uri</c> yalnızca saldırı yüzeyi: o adrese yönlenen
    /// bir yetkilendirme kodu, uygulamanın hiç görmediği bir uçta açığa çıkar.
    /// "Eski kurulumlar bozulmasın" gerekçesi tutmuyor, çünkü eski kurulum
    /// zaten çalışmıyor — o işleyici kodda yok.
    /// </para>
    /// </summary>
    [Fact]
    public void Kaldirilmis_API_OIDC_donus_adresi_realm_de_yok()
    {
        var ui = Clients.Single(c => c.GetProperty("clientId").GetString() == "bizigo-ui");

        var redirects = ui.GetProperty("redirectUris")
            .EnumerateArray()
            .Select(u => u.GetString())
            .ToArray();

        Assert.DoesNotContain("http://localhost:5080/signin-oidc", redirects);

        // Çıkış tarafı da aynı: API artık kimseyi karşılamıyor.
        var postLogout = ui.GetProperty("attributes")
            .GetProperty("post.logout.redirect.uris")
            .GetString()!;

        Assert.DoesNotContain("5080", postLogout, StringComparison.Ordinal);
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
    /// <b>MCP kitlesi realm'de ne basılıyorsa belgede de o yazıyor.</b>
    ///
    /// <para>
    /// Ölçülerek bulundu ve iki taraf <b>ayrışmıştı</b>: realm
    /// <c>http://localhost:5080/mcp</c> basıyordu, README ise beklenen
    /// <c>aud</c> olarak <c>bizigo-mcp</c> yazıyordu. İkisi de kendi içinde
    /// tutarlı, ve <b>hiçbir şey ikisini karşılaştırmıyordu</b>.
    /// </para>
    ///
    /// <para>
    /// Bedeli, bu ürünün en pahalı arıza biçimi: operatör belgedeki değeri
    /// <c>BIZIGO_MCP_RESOURCE</c>'a yazıyor, süreç <b>kalkıyor</b> — çünkü
    /// değişken dolu — ve her araç çağrısı <c>unauthenticated</c> dönüyor.
    /// Belirti bir kimlik hatası gibi duruyor; sebep iki dosyada duran iki
    /// dizge. Aynı olguyu iki yerde temsil etmenin sonucu (§9).
    /// </para>
    ///
    /// <para>
    /// Bekçi realm'i <b>kaynak</b> sayıyor: RFC 8707 kaynak göstergelerini URI
    /// olarak tanımlıyor, yani ayrışmada doğru olan taraf realm'di. Test
    /// belgeyi realm'e karşı okuyor — ters yön, belgeyi kaynak yapardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Mcp_kitlesi_realmde_ve_belgede_ayni()
    {
        var basilan = Root.GetProperty("clientScopes")
            .EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "bizigo-mcp")
            .GetProperty("protocolMappers")
            .EnumerateArray()
            .Single(m => m.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper")
            .GetProperty("config")
            .GetProperty("included.custom.audience")
            .GetString();

        Assert.False(
            string.IsNullOrWhiteSpace(basilan),
            "bizigo-mcp scope'u bir kitle basmıyor — MCP yüzeyi için belirteç üretilemez.");

        // Belgede yazan hâli. Tek satır, tek dizge — README bu değeri
        // operatöre veriyor ve operatörün başka bir kaynağı yok.
        var readme = File.ReadAllLines(Path.Combine(RepositoryLayout.Root, "README.md"))
            .Select(l => l.Trim())
            .SingleOrDefault(l => l.StartsWith("export BIZIGO_MCP_RESOURCE=", StringComparison.Ordinal));

        Assert.NotNull(readme);

        var belgedeki = readme!["export BIZIGO_MCP_RESOURCE=".Length..]
            .Split('#')[0]
            .Trim();

        Assert.Equal(basilan, belgedeki);
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

    /// <summary>
    /// Ekranın container'daki dönüş adresi, realm'in kabul ettiği adresle
    /// <b>birebir</b> aynı olmalı (T49).
    ///
    /// <para>
    /// Bağ realm dosyasında <c>redirectUris</c> olarak yazılı ve Keycloak onu
    /// <b>tam eşleşmeyle</b> sınıyor. Compose'daki <c>BFF_PUBLIC_URL</c>
    /// varsayılanı ondan ayrıldığı an giriş akışı <c>invalid_redirect_uri</c>
    /// ile duruyor — kullanıcının gördüğü şey Keycloak'ın hata sayfası ve orada
    /// hangi iki değerin ayrıştığı <b>yazmıyor</b>.
    /// </para>
    ///
    /// <para>
    /// Ayrışma tek bir dosyada da olmuyor: port compose'da üç yerde geçiyor
    /// (yayımlanan port, <c>BFF_PUBLIC_URL</c>, sağlık kontrolü) ve dördüncüsü
    /// realm dosyasında. Biri değiştirilip diğeri unutulduğunda derleme temiz,
    /// testler yeşil, yığın kalkıyor — ve yalnızca <b>giriş</b> kırık.
    /// </para>
    ///
    /// <para>
    /// Bekçi <c>ui</c> servisinin varsayılanını okuyor, ortam değişkeniyle
    /// geçersiz kılınabilen hâlini değil: kurulumuna göre başka bir adres
    /// veren kişi realm'i de kendisi ayarlıyor, ama <b>depodaki varsayılan</b>
    /// kutudan çıktığı gibi çalışmak zorunda.
    /// </para>
    /// </summary>
    [Fact]
    public void Compose_ekraninin_donus_adresi_realm_ile_ayni()
    {
        var compose = File.ReadAllText(RepositoryLayout.ComposeFile);

        // `BFF_PUBLIC_URL: ${BFF_PUBLIC_URL:-http://localhost:3000}` içinden
        // VARSAYILANI alıyoruz — compose'un `:-` sözdiziminin sağ tarafı.
        var match = System.Text.RegularExpressions.Regex.Match(
            compose,
            @"BFF_PUBLIC_URL:\s*\$\{BFF_PUBLIC_URL:-(?<value>[^}]+)\}");

        Assert.True(
            match.Success,
            "compose'da `ui` servisinin `BFF_PUBLIC_URL` varsayılanı bulunamadı — " +
            "ekran container'a girdiyse bu değer orada olmalı (T49).");

        var publicUrl = match.Groups["value"].Value.TrimEnd('/');

        var client = Clients.Single(c => c.GetProperty("clientId").GetString() == "bizigo-ui");
        var redirectUris = client.GetProperty("redirectUris")
            .EnumerateArray()
            .Select(u => u.GetString()!)
            .ToArray();

        Assert.Contains(
            $"{publicUrl}/signin-oidc",
            redirectUris,
            StringComparer.Ordinal);
    }
    /// <summary>
    /// <b>M09 · MCP kaynağı istekle geliyor, varsayılan olarak değil.</b>
    ///
    /// <para>
    /// <c>bizigo-mcp</c> client scope'u <c>bizigo-ui</c>'ye <b>isteğe bağlı</b>
    /// bağlanmış olmalı. Varsayılan olsaydı her token <c>aud</c>'una MCP
    /// kaynağını da alırdı ve <b>API için basılmış bir token <c>/mcp</c>'de
    /// geçerdi</b> — RFC 8707'nin kaynak bağlamasının engellemek istediği şey
    /// tam olarak bu. Yani bu satırın "isteğe bağlı" olması bir tercih değil,
    /// önlemin kendisi.
    /// </para>
    /// </summary>
    [Fact]
    public void Mcp_kaynak_scopeu_istege_bagli()
    {
        var scope = Root.GetProperty("clientScopes")
            .EnumerateArray()
            .SingleOrDefault(s => s.GetProperty("name").GetString() == "bizigo-mcp");

        Assert.True(
            scope.ValueKind is JsonValueKind.Object,
            "`bizigo-mcp` client scope'u realm'de yok — MCP sunucusunun kaynak kimliğini " +
            "basacak tek yer orası.");

        var ui = Clients.Single(c => c.GetProperty("clientId").GetString() == "bizigo-ui");

        Assert.Contains(
            "bizigo-mcp",
            ui.GetProperty("optionalClientScopes").EnumerateArray().Select(v => v.GetString()!),
            StringComparer.Ordinal);

        Assert.DoesNotContain(
            "bizigo-mcp",
            ui.GetProperty("defaultClientScopes").EnumerateArray().Select(v => v.GetString()!),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// <b>İki gösterim ayrışamaz.</b> Realm'in bastığı <c>aud</c> ile API'nin
    /// doğruladığı <c>Auth:McpResource</c> <b>birebir</b> aynı olmalı.
    ///
    /// <para>
    /// Ayrışırlarsa hiçbir hata alınmıyor: Keycloak token'ı basıyor, API
    /// reddediyor, ve görünen tek şey *"MCP çalışmıyor"* oluyor. T53'ün ölçtüğü
    /// sınıfın kimlik katmanındaki hâli — aynı şeyin iki gösterimi, ve arada
    /// bekçi yok.
    /// </para>
    /// </summary>
    [Fact]
    public void Realmin_bastigi_MCP_kitlesi_APInin_dogruladigiyla_ayni()
    {
        var mapped = Root.GetProperty("clientScopes")
            .EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "bizigo-mcp")
            .GetProperty("protocolMappers")
            .EnumerateArray()
            .Single(m => m.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper")
            .GetProperty("config")
            .GetProperty("included.custom.audience")
            .GetString();

        using var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(
                RepositoryLayout.Root, "src", "Bizigo.Api", "appsettings.json")));

        var configured = settings.RootElement
            .GetProperty("Auth")
            .GetProperty("McpResource")
            .GetString();

        Assert.Equal(configured, mapped, StringComparer.Ordinal);
    }
}
