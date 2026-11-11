function getDashboardTabId() {
    let tabId = sessionStorage.getItem('dashboard-tab-id');

    if (!tabId) {
        tabId = crypto.randomUUID();
        sessionStorage.setItem('dashboard-tab-id', tabId);
    }

    return tabId;
}

// dashboard-api.js
// All HTTP and metadata-loading operations live here.

export async function request(url, options = {}) {
    const response = await fetch(url, {
        ...options,
        headers: {
            Accept: 'application/json',
            'X-Dashboard-Tab': getDashboardTabId(),
            ...(options.headers || {})
        }
    });

    const data = await response.json().catch(() => null);

    if (!response.ok) {
        throw new Error(data?.error || `Request failed (${response.status}).`);
    }

    return data;
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

export async function exportWidgetPdf(exportId) {
console.log('PDF export called with ID:', exportId);
    const response = await fetch(
        `/api/dash/widget-export/${encodeURIComponent(exportId)}`,
        {
            headers: {
                Accept: 'application/pdf',
                'X-Dashboard-Tab': getDashboardTabId()
            }
        }
    );

    if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.error || 'Could not generate PDF.');
    }

    const blob = await response.blob();
    const url = URL.createObjectURL(blob);

    const link = document.createElement('a');
    link.href = url;
    link.download = 'dashboard-table.pdf';
    document.body.appendChild(link);
    link.click();
    link.remove();

    URL.revokeObjectURL(url);
}

export async function loadDepartments() {
    const result = await request('/api/dash/departments');
    return (Array.isArray(result) ? result : [])
        .map(item => ({
            key: item.key || item.Key,
            label: item.label || item.Label || item.key || item.Key,
            role: item.role || item.Role || 'User'
        }));
}

export async function loadSelectedDepartment() {
    return request('/api/dash/department');
}

export async function saveDepartment(department) {
    const response = await fetch('/api/dash/department', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Dashboard-Tab': getDashboardTabId() },
        body: JSON.stringify({ department })
    });

    const data = await response.json().catch(() => null);

    if (!response.ok) {
        throw new Error(data?.error || 'Could not update department.');
    }
}

export async function loadDashboards() {
    const list = await request('/api/dash/dashboards');


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

export async function executeQuery(department, payload, title = '') {
    return request(
        `/api/dash/query-builder/execute?department=${encodeURIComponent(department)}&title=${encodeURIComponent(title)}`,
        {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }
    );
}
export async function loadWidget(department, dashboardKey, widgetKey, title) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/widget/${encodeURIComponent(widgetKey)}?department=${encodeURIComponent(department)}&page=1&pageSize=100&title=${encodeURIComponent(title || widgetKey)}`,
        { method: 'POST' }
    );
}


export async function selectDemoUser(userId) {
    return request('/api/dash/demo-user', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userId: Number(userId) })
    });
}

export async function loadSelectedDemoUser() {
    return request('/api/dash/demo-user');
}

export async function loadUserLayout(department, dashboardKey) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/layout?department=${encodeURIComponent(department)}`
    );
}

export async function saveUserLayout(department, dashboardKey, widgets) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/layout?department=${encodeURIComponent(department)}`,
        {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(widgets)
        }
    );


}

export async function loadDepartmentLayout(department, dashboardKey) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/department-layout?department=${encodeURIComponent(department)}`
    );
}

export async function publishDepartmentLayout(department, dashboardKey, widgets) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/department-layout?department=${encodeURIComponent(department)}`,
        {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(widgets)
        }
    );
}

export async function deleteDepartmentLayout(department, dashboardKey) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/department-layout?department=${encodeURIComponent(department)}`,
        { method: 'DELETE' }
    );
}


export async function loadDashboardPresets(department, dashboardKey) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/presets?department=${encodeURIComponent(department)}`
    );
}

export async function createDashboardPreset(department, dashboardKey, preset) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/presets?department=${encodeURIComponent(department)}`,
        {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(preset)
        }
    );
}

export async function updateDashboardPreset(department, dashboardKey, presetId, preset) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/presets/${presetId}?department=${encodeURIComponent(department)}`,
        {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(preset)
        }
    );
}

export async function deleteDashboardPreset(department, dashboardKey, presetId) {
    return request(
        `/api/dash/dashboard/${encodeURIComponent(dashboardKey)}/presets/${presetId}?department=${encodeURIComponent(department)}`,
        { method: 'DELETE' }
    );
}

export async function loadDrilldown(payload) {
    return request('/api/dash/drilldown', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });
}
