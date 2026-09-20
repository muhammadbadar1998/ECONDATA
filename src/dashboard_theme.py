"""Theme constants and Plotly dark-theme helper for the dashboard.

This module intentionally does NOT import Streamlit so that its tests can
run without a runtime.
"""

PALETTE = {
    "bg": "#000000",
    "surface": "#0f0f0f",
    "surface_alt": "#1a1a1a",
    "border": "#333333",
    "text": "#f5f5f5",
    "text_muted": "#b3b3b3",
    "accent": "#e5e5e5",
}

CHART_COLORWAY = ["#f5f5f5", "#b3b3b3", "#808080", "#595959", "#404040", "#2b2b2b"]


def apply_dark(fig):
    """Apply dark-theme styling to a Plotly figure in place.

    Returns the same figure object for chaining.
    """
    fig.update_layout(
        template="plotly_dark",
        paper_bgcolor="rgba(0,0,0,0)",
        plot_bgcolor="rgba(0,0,0,0)",
        font=dict(color=PALETTE["text"]),
        colorway=CHART_COLORWAY,
    )
    fig.update_xaxes(
        gridcolor=PALETTE["border"],
        zerolinecolor=PALETTE["surface_alt"],
        showgrid=False,
    )
    fig.update_yaxes(
        gridcolor=PALETTE["border"],
        zerolinecolor=PALETTE["surface_alt"],
        showgrid=False,
    )
    return fig
