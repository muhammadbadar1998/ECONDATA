import streamlit as st
import pandas as pd
import json
from pathlib import Path
from datetime import datetime

# Configure the page
st.set_page_config(
    page_title="EconData Dashboard",
    page_icon="📊",
    layout="wide",
    initial_sidebar_state="expanded"
)

# Custom CSS for better aesthetics
st.markdown("""
<style>
    .main-header {
        background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
        padding: 30px;
        border-radius: 15px;
        color: white;
        text-align: center;
        margin-bottom: 30px;
        box-shadow: 0 10px 30px rgba(0,0,0,0.2);
    }
    
    .metric-card {
        background: white;
        border-radius: 12px;
        padding: 20px;
        box-shadow: 0 4px 15px rgba(0,0,0,0.1);
        border-left: 4px solid #3b82f6;
        transition: transform 0.3s ease;
    }
    
    .metric-card:hover {
        transform: translateY(-5px);
    }
    
    .dataset-card {
        background: linear-gradient(135deg, #f5f7fa 0%, #c3cfe2 100%);
        border-radius: 12px;
        padding: 20px;
        margin: 15px 0;
        box-shadow: 0 4px 15px rgba(0,0,0,0.1);
    }
    
    .country-badge {
        background: linear-gradient(135deg, #89f7fe 0%, #66a6ff 100%);
        color: white;
        padding: 8px 16px;
        border-radius: 20px;
        font-weight: bold;
        margin: 5px;
        display: inline-block;
    }
    
    .aggregate-badge {
        background: linear-gradient(135deg, #ffeaa7 0%, #fdcb6e 100%);
        color: #2d3436;
        padding: 8px 16px;
        border-radius: 20px;
        font-weight: bold;
        margin: 5px;
        display: inline-block;
    }
    
    .region-badge {
        background: linear-gradient(135deg, #fd79a8 0%, #e84393 100%);
        color: white;
        padding: 8px 16px;
        border-radius: 20px;
        font-weight: bold;
        margin: 5px;
        display: inline-block;
    }
    
    .sidebar .sidebar-content {
        background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
    }
    
    .stSelectbox > label {
        font-weight: 600;
        color: #1e40af;
    }
    
    .stTextInput > label {
        font-weight: 600;
        color: #1e40af;
    }
</style>
""", unsafe_allow_html=True)

def load_data():
    """Load all data from the project"""
    # Load metrics configuration
    metrics_path = Path("./config/metrics.json")
    metrics_data = {}
    if metrics_path.exists():
        with open(metrics_path, 'r') as f:
            metrics_data = json.load(f)
    
    # Load entities data
    entities_path = Path("./src/entities.py")
    entities_data = {
        "countries": [],
        "aggregates": [],
        "regions": [],
        "entity_aliases": {}
    }
    
    if entities_path.exists():
        with open(entities_path, 'r') as f:
            content = f.read()
            # Extract data from entities.py (simplified parsing)
            lines = content.split('\n')
            in_entities = False
            in_aliases = False
            
            for line in lines:
                line = line.strip()
                if line.startswith('ENTITIES = ['):
                    in_entities = True
                    in_aliases = False
                elif line.startswith('ENTITY_ALIASES = {'):
                    in_entities = False
                    in_aliases = True
                elif line.startswith(']'):
                    in_entities = False
                    in_aliases = False
                elif in_entities and line.startswith('("'):
                    # Parse entity tuple
                    parts = line.strip('(),').split(',')
                    if len(parts) >= 3:
                        entity_code = parts[0].strip('"\'')
                        entity_name = parts[1].strip('"\'')
                        entity_type = parts[2].strip('"\'')
                        if entity_type == "country":
                            entities_data["countries"].append(entity_code)
                        elif entity_type == "aggregate":
                            entities_data["aggregates"].append(entity_code)
                        elif entity_type == "region":
                            entities_data["regions"].append(entity_code)
                elif in_aliases and '):' in line:
                    # Parse alias mapping
                    key, value = line.split('):', 1)
                    key = key.strip()
                    if ',' in key:
                        provider, provider_code = key.strip('()').split(', ')
                        entities_data["entity_aliases"][(provider.strip('"'), provider_code.strip('"'))] = value.strip('"')
    
    return {
        "metrics": metrics_data.get("metrics", []),
        "pipeline_settings": metrics_data.get("pipeline_settings", {}),
        "entities": entities_data
    }

