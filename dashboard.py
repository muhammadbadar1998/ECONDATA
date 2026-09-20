import json
from datetime import datetime
from pathlib import Path

import pandas as pd
import plotly.express as px
import streamlit as st

from src import dashboard_data as dd
from src.dashboard_theme import apply_dark

st.set_page_config(
    page_title="EconData Dashboard",
    page_icon="📊",
    layout="wide",
    initial_sidebar_state="expanded",
)

st.markdown("""
<style>
    .main-header {
        background: linear-gradient(135deg, #1a1a1a 0%, #333333 100%);
        padding: 30px;
        border-radius: 15px;
        color: var(--text-color, #f5f5f5);
        text-align: center;
        margin-bottom: 30px;
        box-shadow: 0 10px 30px rgba(0,0,0,0.4);
    }

    .metric-card {
        background: var(--secondary-background-color, #0f0f0f);
        border-radius: 12px;
        padding: 20px;
        box-shadow: 0 4px 15px rgba(0,0,0,0.3);
        border-left: 4px solid var(--text-color, #f5f5f5);
    }

    .metric-card:hover {
        transform: translateY(-5px);
    }

    .dataset-card {
        background: var(--secondary-background-color, #0f0f0f);
        border-radius: 12px;
        padding: 20px;
        margin: 15px 0;
        box-shadow: 0 4px 15px rgba(0,0,0,0.3);
    }

    .country-badge {
        background: var(--secondary-background-color, #0f0f0f);
        color: var(--text-color, #f5f5f5);
        padding: 8px 16px;
        border-radius: 20px;
        font-weight: bold;
        margin: 5px;
        display: inline-block;
        border: 1px solid var(--text-color, #f5f5f5);
    }

    .aggregate-badge {
        background: var(--secondary-background-color, #0f0f0f);
        color: var(--text-color, #f5f5f5);
        padding: 8px 16px;
        border-radius: 20px;
        font-weight: bold;
        margin: 5px;
        display: inline-block;
        border: 1px solid var(--text-color, #f5f5f5);
    }

    .region-badge {
        background: var(--secondary-background-color, #0f0f0f);
        color: var(--text-color, #f5f5f5);
        padding: 8px 16px;
        border-radius: 20px;
        font-weight: bold;
        margin: 5px;
        display: inline-block;
        border: 1px solid var(--text-color, #f5f5f5);
    }

    .sidebar .sidebar-content {
        background: var(--secondary-background-color, #0f0f0f);
    }

    .stSelectbox > label {
        font-weight: 600;
        color: var(--text-color, #f5f5f5);
    }

    .stTextInput > label {
        font-weight: 600;
        color: var(--text-color, #f5f5f5);
    }

    .stMultiSelect__selected {
        background: var(--surface-alt, #1a1a1a);
        color: var(--text-color, #f5f5f5);
        border: 1px solid var(--border, #333333);
        border-radius: 4px;
        padding: 2px 8px;
        margin: 2px;
        display: inline-block;
    }

    .stMultiSelect span {
        color: var(--text-color, #f5f5f5);
    }

    .stMultiSelect [data-baseweb="option"] {
        color: var(--text-color, #f5f5f5);
    }

    .stMultiSelect [data-baseweb="option"]:hover {
        background-color: var(--surface-alt, #1a1a1a);
    }

    .stMultiSelect div[data-baseweb="input"] {
        background-color: var(--surface, #0f0f0f);
        color: var(--text-color, #f5f5f5);
    }
</style>
""", unsafe_allow_html=True)


def _load_local_metrics():
    """Load metrics catalog from local config (offline catalog)."""
    metrics_path = Path("./config/metrics.json")
    if metrics_path.exists():
        with open(metrics_path, "r") as f:
            return json.load(f).get("metrics", [])
    return []


