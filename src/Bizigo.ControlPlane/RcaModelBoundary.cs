namespace Bizigo.ControlPlane;

/// <summary>
/// <b>Bir koşumun model sınırı hakkında söylenmiş olan şey</b> — kapalı küme
/// (T54).
///
/// <para>
/// Sorduğu soru <i>"muafiyet var mı"</i> değil, bir adım daha geride:
/// <b>bu koşum hakkında sınır konusunda ne söylendi.</b> Aradaki fark bu
/// deponun defalarca bedelini ödediği sınıf — <c>bool</c> bir alan
/// <i>"muafiyet yok"</i> ile <i>"kimse bakmadı"</i>yı aynı bayta indirirdi ve
/// ikisi taban tabana zıt: birincisi güvence, ikincisi ölçümsüzlük.
/// </para>
///
/// <para>
/// <b>Neden dört değer ve dördü de gerekli.</b> Satır
/// <see cref="RcaRunEntity"/> olarak kabul anında doğuyor, damga ise koşum
/// gerçekten <i>başladığında</i> basılıyor; reddedilen bir koşum hiç
/// başlamıyor. Yani "henüz bir şey söylenmedi" gerçek bir hâl ve
/// <see cref="NotEngaged"/> ile birleştirilemez: reddedilen koşum modele
/// konuşmadı, ama <b>hiçbir şey</b> yapmadı — onu "modele konuşmadı" diye
/// kaydetmek doğru bir cümleyi ölçülmemiş bir yerden söylemek olurdu.
/// </para>
/// </summary>
public enum RcaModelBoundary
{
    /// <summary>
    /// <b>Henüz kimse bir şey söylemedi.</b> Koşum kuyrukta ya da reddedildi;
    /// damganın basıldığı an hiç gelmedi.
    ///
    /// <para>
    /// Sıfır olması bilinçli ve <b>tersi tehlikeli olurdu</b>: yeni bir satırın
    /// varsayılanı <see cref="Verified"/> olsaydı, hiç doğrulanmamış bir uç
    /// hakkında veritabanının kendisi bir güvence beyan ederdi (M06'nın
    /// <c>DataBoundary.Unspecified</c> gerekçesi, aynı eksen).
    /// </para>
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Koşum başladı ve <b>modele hiç konuşmadı</b> — söylenecek bir sınır
    /// iddiası yok.
    ///
    /// <para>
    /// <b>Bugün üretimdeki tek değer</b>, ve bu bir eksiklik değil ölçülmüş bir
    /// olgu: <c>RcaScheduleWorker</c> kanıt topluyor, akıl yürütmeyi
    /// çağırmıyor. Bir sınır iddiası uydurmak yerine yokluğunu yazmak, bu
    /// ticket'ın taşıyıcı kararı.
    /// </para>
    /// </summary>
    NotEngaged = 1,

    /// <summary>
    /// Uç <b>adres sınıfına karşı doğrulandı</b>; muafiyet kullanılmadı.
    ///
    /// <para>
    /// Bu hâl de <b>yazılıyor</b>, sessiz bırakılmıyor: yalnızca muafiyet varken
    /// dolan bir alan, muafiyetsiz koşumu <i>"bu soru sorulmamış"</i> hâline
    /// sokardı — T38'in "gizlenen sıfır" kararının aynısı.
    /// </para>
    /// </summary>
    Verified = 2,

    /// <summary>
    /// <b>Muafiyet uygulandı</b> — adres doğrulaması atlandı ve gerekçesi
    /// <see cref="RcaRunEntity.ModelBoundaryOverrideReason"/>'da.
    ///
    /// <para>
    /// Gerekçesiz bu değere ulaşmak <b>derlenmiyor</b>: bu değeri yazan tek yol
    /// <c>Bizigo.Rca.RcaModelBoundaryStamp</c> ve onun tek muafiyet fabrikası
    /// gerekçeyi ucun kendisinden alıyor, boş gerekçeyi reddediyor. Damga tipi
    /// bu derlemede <b>değil</b> ve olamaz: ucu tanıması gerekiyor, oysa
    /// bağımlılık yönü <c>Bizigo.Rca → Bizigo.ControlPlane</c>. Kapalı küme
    /// burada (varlığın kolonu), kurucusu orada (ucu tanıyan katman).
    /// </para>
    /// </summary>
    Overridden = 3,
}