def main():
    # Header
    st.markdown("""
    <div class="main-header">
        <h1>📊 EconData Global Economic Dashboard</h1>
        <p>Explore and navigate through economic datasets and indicators</p>
        <p><em>World Bank, OECD, and IMF economic data pipeline</em></p>
    </div>
    """, unsafe_allow_html=True)
    
    # Load data
    data = load_data()
    metrics = data["metrics"]
    entities = data["entities"]
    
    # Sidebar navigation
    st.sidebar.title("🧭 Navigation")
    page = st.sidebar.selectbox(
        "Select View",
        ["Overview", "Browse Datasets", "Countries", "Aggregates", "Regions", "Data Explorer"]
    )
    
    # Overview Page
    if page == "Overview":
        st.header("Dashboard Overview")
        
        # Key metrics
        col1, col2, col3, col4, col5, col6 = st.columns(6)
        
        with col1:
            st.metric("📈 Total Metrics", len(metrics))
        
        with col2:
            st.metric("🌍 Countries", len(entities["countries"]))
        
        with col3:
            st.metric("🏛️ Aggregates", len(entities["aggregates"]))
        
        with col4:
            st.metric("🗺️ Regions", len(entities["regions"]))
        
        with col5:
            forecast_metrics = [m for m in metrics if m.get("category") == "forecast"]
            st.metric("🔮 Forecasts", len(forecast_metrics))
        
        with col6:
            historical_metrics = [m for m in metrics if m.get("category") == "historical"]
            st.metric("📊 Historical", len(historical_metrics))
        
        # Recent activity
        st.markdown("### 📋 Latest Dataset Categories")
        
        category_counts = {}
        for metric in metrics:
            category = metric.get("category", "unknown")
            category_counts[category] = category_counts.get(category, 0) + 1
        
        if category_counts:
            import plotly.express as px
            fig = px.pie(
                values=list(category_counts.values()),
                names=list(category_counts.keys()),
                title="Dataset Distribution by Category",
                color_discrete_sequence=px.colors.qualitative.Set3
            )
            st.plotly_chart(fig, use_container_width=True)
        
        st.markdown("### 🚀 Quick Start")
        st.info("""
        **Getting Started:**
        
        1. **Browse Datasets** - Explore all available economic indicators
        2. **Countries** - View all countries in the dataset
        3. **Data Explorer** - Search for specific data combinations
        
        Use the sidebar to navigate between different views!
        """)
    
    # Browse Datasets Page
    elif page == "Browse Datasets":
        st.header("📋 Browse All Economic Datasets")
        
        # Search and filters
        col1, col2, col3 = st.columns(3)
        
        with col1:
            search_term = st.text_input("🔍 Search datasets...")
        
        with col2:
            category_filter = st.selectbox(
                "Filter by Category",
                ["All"] + list(set(m.get("category", "unknown") for m in metrics))
            )
        
        with col3:
            provider_filter = st.selectbox(
                "Filter by Provider",
                ["All"] + list(set(m.get("provider", "unknown") for m in metrics))
            )
        
        # Filter datasets
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
        
        # Display metrics in cards
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
    
    # Countries Page
    elif page == "Countries":
        st.header("🌍 Countries")
        
        # Search countries
        country_search = st.text_input("🔍 Search countries...")
        filtered_countries = entities["countries"]
        
        if country_search:
            filtered_countries = [c for c in filtered_countries 
                                if country_search.lower() in c.lower()]
        
        st.write(f"Showing {len(filtered_countries)} of {len(entities['countries'])} countries")
        
        # Display countries by continent/region
        if filtered_countries:
            # Sort countries
            sorted_countries = sorted(filtered_countries)
            
            # Display in columns
            cols = st.columns(4)
            for i, country in enumerate(sorted_countries):
                with cols[i % 4]:
                    st.markdown(f"<div class='country-badge'>{country}</div>", unsafe_allow_html=True)
    
    # Aggregates Page
    elif page == "Aggregates":
        st.header("🏛️ Aggregates & Economic Blocs")
        
        # Display aggregates
        if entities["aggregates"]:
            cols = st.columns(3)
            for i, aggregate in enumerate(entities["aggregates"]):
                with cols[i % 3]:
                    st.markdown(f"<div class='aggregate-badge'>{aggregate}</div>", unsafe_allow_html=True)
        
        # Entity aliases mapping
        st.markdown("### 🔗 Provider Code Mappings")
        if entities["entity_aliases"]:
            for (provider, code), canonical in list(entities["entity_aliases"].items())[:10]:
                st.markdown(f"**{provider}:** {code} → **{canonical}**")
    
    # Regions Page
    elif page == "Regions":
        st.header("🗺️ World Regions")
        
        if entities["regions"]:
            cols = st.columns(4)
            for i, region in enumerate(entities["regions"]):
                with cols[i % 4]:
                    st.markdown(f"<div class='region-badge'>{region}</div>", unsafe_allow_html=True)
    
    # Data Explorer Page
    elif page == "Data Explorer":
        st.header("🔍 Data Explorer")
        
        col1, col2 = st.columns(2)
        
        with col1:
            st.subheader("📊 Dataset Selection")
            
            # Metric selection
            metric_options = [f"{m['metric_code']} - {m['metric_name']}" 
                            for m in metrics]
            selected_metric = st.selectbox("Select Metric", metric_options)
            
            # Get the selected metric object
            selected_metric_code = selected_metric.split(" - ")[0] if " - " in selected_metric else selected_metric
            selected_metric_obj = next((m for m in metrics 
                                      if m["metric_code"] == selected_metric_code), None)
            
            if selected_metric_obj:
                st.markdown("### 📋 Metric Details")
                st.write(f"**Code:** {selected_metric_obj['metric_code']}")
                st.write(f"**Name:** {selected_metric_obj['metric_name']}")
                st.write(f"**Category:** {selected_metric_obj['category']}")
                st.write(f"**Provider:** {selected_metric_obj['provider']}")
                st.write(f"**Dataset:** {selected_metric_obj['dataset']}")
                st.write(f"**Indicator Code:** {selected_metric_obj['indicator_code']}")
                st.write(f"**Unit:** {selected_metric_obj['unit']}")
                st.write(f"**Frequency:** {selected_metric_obj['frequency']}")
                st.write(f"**Years:** {selected_metric_obj['start_year']} - {selected_metric_obj['end_year']}")
                
                if selected_metric_obj.get('synonyms'):
                    st.markdown("### 🔄 Synonyms")
                    for synonym in selected_metric_obj['synonyms']:
                        st.write(f"• {synonym}")
        
        with col2:
            st.subheader("🌍 Entity Selection")
            
            # Entity type selection
            entity_type = st.radio("Select Entity Type", ["Countries", "Aggregates", "Regions"])
            
            if entity_type == "Countries":
                selected_entities = st.multiselect(
                    "Select Countries",
                    sorted(entities["countries"]),
                    default=sorted(entities["countries"])[:5]
                )
            elif entity_type == "Aggregates":
                selected_entities = st.multiselect(
                    "Select Aggregates",
                    sorted(entities["aggregates"]),
                    default=sorted(entities["aggregates"])
                )
            else:  # Regions
                selected_entities = st.multiselect(
                    "Select Regions",
                    sorted(entities["regions"]),
                    default=sorted(entities["regions"])
                )
            
            st.markdown("### 📈 Analytics")
            if selected_entities:
                st.write(f"Selected {len(selected_entities)} entities")
                
                # Create a simple visualization
                import plotly.express as px
                
                # Mock data for visualization
                mock_data = {
                    'Entity': selected_entities,
                    'Value': [42, 38, 45, 39, 41][:len(selected_entities)],
                    'Category': [selected_metric_obj['category']] * len(selected_entities) if selected_metric_obj else ['N/A'] * len(selected_entities)
                }
                
                if mock_data['Entity']:
                    fig = px.bar(
                        mock_data,
                        x='Entity',
                        y='Value',
                        title=f"Sample Data for {selected_metric_code}",
                        color='Category'
                    )
                    st.plotly_chart(fig, use_container_width=True)
    
    # Footer
    st.markdown("""
    <div style="background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); padding: 30px; border-radius: 15px; color: white; text-align: center; margin-top: 50px;">
        <h3>📊 EconData Pipeline Dashboard</h3>
        <p>World Bank, OECD, and IMF Economic Data Extraction & Analysis</p>
        <p><em>Built with ❤️ for economic data exploration</em></p>
        <p><small>Last Updated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}</small></p>
    </div>
    """, unsafe_allow_html=True)

