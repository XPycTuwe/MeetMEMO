using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace MeetMemo.Storage;

/// <summary>
/// Единые настройки сериализации пакета встречи (ТЗ 12.2): UTF-8 без BOM, кириллица без \uXXXX,
/// время в ISO 8601. JSONL пишется без отступов — одна завершённая запись на строку.
/// </summary>
public static class JsonSetup
{
    public static readonly JsonSerializerOptions Compact = CreateCompact();

    public static readonly JsonSerializerOptions Pretty = new(Compact)
    {
        WriteIndented = true
    };

    private static JsonSerializerOptions CreateCompact()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic)
        };

        // Значения перечислений в пакете встречи пишутся в нижнем регистре через подчёркивание
        // ("application", "application_process_tree", "completed_with_warnings"): именно такой
        // вид зафиксирован в схемах и его ждут потребители пакета.
        options.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

        return options;
    }
}
