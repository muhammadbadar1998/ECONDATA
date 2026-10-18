namespace EconData.Core.Chat;

/// <summary>Incoming chat request.</summary>
public sealed record ChatRequest(
    string Question,
    IReadOnlyList<ChatTurnDto>? History = null);

/// <summary>Outgoing chat turn for the wire format.</summary>
public sealed record ChatTurnDto(string Role, string Content);

/// <summary>Outgoing chat response.</summary>
public sealed record ChatResponse(
    string Answer,
    string Intent,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> ToolsUsed);
