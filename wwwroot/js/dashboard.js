// dashboard.js
// Application entry point: shared page state, initialization and event wiring.

import {
    request,
    loadUIConfiguration,
    saveDepartment,
    loadDashboards,
    loadDashboard as fetchDashboard,
    loadDatasets as fetchDatasets,
    loadMetadata as fetchMetadata,
    executeQuery,
    loadWidget,
    loadDemoUsers,
    selectDemoUser,
    loadSelectedDemoUser,
    loadUserLayout,
    saveUserLayout,
    loadDrilldown
} from './dashboard-api.js';

import { createRenderer } from './dashboard-renderer.js';
import { createWidgetBuilder } from './widget-builder.js';

(() => {
    const $ = id => document.getElementById(id);

    const dashboardSelect = $('dashboardSelect');
    const departmentSelect = $('departmentSelect');
    const demoUserSelect = $('demoUserSelect');
    const widgetsHost = $('dashboardWidgets');
    const loading = $('dashboardLoading');
    const errorBox = $('dashboardError');
    const notice = $('dashboardNotice');
    const modal = bootstrap.Modal.getOrCreateInstance($('widgetModal'));

    const drilldownModal = bootstrap.Modal.getOrCreateInstance(
        $('drilldownModal')
    );

    let activeDrilldown = null;

    function formatDrilldownValue(value) {
        if (value == null) return '—';

        if (typeof value === 'number') {
            return new Intl.NumberFormat('en-IN', {
                maximumFractionDigits: 2
            }).format(value);
        }

        return String(value);
    }

    function setDrilldownLoading(isLoading) {
        $('drilldownLoading').classList.toggle('d-none', !isLoading);
        $('drilldownTableContainer').classList.toggle('d-none', isLoading);
        $('drilldownError').classList.add('d-none');
    }

    function renderDrilldownResult(result) {
        const columns = result.columns || result.Columns || [];
        const rows = result.rows || result.Rows || [];

        const head = $('drilldownTableHead');
        const body = $('drilldownTableBody');

        head.replaceChildren();
        body.replaceChildren();

        const headerRow = document.createElement('tr');

        columns.forEach(column => {
            const th = document.createElement('th');
            th.textContent = column.label || column.Label ||
                column.key || column.Key;
            headerRow.append(th);
        });

        head.append(headerRow);

        rows.forEach(row => {
            const tr = document.createElement('tr');

            columns.forEach(column => {
                const key = column.key || column.Key;
                const td = document.createElement('td');
                td.textContent = formatDrilldownValue(row[key]);
                tr.append(td);
            });

            body.append(tr);
        });

        const hasRows = rows.length > 0;

        $('drilldownEmpty').classList.toggle('d-none', hasRows);
        $('drilldownTableContainer').classList.toggle('d-none', !hasRows);

        const page = result.page || result.Page || activeDrilldown.page;
        const hasMore = result.hasMore ?? result.HasMore ?? false;

        $('drilldownPageInfo').textContent =
            `Page ${page} · ${rows.length} records`;

        $('drilldownPrevious').disabled = page <= 1;
        $('drilldownNext').disabled = !hasMore;
    }

    async function openDrilldown(widget, dimension, value, page = 1) {
        const filters = (widget.filters || widget.Filters || []).map(filter => ({
            dimension: filter.columnKey || filter.ColumnKey ||
                filter.dimension || filter.Dimension,
            operator: filter.operator || filter.Operator || 'eq',
            value: filter.filterValue ?? filter.FilterValue ??
                filter.value ?? filter.Value
        }));

        activeDrilldown = {
            dataset: widget.datasetKey || widget.DatasetKey,
            dimension,
            value,
            filters,
            page
        };

        $('drilldownModalTitle').textContent =
            widget.title || widget.Title || 'Underlying records';

        $('drilldownDescription').textContent =
            `${dimension}: ${value}`;

        drilldownModal.show();
        setDrilldownLoading(true);

        try {
            const result = await loadDrilldown(activeDrilldown);
            renderDrilldownResult(result);
        } catch (error) {
            $('drilldownTableContainer').classList.add('d-none');
            $('drilldownEmpty').classList.add('d-none');
            showError(error.message, $('drilldownError'));
        } finally {
            $('drilldownLoading').classList.add('d-none');
        }
    }

    let currentDashboard = null;
    let widgets = [];
    let editMode = false;
    let activeUserId = null;
    const CACHE_TTL_MS = 5 * 60 * 1000;
    let uiConfiguration = {
        dimensionLabel: 'Dimensions',
        measureLabel: 'Measures'
    };

    function showError(message, target = errorBox) {
        target.textContent = message;
        target.classList.remove('d-none');
    }

    function clearError(target = errorBox) {
        target.textContent = '';
        target.classList.add('d-none');
    }

    function getDepartment() {
        return departmentSelect.value;
    }

    function getDashboardKey() {
        return dashboardSelect.value;
    }

    function storageKey(kind) {
        return `dashboard-${kind}:${activeUserId}:${getDashboardKey()}:${getDepartment()}`;
    }

    function setLayoutButtons(isDirty) {
        $('saveLayoutButton').disabled = !isDirty;
        $('discardLayoutButton').disabled = !isDirty;
    }

    function markDraft() {
        if (!activeUserId) return;
        try { sessionStorage.setItem(storageKey('draft'), JSON.stringify(widgets)); } catch { /* Optional draft cache. */ }
        setLayoutButtons(true);
    }

    async function getCachedWidget(department, dashboardKey, widgetKey) {
        const key = storageKey('data');
        let cache = {};
        try { cache = JSON.parse(sessionStorage.getItem(key) || '{}'); } catch { cache = {}; }
        const cacheKey = `${department}:${dashboardKey}:${widgetKey}`;
        const entry = cache[cacheKey];
        if (entry && Date.now() - entry.timestamp < CACHE_TTL_MS) return entry.data;
        const data = await loadWidget(department, dashboardKey, widgetKey);
        cache[cacheKey] = { timestamp: Date.now(), data };
        try { sessionStorage.setItem(key, JSON.stringify(cache)); } catch { /* Cache is optional. */ }
        return data;
    }

    async function loadPersonalizedLayout() {
        const draft = sessionStorage.getItem(storageKey('draft'));
        if (draft) {
            try { widgets = JSON.parse(draft); setLayoutButtons(true); return; }
            catch { sessionStorage.removeItem(storageKey('draft')); }
        }
        const saved = await loadUserLayout(getDepartment(), getDashboardKey());
        const json = saved?.layoutJson || saved?.LayoutJson;
        if (json) widgets = JSON.parse(json);
        setLayoutButtons(false);
    }

    function getWidgets() {
        return widgets;
    }

    function setWidgets(value) {
        widgets = value;
        markDraft();
    }

    const renderer = createRenderer({
        widgetsHost,
        getWidgets,
        getDepartment,
        getDashboardKey,
        getEditMode: () => editMode,
        requestWidget: getCachedWidget,
        openWidgetModal: widget => builder.openWidgetModal(widget),
        deleteWidget: index => builder.deleteWidget(index),
        getUIConfiguration: () => uiConfiguration,
        renderAfterReorder: () => builder.renderLocalDashboard(),
        onDrilldown: openDrilldown
    });

    const builder = createWidgetBuilder({
        $,
        modal,
        requestDatasets: fetchDatasets,
        requestMetadata: fetchMetadata,
        executeQuery,
        getDepartment,
        getWidgets,
        setWidgets,
        renderer,
        showError,
        clearError,
        getUIConfiguration: () => uiConfiguration,
        notice
    });

    function applyUILabels() {
        $('dimensionLabel').textContent = uiConfiguration.dimensionLabel;
        $('measureLabel').textContent = uiConfiguration.measureLabel;
    }

    async function loadDashboard() {
        clearError();
        loading.classList.remove('d-none');
        widgetsHost.classList.add('d-none');
        widgetsHost.replaceChildren();
        renderer.destroyCharts();

        try {
            currentDashboard = await fetchDashboard(
                getDepartment(),
                getDashboardKey()
            );

            widgets = [
                ...(currentDashboard.widgets || currentDashboard.Widgets || [])
            ];
            await loadPersonalizedLayout();

            $('dashboardTitle').textContent =
                currentDashboard.displayName ||
                currentDashboard.DisplayName ||
                'Dashboard';

            $('dashboardDescription').textContent =
                currentDashboard.description ||
                currentDashboard.Description ||
                '';

            loading.classList.add('d-none');
            widgetsHost.classList.remove('d-none');

            await builder.renderLocalDashboard();
        } catch (error) {
            loading.classList.add('d-none');
            showError(error.message);
        }
    }

    async function refreshDashboardList() {
        const items = await loadDashboards(getDepartment());
        dashboardSelect.replaceChildren();

        items.forEach(item => {
            dashboardSelect.add(new Option(item.label, item.key));
        });

        dashboardSelect.selectedIndex = 0;
    }

    function bindPageEvents() {
        builder.bindEvents();
        $('saveLayoutButton').addEventListener('click', async () => {
            try {
                await saveUserLayout(getDepartment(), getDashboardKey(), widgets);
                sessionStorage.removeItem(storageKey('draft'));
                setLayoutButtons(false);
                notice.textContent = 'Layout saved for this demo user.';
                notice.classList.remove('d-none');
            } catch (error) { showError(error.message); }
        });
        $('discardLayoutButton').addEventListener('click', async () => {
            sessionStorage.removeItem(storageKey('draft'));
            await loadDashboard();
        });
        $('refreshDataButton').addEventListener('click', async () => {
            sessionStorage.removeItem(storageKey('data'));
            await loadDashboard();
        });
        demoUserSelect.addEventListener('change', async () => {
            try {
                await selectDemoUser(Number(demoUserSelect.value));
                activeUserId = Number(demoUserSelect.value);
                await loadDashboard();
            } catch (error) { showError(error.message); }
        });


        $('editLayoutButton').addEventListener('click', () => {
            editMode = !editMode;
            $('editLayoutButton').textContent =
                editMode ? 'Done editing' : 'Edit layout';

            widgetsHost.classList.toggle('dashboard-edit-mode', editMode);
            renderer.wireDragAndDrop();
        });

        $('drilldownPrevious').addEventListener('click', () => {
            if (activeDrilldown && activeDrilldown.page > 1) {
                openDrilldown(
                    {
                        datasetKey: activeDrilldown.dataset,
                        filters: activeDrilldown.filters
                    },
                    activeDrilldown.dimension,
                    activeDrilldown.value,
                    activeDrilldown.page - 1
                );
            }
        });

        $('drilldownNext').addEventListener('click', () => {
            if (activeDrilldown) {
                openDrilldown(
                    {
                        datasetKey: activeDrilldown.dataset,
                        filters: activeDrilldown.filters
                    },
                    activeDrilldown.dimension,
                    activeDrilldown.value,
                    activeDrilldown.page + 1
                );
            }
        });

        dashboardSelect.addEventListener('change', loadDashboard);

        departmentSelect.addEventListener('change', async () => {
            clearError();

            try {
                await saveDepartment(getDepartment());
                await refreshDashboardList();
                await loadDashboard();
            } catch (error) {
                loading.classList.add('d-none');
                showError(error.message);
            }
        });
    }

    async function initialize() {
        try {
            const demoUsers = await loadDemoUsers();
            demoUserSelect.replaceChildren(...demoUsers.map(user => new Option(user.displayName || user.DisplayName, user.userId || user.UserId)));
            try {
                const selected = await loadSelectedDemoUser();
                activeUserId = selected.userId || selected.UserId;
            } catch {
                if (!demoUsers.length) throw new Error('No active demo users are configured. Run the dashboard user/layout SQL script first.');
                activeUserId = Number(demoUsers[0].userId || demoUsers[0].UserId);
                await selectDemoUser(activeUserId);
            }
            demoUserSelect.value = String(activeUserId);
            uiConfiguration = await loadUIConfiguration();
            applyUILabels();

            await saveDepartment(getDepartment());
            await refreshDashboardList();
            await loadDashboard();
        } catch (error) {
            loading.classList.add('d-none');
            showError(error.message);
        }
    }

    bindPageEvents();
    initialize();
})();