def display_metric_card(metric):
    """Display a metric in a card format"""
    st.markdown(f"""
    <div class="dataset-card">
        <h4 style="color: #1e40af; margin-bottom: 10px;">{metric.get('metric_name', metric.get('metric_code', 'Unknown'))}</h4>
        <p style="margin-bottom: 8px;"><strong>Code:</strong> {metric.get('metric_code', 'N/A')}</p>
        <p style="margin-bottom: 8px;"><strong>Category:</strong> <span style="background: #e0f2fe; padding: 2px 8px; border-radius: 10px;">{metric.get('category', 'N/A')}</span></p>
        <p style="margin-bottom: 8px;"><strong>Provider:</strong> {metric.get('provider', 'N/A')}</p>
        <p style="margin-bottom: 8px;"><strong>Unit:</strong> {metric.get('unit', 'N/A')}</p>
        <p style="margin-bottom: 8px;"><strong>Years:</strong> {metric.get('start_year', 'N/A')} - {metric.get('end_year', 'N/A')}</p>
        <details>
            <summary style="color: #64748b; cursor: pointer;">View Details</summary>
            <p style="margin-top: 10px; font-size: 0.9em;">{metric.get('description', 'N/A')[:200]}...</p>
        </details>
    </div>
    """, unsafe_allow_html=True)

if __name__ == "__main__":
    main()
