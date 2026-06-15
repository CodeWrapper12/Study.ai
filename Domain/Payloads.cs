using System.Text.Json;
using System.Text.Json.Serialization;

namespace Accelerator.Api.Domain;

/// <summary>
/// Typed payloads stored in LessonItem.PayloadJson.
/// One JSON column instead of six tables: lesson types evolve fast, payloads are
/// read as a unit, and PostgreSQL jsonb keeps them queryable if ever needed.
/// Server-side code always deserializes before exposing — quiz answers never
/// leave the API unstripped.
/// </summary>
public static class Payloads
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
    public static string Write<T>(T payload) => JsonSerializer.Serialize(payload, Json);
}

public record ArticlePayload(string ContentMarkdown);

public record VideoPayload(
    string Url,                 // YouTube/Vimeo/direct URL
    int DurationSec,
    string? NotesMarkdown       // companion notes / why this talk matters
);

public record QuizOptionP(string Id, string Text, bool Correct);
public record QuizQuestionP(
    string Id,
    string Text,
    string Kind,                // "single" | "multi" | "truefalse"
    List<QuizOptionP> Options,
    string? Explanation         // shown after attempt
);
public record QuizPayload(int PassPercent, List<QuizQuestionP> Questions);

public record RubricItemP(string Criterion, int Points);
public record AssignmentPayload(
    string InstructionsMarkdown,
    int TotalPoints,
    List<RubricItemP> Rubric,
    string SubmissionHint       // e.g. "Paste your repo URL + a short write-up"
);

public record CodeTestP(string Name, string? Stdin, string ExpectedStdout);
public record CodeExercisePayload(
    string Language,            // "python" runs in-browser via Pyodide; others are editor-only
    string PromptMarkdown,
    string StarterCode,
    List<CodeTestP> Tests,
    string? SolutionCode,       // revealed after first passing run or on demand
    string? HintMarkdown
);

public record ResourceLinkP(string Title, string Url);
public record ResourcePayload(string DescriptionMarkdown, List<ResourceLinkP> Links);
