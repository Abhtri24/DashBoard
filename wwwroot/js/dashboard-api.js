// dashboard-api.js
// All HTTP and metadata-loading operations live here.

export async function request(url, options = {}) {
    const response = await fetch(url, {
        headers: {
            Accept: 'application/json',
            ...(options.headers || {})
        },
        ...options
    });

    const body = await response.json().catch(() => null);

    if (!response.ok) {
        throw new Error(
            body?.error ||
            body?.message ||
            `Request failed (${response.status}).`
        );
    }

    return body;
}

export async function loadUIConfiguration() {
    try {
        const configuration = await request('/api/dash/ui-configuration');

        return {
            dimensionLabel: configuration?.dimensionLabel || 'Dimensions',
            measureLabel: configuration?.measureLabel || 'Measures'
        };
    } catch (error) {
        console.error('Could not load UI configuration:', error);
        return {
            dimensionLabel: 'Dimensions',
            measureLabel: 'Measures'
        };
    }
}

export async function saveDepartment(department) {
    const response = await fetch('/api/dash/department', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ department })
    });

    const data = await response.json().catch(() => null);

    if (!response.ok) {
        throw new Error(data?.error || 'Could not update department.');
    }
}

export async function loadDashboards(department) {
    const list = await request(
        `/api/dash/dashboards?department=${encodeURIComponent(department)}`
    );

    const items = Array.isArray(list)
        ? list
        : (list?.value || list?.dashboards || list?.Dashboards || []);

    if (!items.length) {
        throw new Error('No dashboards are configured for this department.');
    }

    return items.map(item => ({
        key: item.dashboardKey || item.DashboardKey,
        label: item.displayName || item.DisplayName ||
            item.dashboardKey || item.DashboardKey
    }));
}

export async function loadDashboard(department, dashboardKey) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}?department=${encodeURIComponent(department)}`
    );
}

export async function loadDatasets(department) {
    const result = await request(
        `/api/dash/query-builder/datasets?department=${encodeURIComponent(department)}`
    );

    const datasets = Array.isArray(result)
        ? result
        : (result?.value || result?.datasets || result?.Datasets || []);

    return datasets.map(item => ({
        key: item.key || item.Key,
        label: item.label || item.Label || item.key || item.Key
    }));
}

export async function loadMetadata(department, dataset) {
    if (!dataset) throw new Error('Select a dataset.');

    const metadata = await request(
        `/api/dash/query-builder/metadata?department=${encodeURIComponent(department)}&dataset=${encodeURIComponent(dataset)}`
    );

    return {
        dimensions: metadata.dimensions || metadata.Dimensions || [],
        measures: metadata.measures || metadata.Measures || []
    };
}

export async function executeQuery(department, payload) {
    return request(
        `/api/dash/query-builder/execute?department=${encodeURIComponent(department)}`,
        {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }
    );
}

export async function loadWidget(department, dashboardKey, widgetKey) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/widget/${encodeURIComponent(widgetKey)}?department=${encodeURIComponent(department)}&page=1&pageSize=100`,
        { method: 'POST' }
    );
}
