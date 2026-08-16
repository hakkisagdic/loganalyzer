using System.Runtime.CompilerServices;
using Bizigo.Ingest.Text;

namespace Bizigo.UnitTests;

internal static class TestModuleInitializer
{
    /// <summary>
    /// Legacy kod sayfalarını derleme başına <b>bir kez</b> kaydeder.
    ///
    /// <para>
    /// Neden burada: kayıt süreç genelinde bir kereliktir. Bir test sınıfının
    /// statik kurucusuna bırakılırsa, <c>windows-1254</c> kullanan diğer testler
    /// yalnızca <i>o sınıf önce koştuğu için</i> geçer. Yerelde geçip CI'da
    /// patlayan bu davranış gerçekten yaşandı: sıraya bağlı bir testin "geçmesi"
    /// bir bilgi taşımıyor.
    /// </para>
    ///
    /// <para>
    /// Ürün kodu bu kancaya bağlı değil — <c>EncodingDetector</c> kendi statik
    /// kurucusunda ve <c>AddBizigoIngest</c> içinde ayrıca kaydediyor.
    /// </para>
    /// </summary>
    [ModuleInitializer]
    public static void Initialize() => EncodingDetector.RegisterCodePages();
}