def main():
    st.markdown("""
    <div class="main-header">
        <h1>📊 EconData Global Economic Dashboard</h1>
        <p>Explore and navigate through economic datasets and indicators</p>
        <p><em>World Bank, OECD, and IMF economic data pipeline</em></p>
    </div>
    """, unsafe_allow_html=True)

    st.sidebar.title("🧭 Navigation")
    page = st.sidebar.selectbox(
        "Select View",
        ["Overview", "Browse Datasets", "Countries", "Aggregates", "Regions", "Metadata", "Data Explorer"],
    )

    if st.sidebar.button("🔄 Refresh Data"):
        dd.reload_env()
        dd.get_engine_cached.clear()
        st.cache_data.clear()
        st.rerun()

    connected, conn_msg = dd.check_connection()
    if not connected:
        st.error(f"⚠️ Supabase connection failed: {conn_msg}")
        st.info(
            "Ensure `config/.env` exists with valid Supabase credentials. "
            "If using a pooler, set `ECONDATA_DATABASE_URL` (or `DATABASE_URL`/`SHAREDPOOLER`)."
        )
    else:
        st.success(f"✅ Connected to Supabase: {conn_msg}")

    metrics = _load_local_metrics()

    if page == "Overview":
        st.header("Dashboard Overview")

        try:
            counts = dd.get_overview_counts()
        except dd.DashboardDataError as e:
            st.warning(f"Could not load overview counts: {e}")
            counts = {}

        col1, col2, col3, col4, col5, col6 = st.columns(6)

        with col1:
            st.metric("📈 Total Metrics", dd.format_count(counts.get("metrics_count")))

        with col2:
            st.metric("🌍 Countries", dd.format_count(counts.get("countries_count")))

        with col3:
            st.metric("🏛️ Aggregates", dd.format_count(counts.get("aggregates_count")))

        with col4:
            st.metric("🗺️ Regions", dd.format_count(counts.get("regions_count")))

        with col5:
            st.metric("🔮 Forecasts", dd.format_count(counts.get("forecasts_count")))

        with col6:
            st.metric("📊 Historical", dd.format_count(counts.get("historical_count")))

        st.markdown("### 📋 Latest Dataset Categories")

        if counts.get("historical_count") or counts.get("forecasts_count"):
            fig = px.pie(
                values=[
                    counts.get("historical_count") or 0,
                    counts.get("forecasts_count") or 0,
                ],
                names=["Historical", "Forecast"],
                title="Dataset Distribution by Category",
            )
            apply_dark(fig)
            st.plotly_chart(fig, use_container_width=True)

        st.markdown("### 🚀 Quick Start")
        st.info("""
        **Getting Started:**

        1. **Browse Datasets** - Explore all available economic indicators
        2. **Countries** - View all countries in the dataset
        3. **Data Explorer** - Search for specific data combinations

        Use the sidebar to navigate between different views!
        """)

    elif page == "Browse Datasets":
        st.header("📋 Browse All Economic Datasets")

        col1, col2, col3 = st.columns(3)

        with col1:
            search_term = st.text_input("🔍 Search datasets...")

        with col2:
            category_filter = st.selectbox(
                "Filter by Category",
                ["All"] + list(set(m.get("category", "unknown") for m in metrics)),
            )

        with col3:
            provider_filter = st.selectbox(
                "Filter by Provider",
                ["All"] + list(set(m.get("provider", "unknown") for m in metrics)),
            )

        filtered_metrics = metrics
        if search_term:
            filtered_metrics = [m for m in filtered_metrics
                                if search_term.lower() in m.get("metric_name", "").lower()
                                or search_term.lower() in m.get("metric_code", "").lower()]

        if category_filter != "All":
            filtered_metrics = [m for m in filtered_metrics if m.get("category") == category_filter]

        if provider_filter != "All":
            filtered_metrics = [m for m in filtered_metrics if m.get("provider") == provider_filter]

        st.write(f"Showing {len(filtered_metrics)} of {len(metrics)} datasets")

        for i in range(0, len(filtered_metrics), 2):
            col1, col2 = st.columns(2)

            with col1:
                if i < len(filtered_metrics):
                    metric = filtered_metrics[i]
                    display_metric_card(metric)

            with col2:
                if i + 1 < len(filtered_metrics):
                    metric = filtered_metrics[i + 1]
                    display_metric_card(metric)

    elif page == "Countries":
        st.header("🌍 Countries")

        try:
            entities_df = dd.get_entities_by_type("country")
            countries = entities_df["entity_code"].tolist() if not entities_df.empty else []
        except dd.DashboardDataError as e:
            st.warning(f"Could not load countries: {e}")
            countries = []

        if not countries:
            st.info("No data — the entity table is empty or unreachable.")
        else:
            country_search = st.text_input("🔍 Search countries...")
            filtered_countries = countries

            if country_search:
                filtered_countries = [c for c in filtered_countries
                                      if country_search.lower() in c.lower()]

            st.write(f"Showing {len(filtered_countries)} of {len(countries)} countries")

            sorted_countries = sorted(filtered_countries)
            cols = st.columns(4)
            for i, country in enumerate(sorted_countries):
                with cols[i % 4]:
                    st.markdown(f"<div class='country-badge'>{country}</div>", unsafe_allow_html=True)

    elif page == "Aggregates":
        st.header("🏛️ Aggregates & Economic Blocs")

        try:
            aggregates_df = dd.get_entities_by_type("aggregate")
            aggregates = aggregates_df["entity_code"].tolist() if not aggregates_df.empty else []
        except dd.DashboardDataError as e:
            st.warning(f"Could not load aggregates: {e}")
            aggregates = []

        if aggregates:
            cols = st.columns(3)
            for i, aggregate in enumerate(aggregates):
                with cols[i % 3]:
                    st.markdown(f"<div class='aggregate-badge'>{aggregate}</div>", unsafe_allow_html=True)

        st.markdown("### 🔗 Provider Code Mappings")
        try:
            aliases_df = dd.get_entity_aliases()
            if not aliases_df.empty:
                for _, row in aliases_df.head(10).iterrows():
                    st.markdown(
                        f"**{row['provider']}:** {row['provider_code']} → **{row['entity_code']}**"
                    )
            else:
                st.info("No alias mappings found.")
        except dd.DashboardDataError as e:
            st.warning(f"Could not load aliases: {e}")

    elif page == "Regions":
        st.header("🗺️ World Regions")

        try:
            regions_df = dd.get_entities_by_type("region")
            regions = regions_df["entity_code"].tolist() if not regions_df.empty else []
        except dd.DashboardDataError as e:
            st.warning(f"Could not load regions: {e}")
            regions = []

        if regions:
            cols = st.columns(4)
            for i, region in enumerate(regions):
                with cols[i % 4]:
                    st.markdown(f"<div class='region-badge'>{region}</div>", unsafe_allow_html=True)
        else:
            st.info("No data — the entity table is empty or unreachable.")

    elif page == "Metadata":
        st.header("📊 Integration Health")

        connected, conn_msg = dd.check_connection()
        if not connected:
            st.error(f"Cannot read integration metadata: {conn_msg}")
            st.info("Ensure `config/.env` points at a reachable Supabase project, then click **Refresh Data**.")
            st.stop()

        st.markdown("### 🔍 System Status")
        try:
            totals = dd.get_totals()
        except dd.DashboardDataError as e:
            totals = {}
            st.warning(f"Could not load totals: {e}")

        col1, col2, col3, col4, col5 = st.columns(5)
        with col1:
            st.metric("📈 Metrics", totals.get("metrics_count", 0))
        with col2:
            st.metric("🌍 Entities", totals.get("entities_count", 0))
        with col3:
            st.metric("📊 Observations", totals.get("observations_count", 0))
        with col4:
            st.metric("🗄️ Sources", totals.get("sources_count", 0))
        with col5:
            st.metric("🏭 Providers", totals.get("providers_count", 0))

        if totals:
            st.markdown("### 📅 Data Range")
            st.write(f"*From {totals.get('min_date', 'N/A')} to {totals.get('max_date', 'N/A')}*")
            st.write(f"*Last extracted: {totals.get('last_extracted_at', 'N/A')}* (UTC)")

        st.markdown("### 📋 Recent Pipeline Runs")
        try:
            runs = dd.get_pipeline_runs(limit=10)
            if not runs.empty:
                runs_display = runs.copy()
                runs_display["run_timestamp"] = (
                    pd.to_datetime(runs_display["run_timestamp"]).dt.strftime("%Y-%m-%d %H:%M")
                )
                st.dataframe(runs_display, use_container_width=True)
            else:
                st.info("No pipeline runs logged yet.")
        except dd.DashboardDataError as e:
            st.warning(f"Could not load pipeline runs: {e}")

        st.markdown("### 📊 Metric Coverage")
        try:
            coverage = dd.get_metric_coverage()
            if not coverage.empty:
                merged = dd.merge_declared_coverage(coverage)
                display_cols = [
                    "metric_code", "metric_name", "category", "frequency", "providers",
                    "observation_count", "entity_count", "min_date", "max_date",
                    "last_extracted_at", "declared_start_year", "declared_end_year",
                    "actual_start_year", "actual_end_year", "end_year_gap",
                ]
                st.dataframe(merged[display_cols], use_container_width=True)

                gap_series = pd.to_numeric(merged["end_year_gap"], errors="coerce")
                gap_count = int((gap_series.abs() > 0).sum())
                if gap_count:
                    st.warning(f"{gap_count} metrics have a gap between declared end year and actual data.")
            else:
                st.info("No metrics found in the database.")
        except dd.DashboardDataError as e:
            st.warning(f"Could not load metric coverage: {e}")

        st.markdown("### 🔗 Connection Status")
        st.success("Connected to Supabase PostgreSQL")
        st.code(conn_msg)

    elif page == "Data Explorer":
        st.header("🔍 Data Explorer")

        if not dd.check_connection()[0]:
            st.error("Cannot access data - database connection failed. Check `config/.env`.")
        else:
            try:
                entity_options = dd.get_entity_options()
                countries = entity_options[entity_options["entity_type"] == "country"]["entity_code"].tolist()
                aggregates = entity_options[entity_options["entity_type"] == "aggregate"]["entity_code"].tolist()
                regions = entity_options[entity_options["entity_type"] == "region"]["entity_code"].tolist()
            except dd.DashboardDataError as e:
                st.warning(f"Could not load entity list: {e}")
                countries, aggregates, regions = [], [], []

            try:
                coverage = dd.get_metric_coverage()
                metric_options = coverage[["metric_code", "metric_name", "category"]].drop_duplicates()
                metric_options_list = [
                    f"{row['metric_code']} - {row['metric_name']} ({row['category']})"
                    for _, row in metric_options.iterrows()
                ]
            except dd.DashboardDataError as e:
                st.warning(f"Could not load metric list: {e}")
                metric_options_list = []

            selected_metric = None
            selected_metric_code = None
            selected_entities = []
            start_date = None
            end_date = None
            metric_row = pd.DataFrame()

            col1, col2 = st.columns(2)

            with col1:
                st.subheader("📊 Dataset Selection")

                selected_metric = st.selectbox(
                    "Select Metric",
                    metric_options_list,
                    index=0 if metric_options_list else None,
                )

                if selected_metric:
                    selected_metric_code = selected_metric.split(" - ")[0]
                    metric_row = coverage[coverage["metric_code"] == selected_metric_code]
                    if not metric_row.empty:
                        st.markdown("### 📋 Metric Details")
                        st.write(f"**Code:** {metric_row.iloc[0]['metric_code']}")
                        st.write(f"**Name:** {metric_row.iloc[0]['metric_name']}")
                        st.write(f"**Category:** {metric_row.iloc[0]['category']}")
                        st.write(f"**Base Unit:** {metric_row.iloc[0]['base_unit']}")
                        st.write(f"**Frequency:** {metric_row.iloc[0]['frequency']}")
                        st.write(f"**Observations:** {metric_row.iloc[0]['observation_count']}")
                        st.write(f"**Entities:** {metric_row.iloc[0]['entity_count']}")
                        st.write(f"**Data Range:** {metric_row.iloc[0]['min_date']} → {metric_row.iloc[0]['max_date']}")

            with col2:
                st.subheader("🌍 Entity Selection")

                entity_type = st.radio("Select Entity Type", ["Countries", "Aggregates", "Regions"], horizontal=True)

                if entity_type == "Countries" and countries:
                    selected_entities = st.multiselect(
                        "Select Countries",
                        sorted(countries),
                        default=sorted(countries)[:5],
                        help="Choose one or more countries to explore",
                    )
                elif entity_type == "Aggregates" and aggregates:
                    selected_entities = st.multiselect(
                        "Select Aggregates",
                        sorted(aggregates),
                        default=sorted(aggregates)[:5],
                        help="Choose one or more aggregates to explore",
                    )
                elif entity_type == "Regions" and regions:
                    selected_entities = st.multiselect(
                        "Select Regions",
                        sorted(regions),
                        default=sorted(regions)[:5],
                        help="Choose one or more regions to explore",
                    )
                else:
                    selected_entities = []
                    st.info(f"No {entity_type.lower()} available in the database.")

                if selected_metric and not metric_row.empty:
                    min_raw = metric_row.iloc[0]["min_date"]
                    max_raw = metric_row.iloc[0]["max_date"]
                    min_year = int(pd.to_datetime(min_raw).year) if pd.notna(min_raw) else 1960
                    max_year = int(pd.to_datetime(max_raw).year) if pd.notna(max_raw) else 2026
                    if min_year > max_year:
                        min_year, max_year = max_year, min_year
                    year_range = st.selectbox(
                        "Date Range",
                        ["All Years", "Custom"],
                        help="Use the full available range or pick a custom window",
                    )
                    if year_range == "Custom":
                        col_a, col_b = st.columns(2)
                        with col_a:
                            start_year = st.selectbox("Start Year", list(range(min_year, max_year + 1)))
                        with col_b:
                            end_year = st.selectbox("End Year", list(range(start_year, max_year + 1)))
                        start_date = f"{start_year}-01-01"
                        end_date = f"{end_year}-12-31"
                    else:
                        start_date = f"{min_year}-01-01"
                        end_date = f"{min_year}-12-31"

            st.markdown("### 📈 Analytics")

            if selected_metric and selected_entities and start_date and end_date:
                try:
                    df = dd.get_observations(
                        metric_codes=[selected_metric_code],
                        entity_codes=selected_entities,
                        start_date=start_date,
                        end_date=end_date,
                    )

                    if not df.empty:
                        st.write(
                            f"Showing {len(df)} observations for {selected_metric_code} "
                            f"across {len(selected_entities)} entities ({start_date} to {end_date})."
                        )
                        if len(df) >= dd.OBSERVATIONS_ROW_LIMIT:
                            st.warning(
                                f"Results are capped at {dd.OBSERVATIONS_ROW_LIMIT:,} rows; "
                                "narrow the metric, entities, or date range to see the full selection."
                            )

                        st.markdown("#### 📊 Time Series Visualization")
                        fig = px.line(
                            df,
                            x="start_period",
                            y="val",
                            color="country_code",
                            title=f"{selected_metric_code} Over Time",
                            labels={"val": "Value", "start_period": "Period"},
                        )
                        apply_dark(fig)
                        st.plotly_chart(fig, use_container_width=True)

                        st.markdown("#### 📋 Data Table")
                        display_cols = [
                            "country_code", "country_name", "start_period", "end_period",
                            "val", "unit", "period_str", "extracted_at",
                        ]
                        st.dataframe(df[display_cols].head(1000), use_container_width=True)

                        csv = df.to_csv(index=False)
                        st.download_button(
                            label="Download CSV",
                            data=csv,
                            file_name=f"{selected_metric_code}_explorer.csv",
                            mime="text/csv",
                        )

                    else:
                        st.warning(f"No data found for {selected_metric_code} in the selected entities and date range.")

                except dd.DashboardDataError as e:
                    st.error(f"Error loading data: {e}")
            elif selected_metric:
                st.info("Please select at least one entity to view data.")
            else:
                st.info("Please select a metric to begin exploration.")

    st.markdown(f"""
    <div style="background: linear-gradient(135deg, #1a1a1a 0%, #333333 100%); padding: 30px; border-radius: 15px; color: var(--text-color, #f5f5f5); text-align: center; margin-top: 50px;">
        <h3>📊 EconData Pipeline Dashboard</h3>
        <p>World Bank, OECD, and IMF Economic Data Extraction &amp; Analysis</p>
        <p><em>Built with ❤️ for economic data exploration</em></p>
        <p><small>Last Updated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}</small></p>
    </div>
    """, unsafe_allow_html=True)


