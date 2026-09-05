using System.Reflection;
using System.Reflection.Emit;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Derlenmiş IL'den çağrı okuyan ortak yüzey.</b>
///
/// <para>
/// İki bekçi aynı soruyu farklı kapsamlarda soruyor ve ikisi de bu okuyucuya
/// dayanıyor: <c>CompositionRootTests</c> (T50) <i>"bu uzantı kompozisyon
/// kökünün çağrı grafiğinde mi"</i>, <c>McpRedactionGateTests</c> (M06)
/// <i>"bu tipi kuran başka bir metot var mı"</i>.
/// </para>
///
/// <para>
/// <b>Neden ortak yüzey.</b> İlk hâli T50'nin içinde <c>private</c>
/// duruyordu ve M06 aynı makineye ihtiyaç duydu. Kopyalamak
/// <c>CLAUDE.md</c> §9'un yasakladığı şey — ve bu dosyanın özel bir tehlikesi
/// var: IL çözücü <b>sessizce yanlış cevap verebilen</b> bir makine.
/// Tanınmayan bir bayt görüp durduğunda "çağrı yok" diyor, ve iki kopyadan
/// biri düzeltilip diğeri düzeltilmediğinde <b>iki bekçi farklı şey görür</b>
/// — ikisi de yeşil kalarak.
/// </para>
///
/// <h3>Bu okuyucunun GÖREMEDİKLERİ</h3>
///
/// <list type="number">
/// <item><b>Yansımayla</b> yapılan çağrı ve nesne kurulumu. IL'de bir
/// <c>call</c>/<c>newobj</c> olarak durmuyor.</item>
/// <item><b>Ölü kod.</b> <c>if (false)</c> içindeki bir çağrı IL'de duruyor ve
/// burada "var" sayılıyor. Sorulan soru <i>"ulaşılabilir mi"</i> değil,
/// <i>"çağrı grafiğinde var mı"</i>.</item>
/// <item><b>Kaynak üreteçlerinin</b> çalışma anında kurduğu bağlar.</item>
/// </list>
///
/// <para>
/// Çözülemeyen tokenlar <b>sessizce atlanmıyor</b>: sayılıyor ve çağıran bekçi
/// onu kapsam beyanında bildiriyor. Çözülemeyen her token, görülemeyen bir
/// çağrı demek.
/// </para>
/// </summary>
public static class IlCallReader
{
    private static readonly Dictionary<short, OpCode> OpcodeTable = BuildOpcodeTable();

    private static Dictionary<short, OpCode> BuildOpcodeTable()
    {
        var table = new Dictionary<short, OpCode>();

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opcode)
            {
                table[opcode.Value] = opcode;
            }
        }

        return table;
    }

    /// <summary>
    /// <paramref name="method"/>'un gövdesinden çözülebilen tüm çağrılar.
    /// Çözülemeyen tokenlar <paramref name="unresolved"/>'a ekleniyor.
    /// </summary>
    public static IEnumerable<MethodBase> Callees(MethodBase method, ref int unresolved)
    {
        ArgumentNullException.ThrowIfNull(method);

        byte[]? il;

        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            // Soyut, extern ya da gövdesi okunamayan metot: çağrısı yok.
            il = null;
        }

        if (il is null)
        {
            return [];
        }

        var typeArguments = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;

        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var callees = new List<MethodBase>();
        var failures = 0;

        foreach (var token in MethodTokens(il))
        {
            try
            {
                if (method.Module.ResolveMethod(token, typeArguments, methodArguments) is { } callee)
                {
                    callees.Add(callee);
                }
            }
            catch (Exception)
            {
                // Çözülemeyen token SESSİZCE atlanmıyor: sayılıyor ve çağıran
                // bekçi kapsamını beyan ederken bildiriyor.
                failures++;
            }
        }

        unresolved += failures;
        return callees;
    }

    /// <summary>
    /// IL gövdesinden metot tokenlarını çıkarır: <c>call</c>, <c>callvirt</c>,
    /// <c>newobj</c>, <c>ldftn</c>, <c>ldvirtftn</c> — hepsi
    /// <c>InlineMethod</c> operandı taşıyor.
    /// </summary>
    public static IEnumerable<int> MethodTokens(byte[] il)
    {
        ArgumentNullException.ThrowIfNull(il);

        var index = 0;

        while (index < il.Length)
        {
            short code = il[index++];

            if (code == 0xFE)
            {
                if (index >= il.Length)
                {
                    yield break;
                }

                code = (short)(0xFE00 | il[index++]);
            }

            if (!OpcodeTable.TryGetValue(code, out var opcode))
            {
                // Tanınmayan bayt: gövdenin geri kalanı güvenle yürünemez.
                // Durmak, yanlış token üretmekten iyi.
                yield break;
            }

            if (opcode.OperandType == OperandType.InlineMethod)
            {
                if (index + 4 > il.Length)
                {
                    yield break;
                }

                yield return BitConverter.ToInt32(il, index);
            }

            var size = OperandSize(opcode, il, index);

            if (size < 0 || index + size > il.Length)
            {
                yield break;
            }

            index += size;
        }
    }

    private static int OperandSize(OpCode opcode, byte[] il, int index) => opcode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => index + 4 > il.Length
            ? -1
            : 4 + (4 * BitConverter.ToInt32(il, index)),
        _ => -1,
    };

    /// <summary>
    /// Bir tipin <b>bütün</b> üyeleri — <c>DeclaredOnly</c>, her görünürlük,
    /// örnek ve statik. Bekçilerin ortak ihtiyacı: <c>private</c> bir üyenin
    /// görülmemesi, kapının kaldırıldığını görmemek demek.
    /// </summary>
    public const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
}
