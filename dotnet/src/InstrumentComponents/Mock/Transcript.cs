using System.Text.Json;
using System.Text.Json.Serialization;
using InstrumentComponents.Errors;

namespace InstrumentComponents.Mock;

public sealed partial class Transcript
{
    public List<ScriptStep> Steps { get; set; } = new();

    public static Transcript FromSteps(IReadOnlyList<ScriptStep> steps) => new() { Steps = steps.ToList() };

    public string ToJson() => JsonSerializer.Serialize(new TranscriptDto { Steps = Steps.Select(StepToDto).ToList() }, TranscriptJsonContext.Default.TranscriptDto);

    public static Transcript FromJson(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize(json, TranscriptJsonContext.Default.TranscriptDto)
                ?? throw new ParseException("empty transcript");
            return new Transcript { Steps = dto.Steps.Select(DtoToStep).ToList() };
        }
        catch (JsonException ex)
        {
            throw new ParseException(ex.Message);
        }
    }

    private static StepDto StepToDto(ScriptStep step) => step switch
    {
        WriteStep w => new StepDto { Op = "write", Data = w.Data },
        ReadStep r => new StepDto { Op = "read", Data = r.Data },
        ClearStep => new StepDto { Op = "clear" },
        _ => throw new ParseException("unknown step"),
    };

    private static ScriptStep DtoToStep(StepDto dto) => dto.Op switch
    {
        "write" => new WriteStep { Data = dto.Data ?? "" },
        "read" => new ReadStep { Data = dto.Data ?? "" },
        "clear" => new ClearStep(),
        _ => throw new ParseException($"unknown op: {dto.Op}"),
    };

    private sealed class TranscriptDto
    {
        public List<StepDto> Steps { get; set; } = new();
    }

    private sealed class StepDto
    {
        public string Op { get; set; } = "";
        public string? Data { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase,WriteIndented=true)]
    [JsonSerializable(typeof(TranscriptDto))]
    private partial class TranscriptJsonContext:JsonSerializerContext { }
}
