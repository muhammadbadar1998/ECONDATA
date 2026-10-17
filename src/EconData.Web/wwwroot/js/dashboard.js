const grids = new Map();
const charts = new Map();
let dotNetRef = null;
let saveTimer = null;
let pendingLayout = null;

function emitLayout(layout) {
    pendingLayout = layout;
    if (saveTimer) {
        clearTimeout(saveTimer);
    }

    saveTimer = setTimeout(() => {
        saveTimer = null;
        if (dotNetRef && pendingLayout) {
            dotNetRef.invokeMethodAsync('OnLayoutChanged', JSON.stringify(pendingLayout));
        }
    }, 150);
}

export function initGrid(containerId, widgetsJson, ref) {
    dotNetRef = ref;
    const element = document.getElementById(containerId);
    if (!element || typeof GridStack === 'undefined') {
        return;
    }

    const existing = grids.get(containerId);
    if (existing) {
        existing.destroy(false);
        grids.delete(containerId);
    }

    const widgets = JSON.parse(widgetsJson);
    for (const widget of widgets) {
        const item = element.querySelector(`.grid-stack-item[gs-id="${widget.id}"]`);
        if (item) {
            item.setAttribute('gs-x', widget.x);
            item.setAttribute('gs-y', widget.y);
            item.setAttribute('gs-w', widget.w);
            item.setAttribute('gs-h', widget.h);
        }
    }

    const grid = GridStack.init({
        column: 12,
        cellHeight: 80,
        margin: 8,
        float: false,
        draggable: { handle: '.widget-header' },
        resizable: { handles: 'se,sw,e,w,s' }
    }, element);

    grid.on('change', (_event, items) => {
        const layout = items
            .filter(item => item.el && item.el.getAttribute('gs-id'))
            .map(item => ({
                id: item.el.getAttribute('gs-id'),
                x: item.x,
                y: item.y,
                w: item.w,
                h: item.h
            }));
        emitLayout(layout);
    });

    grid.on('resizestop', () => resizeCharts());
    grids.set(containerId, grid);
}

export function renderCharts(chartsJson) {
    const entries = JSON.parse(chartsJson);
    for (const entry of entries) {
        const element = document.getElementById(`chart-${entry.id}`);
        if (!element || typeof echarts === 'undefined') {
            continue;
        }

        const existing = charts.get(entry.id);
        if (existing) {
            existing.dispose();
            charts.delete(entry.id);
        }

        const chart = echarts.init(element);
        chart.setOption(entry.option, true);
        charts.set(entry.id, chart);
    }
}

export function disposeChart(id) {
    const chart = charts.get(id);
    if (chart) {
        chart.dispose();
        charts.delete(id);
    }
}

export function resizeCharts() {
    for (const chart of charts.values()) {
        chart.resize();
    }
}

export function dispose() {
    if (saveTimer) {
        clearTimeout(saveTimer);
        saveTimer = null;
    }

    for (const chart of charts.values()) {
        chart.dispose();
    }

    charts.clear();

    for (const grid of grids.values()) {
        grid.destroy(false);
    }

    grids.clear();
    dotNetRef = null;
    pendingLayout = null;
}
