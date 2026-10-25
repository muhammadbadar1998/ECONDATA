using System.Text.Json.Serialization;

namespace EconData.Web.Services;

public sealed record DashboardWidgetConfig(
    [property: JsonPropertyName("entity")] string? Entity,
    [property: JsonPropertyName("metric")] string? Metric,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("period")] string? Period,
    [property: JsonPropertyName("direction")] string? Direction,
    [property: JsonPropertyName("metric_a")] string? MetricA,
    [property: JsonPropertyName("metric_b")] string? MetricB);

public sealed record DashboardWidget(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("w")] int W,
    [property: JsonPropertyName("h")] int H,
    [property: JsonPropertyName("config")] DashboardWidgetConfig Config);

public sealed record DashboardLayout(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("widgets")] IReadOnlyList<DashboardWidget> Widgets);
