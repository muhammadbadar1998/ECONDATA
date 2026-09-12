Dashboard
========

This directory contains the Streamlit dashboard for exploring EconData global economic datasets.

## Files

- `dashboard.py`: The main Streamlit dashboard application
- `requirements.txt`: Streamlit dependencies (to be merged with main requirements)

## Setup Instructions

### Prerequisites

- Python 3.10+ (since the project uses py310 target version)
- pip

### Installation

1. **Install Streamlit dependencies**:
   ```bash
   pip install streamlit pandas plotly
   ```

2. **Ensure project dependencies are installed**:
   ```bash
   pip install -r requirements.txt
   ```

3. **Run the dashboard**:
   ```bash
   streamlit run dashboard.py
   ```

## Dashboard Features

### Navigation

The dashboard has a sidebar with 6 main views:

1. **Overview** - High-level statistics and analytics
2. **Browse Datasets** - Explore all available economic indicators
3. **Countries** - View all countries in the dataset
4. **Aggregates** - Economic blocs and aggregates (OECD, EU, G7, etc.)
5. **Regions** - World regions and geographical groupings
6. **Data Explorer** - Search for specific data combinations

### Key Features

- **Interactive Cards**: Beautiful styled cards for displaying metric information
- **Search & Filter**: Search datasets by name, code, or filter by category/provider
- **Visualizations**: Pie charts and bar charts for data distribution analysis
- **Responsive Design**: Adapts to different screen sizes
- **Entity Selection**: Multiselect for exploring different combinations
- **Provider Mappings**: Shows how provider codes map to canonical entities
- **Real-time Updates**: Live statistics and dynamic filtering

### Data Sources

The dashboard loads data from:

1. **`config/metrics.json`**: Contains all economic metrics (39+ datasets)
   - Categories: Historical and Forecast
   - Providers: WB (World Bank), OECD
   - Time ranges: 1960-2060

2. **`src/entities.py`**: Contains canonical entity registry
   - 40+ countries (ISO-3166 alpha-3 codes)
   - 9 aggregates (WLD, OECD, EMU, EA19, EUU, G20, G7, etc.)
   - 13 regions (EAS, EAP, ECA, etc.)

### Visualization Types

- **Pie Charts**: Dataset category distribution
- **Bar Charts**: Unit and provider distributions
- **Funnel Charts**: Provider data volume
- **Interactive Maps**: Entity selection and visualization

### Usage Tips

1. Use the search in "Browse Datasets" to quickly find specific indicators
2. Filter by category or provider to narrow down results
3. Use the "Data Explorer" to analyze specific data combinations
4. The dashboard automatically loads and caches all data for fast performance

## Customization

### Add New Metrics

Edit `config/metrics.json` to add new economic indicators.

### Update Styling

Edit the CSS in `dashboard.py` (the `<style>` section) to customize colors and layout.

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

While the dashboard doesn't have traditional unit tests, you can verify it loads by running:

```bash
python -c "import dashboard; print('Dashboard imports successfully')"
```

### Performance

The dashboard uses Streamlit's `@st.cache_data` decorator to cache data loading for better performance. The data is loaded once and reused across page navigations.

## Known Issues & Limitations

1. **Entity Parsing**: The entities.py parser is a simplified version that may need refinement for complex cases
2. **Data Loading**: The dashboard loads all data at once; for very large datasets, consider implementing lazy loading
3. **Responsive Design**: Some elements may not display perfectly on very small screens

## License

This project is part of the EconData Global Economic Data Extraction & Supabase PostgreSQL Pipeline.

Dashboard code is distributed under the same terms as the main project.
