"""Theme, contrast, and regression tests for the dark-theme dashboard.

No Streamlit runtime is required: the palette is read from
``src.dashboard_theme.PALETTE`` and the config from ``.streamlit/config.toml``.
"""
import pathlib

PROJECT_ROOT = pathlib.Path(__file__).resolve().parent.parent


def _relative_luminance(hex_color: str) -> float:
    """Calculate WCAG 2.x relative luminance for a hex colour."""
    hex_color = hex_color.lstrip("#")
    if len(hex_color) == 3:
        hex_color = "".join(c * 2 for c in hex_color)
    r, g, b = (int(hex_color[i : i + 2], 16) / 255 for i in (0, 2, 4))

    def _linear(c):
        return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4

    return 0.2126 * _linear(r) + 0.7152 * _linear(g) + 0.0722 * _linear(b)


def _contrast_ratio(hex1: str, hex2: str) -> float:
    l1 = _relative_luminance(hex1)
    l2 = _relative_luminance(hex2)
    lighter = max(l1, l2)
    darker = min(l1, l2)
    return (lighter + 0.05) / (darker + 0.05)


# ---------------------------------------------------------------------------
# Config file tests
# ---------------------------------------------------------------------------

def test_theme_config_is_dark():
    import tomllib

    config_path = PROJECT_ROOT / ".streamlit" / "config.toml"
    with open(config_path, "rb") as f:
        cfg = tomllib.load(f)

    theme = cfg["theme"]
    assert theme["base"] == "dark"
    assert _relative_luminance(theme["backgroundColor"]) < 0.05
    assert _relative_luminance(theme["secondaryBackgroundColor"]) < 0.05
    assert _relative_luminance(theme["textColor"]) > 0.85


# ---------------------------------------------------------------------------
# Palette contrast tests
# ---------------------------------------------------------------------------

def test_palette_contrast_text_on_bg():
    from src.dashboard_theme import PALETTE

    ratio = _contrast_ratio(PALETTE["text"], PALETTE["bg"])
    assert ratio >= 7, f"text on bg ratio {ratio:.1f} < 7:1"


def test_palette_contrast_text_on_surface():
    from src.dashboard_theme import PALETTE

    ratio = _contrast_ratio(PALETTE["text"], PALETTE["surface"])
    assert ratio >= 7, f"text on surface ratio {ratio:.1f} < 7:1"


def test_palette_contrast_text_muted_on_surface():
    from src.dashboard_theme import PALETTE

    ratio = _contrast_ratio(PALETTE["text_muted"], PALETTE["surface"])
    assert ratio >= 4.5, f"text_muted on surface ratio {ratio:.1f} < 4.5:1"


def test_palette_contrast_border_on_surface():
    from src.dashboard_theme import PALETTE

    ratio = _contrast_ratio(PALETTE["border"], PALETTE["surface"])
    assert ratio >= 1.5, f"border on surface ratio {ratio:.1f} < 1.5:1"


# ---------------------------------------------------------------------------
# apply_dark tests
# ---------------------------------------------------------------------------

def test_apply_dark_settings():
    import plotly.graph_objects as go
    from plotly.graph_objs.layout import Template

    from src.dashboard_theme import CHART_COLORWAY, PALETTE, apply_dark

    fig = go.Figure()
    result = apply_dark(fig)

    assert isinstance(result.layout.template, Template)
    assert result.layout.paper_bgcolor == "rgba(0,0,0,0)"
    assert result.layout.plot_bgcolor == "rgba(0,0,0,0)"
    assert result.layout.font.color == PALETTE["text"]
    assert tuple(result.layout.colorway) == tuple(CHART_COLORWAY)


# ---------------------------------------------------------------------------
# Dashboard source regression tests
# ---------------------------------------------------------------------------

def _dashboard_source():
    dashboard_path = PROJECT_ROOT / "dashboard.py"
    return dashboard_path.read_text(encoding="utf-8")


def test_dashboard_no_legacy_light_values():
    source = _dashboard_source()

    forbidden = ["#1e40af", "#f5f7fa", "#c3cfe2", "#e0f2fe", "#64748b", "#2d3436"]
    for value in forbidden:
        assert value not in source, f"Legacy light value {value} still present"


def test_dashboard_no_entities_parser():
    source = _dashboard_source()

    assert "src/entities.py" not in source
    assert "ENTITIES = [" not in source
    assert 'line.split(",")' not in source


def test_dashboard_every_chart_has_apply_dark():
    source = _dashboard_source()

    pie_calls = source.count("px.pie(")
    line_calls = source.count("px.line(")
    apply_calls = source.count("apply_dark(")
    total_chart_calls = pie_calls + line_calls

    assert apply_calls >= total_chart_calls, (
        f"{apply_calls} apply_dark calls < {total_chart_calls} chart calls "
        f"({pie_calls} pie, {line_calls} line)"
    )


def test_dashboard_footer_uses_fstring_and_datetime():
    source = _dashboard_source()

    assert "import datetime" in source
    assert "{datetime.now()" in source
    assert 'Last Updated: {datetime.now().strftime' in source
