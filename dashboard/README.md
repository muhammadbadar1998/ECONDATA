Dashboard
=========

This directory contains the Streamlit dashboard for exploring EconData global economic datasets.

## Files

- `dashboard.py`: The main Streamlit dashboard application

## Setup Instructions

### Prerequisites

- Python 3.10+ (since the project uses py310 target version)
- pip

### Installation

1. **Ensure project dependencies are installed**:
    ```bash
    pip install -r requirements.txt
    ```

### Configuration

Create `config/.env` from the template:
```bash
cp config/.env.example config/.env
```

Edit `config/.env` with your Supabase credentials. For IPv4 connectivity (recommended), use the **Session pooler** connection string from Supabase Dashboard → Settings → Database → Connection string → Session pooler:
```
SHAREDPOOLER=postgresql://postgres.<project-ref>:<password>@aws-1-<region>.pooler.supabase.com:5432/postgres
```

Or set individual variables:
```
SUPABASE_HOST=aws-1-<region>.pooler.supabase.com
SUPABASE_PORT=5432
SUPABASE_DB=postgres
SUPABASE_USER=postgres.<project-ref>
SUPABASE_PASSWORD=<password>
```

### Run the dashboard:
```bash
streamlit run dashboard.py
```

## Dashboard Features

### Navigation

The dashboard has a sidebar with 7 main views:

1. **Overview** - High-level statistics and analytics
2. **Browse Datasets** - Explore all available economic indicators
3. **Countries** - View all countries in the dataset
4. **Aggregates** - Economic blocs and aggregates (OECD, EU, G7, etc.)
5. **Regions** - World regions and geographical groupings
6. **Metadata** - Integration health, pipeline runs, and per-metric coverage
7. **Data Explorer** - Guided time-series exploration of actual loaded observations

### Key Features

- **Interactive Cards**: Beautiful styled cards for displaying metric information
- **Search & Filter**: Search datasets by name, code, or filter by category/provider
- **Visualizations**: Pie charts and bar charts for data distribution analysis
- **Responsive Design**: Adapts to different screen sizes
- **Entity Selection**: Multiselect for exploring different combinations
- **Provider Mappings**: Shows how provider codes map to canonical entities
- **Real-time Updates**: Live statistics and dynamic filtering
- **Metadata Page**: Integration health, pipeline run history, per-metric coverage with declared-vs-actual comparison
- **Data Explorer**: Guided time-series exploration with Plotly charts, data tables, and CSV export

### Data Sources

The dashboard reads from two sources:

1. **Local configuration** (`config/metrics.json`):
    - Metric definitions (categories, providers, time ranges)
    - Used by Browse Datasets and Data Explorer as the offline catalog

2. **Supabase PostgreSQL** (live data):
    - `dbe_metrics`, `dbe_entities`, `dbe_observations`, `dbe_sources`, `dbe_pipeline_runs`
    - View `v_dated_execution_snapshot` for time-series queries
    - Connection via `config/.env` (Supabase Session pooler recommended for IPv4)
    - Entity registry (countries, aggregates, regions) is queried from `dbe_entities`

### Visualization Types

- **Pie Charts**: Dataset category distribution
- **Bar Charts**: Unit and provider distributions
- **Funnel Charts**: Provider data volume
- **Interactive Maps**: Entity selection and visualization
- **Time-Series Charts**: Interactive Plotly line charts in Data Explorer

### Usage Tips

1. Use the search in "Browse Datasets" to quickly find specific indicators
2. Filter by category or provider to narrow down results
3. Use the "Data Explorer" to analyze specific data combinations over time
4. Use "Metadata" to check integration health, pipeline runs, and data coverage
5. Click "Refresh Data" in the sidebar to reload cached data

## Customization

### Add New Metrics

Edit `config/metrics.json` to add new economic indicators.

### Update Styling

Edit `.streamlit/config.toml` for theme settings or `src/dashboard_theme.py` for the palette.
The CSS in `dashboard.py` (the `<style>` section) also controls layout and colors.
A **server restart** is required for `.streamlit/config.toml` changes to take effect.

### Add New Views

Add new pages to the sidebar `selectbox` in the `main()` function.

## Development

### Python Requirements

The project uses:
- **Python 3.10**
- **Streamlit 1.63.0**
- **Pandas 2.0.0**
- **Plotly** for interactive charts
- **Python-dotenv** for environment variables

### Testing

The dashboard includes three test modules:

- `tests/test_dashboard_counts.py` — unit tests for count formatting, overview summarisation, and regression locks (no DB needed)
- `tests/test_dashboard_theme.py` — contrast, palette, and chart-styling tests (no Streamlit runtime needed)
- `tests/test_dashboard_db_integration.py` — opt-in live-Supabase integration tests (set `ECONDATA_TEST_DB_CONN` to run)

While the dashboard doesn't have traditional unit tests for the Streamlit UI itself, you can verify it loads by running:

```bash
python -c "import dashboard; print('Dashboard imports successfully')"
```

### Performance

The dashboard uses Streamlit's `@st.cache_data` decorator to cache database queries (TTL 60 seconds). The Refresh button clears the cache and re-reads `config/.env` for credential changes.

## Known Issues & Limitations

1. **Data Loading**: The dashboard queries Supabase directly; large result sets are capped at 50,000 rows in the Data Explorer
2. **Responsive Design**: Some elements may not display perfectly on very small screens
3. **Credential Changes**: Use the "Refresh Data" button after editing `config/.env` to pick up new credentials without a restart
4. **Count vs List Mismatch**: Entity counts on the Overview tiles come from `dbe_entities`, but badge lists on entity pages use `get_entity_options()` which inner-joins `dbe_observations`. An active entity with no loaded data appears in the tile count but not in the badge lists. This is by design.
5. **Restart Required**: Changes to `.streamlit/config.toml` require a Streamlit server restart (unlike script edits, which hot-reload).

## License

This project is part of the EconData Global Economic Data Extraction & Supabase PostgreSQL Pipeline.

Dashboard code is distributed under the same terms as the main project.