def display_metric_card(metric):
    """Display a metric in a card format"""
    st.markdown(f"""
    <div class="dataset-card">
        <h4 style="color: var(--text-color, #f5f5f5); margin-bottom: 10px;">{metric.get('metric_name', metric.get('metric_code', 'Unknown'))}</h4>
        <p style="margin-bottom: 8px;"><strong>Code:</strong> {metric.get('metric_code', 'N/A')}</p>
        <p style="margin-bottom: 8px;"><strong>Category:</strong> <span style="background: var(--surface-alt, #1a1a1a); padding: 2px 8px; border-radius: 10px; border: 1px solid var(--border, #333333);">{metric.get('category', 'N/A')}</span></p>
        <p style="margin-bottom: 8px;"><strong>Provider:</strong> {metric.get('provider', 'N/A')}</p>
        <p style="margin-bottom: 8px;"><strong>Unit:</strong> {metric.get('unit', 'N/A')}</p>
        <p style="margin-bottom: 8px;"><strong>Years:</strong> {metric.get('start_year', 'N/A')} - {metric.get('end_year', 'N/A')}</p>
        <details>
            <summary style="color: var(--text-muted, #b3b3b3); cursor: pointer;">View Details</summary>
            <p style="margin-top: 10px; font-size: 0.9em;">{metric.get('description', 'N/A')[:200]}...</p>
        </details>
    </div>
    """, unsafe_allow_html=True)


if __name__ == "__main__":
    main()
