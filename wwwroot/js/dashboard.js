
// dashboard.js
// Application entry point: shared page state, initialization and event wiring.

import {
    loadUIConfiguration,
    loadDepartments,
    loadSelectedDepartment,
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
    loadDepartmentLayout,
    publishDepartmentLayout,
    deleteDepartmentLayout,
    loadDashboardPresets,
    createDashboardPreset,
    updateDashboardPreset,
    deleteDashboardPreset,
    loadDrilldown,
    exportWidgetPdf
} from './dashboard-api.js';

import { createRenderer, } from './dashboard-renderer.js';
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

        if (isLoading) {
            $('drilldownEmpty').classList.add('d-none');
            $('drilldownError').classList.add('d-none');
        }
    }

    function getRowValue(row, key) {
        if (!row || !key) return undefined;

        if (Object.prototype.hasOwnProperty.call(row, key)) {
            return row[key];
        }

        const matchingKey = Object.keys(row).find(
            property => property.toLowerCase() === String(key).toLowerCase()
        );

        return matchingKey ? row[matchingKey] : undefined;
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

            th.textContent =
                column.label || column.Label ||
                column.key || column.Key || '';

            headerRow.append(th);
        });

        head.append(headerRow);

        rows.forEach(row => {
            const tr = document.createElement('tr');

            columns.forEach(column => {
                const key = column.key || column.Key;
                const td = document.createElement('td');

                td.textContent = formatDrilldownValue(
                    getRowValue(row, key)
                );

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

    function normalizeDrilldownFilters(widget) {
        return (widget.filters || widget.Filters || [])
            .map(filter => ({
                dimension: filter.columnKey || filter.ColumnKey ||
                    filter.dimension || filter.Dimension,
                operator: filter.operator || filter.Operator || 'eq',
                value: filter.filterValue ?? filter.FilterValue ??
                    filter.value ?? filter.Value
            }))
            .filter(filter => Boolean(filter.dimension));
    }

    async function fetchDrilldownPage(page) {
        if (!activeDrilldown) return;

        activeDrilldown.page = page;

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

    async function openDrilldown(widget, dimension, value, page = 1) {
        activeDrilldown = {
            dataset: widget.datasetKey || widget.DatasetKey,
            dimension,
            value,
            filters: normalizeDrilldownFilters(widget),
            page
        };

        $('drilldownModalTitle').textContent =
            widget.title || widget.Title || 'Underlying records';

        $('drilldownDescription').textContent =
            `${dimension}: ${value}`;

        drilldownModal.show();

        await fetchDrilldownPage(page);
    }

    let currentDashboard = null;
    let widgets = [];
    let editMode = false;
    let activeUserId = null;

    let departmentRoles = new Map();

    function updateDepartmentLayoutControls() {
        const role = departmentRoles.get(getDepartment());

        const isAdmin = String(role || '').toLowerCase() === 'admin';

        $('manageLayoutsButton').classList.toggle('d-none', !isAdmin);
    }

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

        try {
            sessionStorage.setItem(
                storageKey('draft'),
                JSON.stringify(widgets)
            );
        } catch {
            // Optional draft cache.
        }

        setLayoutButtons(true);
    }

    async function getCachedWidget(department, dashboardKey, widgetKey,title,widgetType) {

        if (String(widgetType).toUpperCase() === 'TABLE') {
        return loadWidget(department, dashboardKey, widgetKey, title);
    }
        const key = storageKey('data');

        let cache = {};



        try {
            cache = JSON.parse(sessionStorage.getItem(key) || '{}');
        } catch {
            cache = {};
        }

        const cacheKey = `${department}:${dashboardKey}:${widgetKey}`;
        const entry = cache[cacheKey];

        if (entry && Date.now() - entry.timestamp < CACHE_TTL_MS) {
            return entry.data;
        }

        const data = await loadWidget(
            department,
            dashboardKey,
            widgetKey,
            title
        );

        cache[cacheKey] = {
            timestamp: Date.now(),
            data
        };

        try {
            sessionStorage.setItem(key, JSON.stringify(cache));
        } catch {
            // Cache is optional.
        }

        return data;
    }

    async function loadPersonalizedLayout() {
        // Start with the dashboard's configured widgets.
        widgets = [
            ...(currentDashboard.widgets ||
                currentDashboard.Widgets || [])
        ];

        // Department layout is the shared default.
        const departmentLayout = await loadDepartmentLayout(
            getDepartment(),
            getDashboardKey()
        );

        const departmentJson =
            departmentLayout?.layoutJson ||
            departmentLayout?.LayoutJson;

        if (departmentJson) {
            widgets = JSON.parse(departmentJson);
        }

        // A personal saved layout overrides the shared default.
        const saved = await loadUserLayout(
            getDepartment(),
            getDashboardKey()
        );

        const personalJson =
            saved?.layoutJson ||
            saved?.LayoutJson;

        if (personalJson) {
            widgets = JSON.parse(personalJson);
        }

        // An unsaved local draft takes highest priority.
        const draft = sessionStorage.getItem(storageKey('draft'));

        if (draft) {
            try {
                widgets = JSON.parse(draft);
                setLayoutButtons(true);
                return;
            } catch {
                sessionStorage.removeItem(storageKey('draft'));
            }
        }

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
        exportWidgetPdf,
        getDepartment,
        getDashboardKey,
        getEditMode: () => editMode,
       requestWidget: (department, dashboardKey, widgetKey, title, widgetType) =>
    getCachedWidget(department, dashboardKey, widgetKey, title, widgetType),
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
                ...(currentDashboard.widgets ||
                    currentDashboard.Widgets || [])
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


    function showNotice(message) {
        notice.textContent = message;
        notice.classList.remove('d-none');
    }

    async function renderPresetList(isAdmin) {
        const presets = await loadDashboardPresets(getDepartment(), getDashboardKey());
        const host = $('presetList');
        host.replaceChildren();
        if (isAdmin) {
            const create = document.createElement('button');
            create.type = 'button';
            create.className = 'btn btn-primary align-self-start';
            create.textContent = $('editingPresetId').value
                ? 'Update selected preset from current dashboard'
                : 'Save current dashboard as a preset';
            create.addEventListener('click', () => {
                if ($('editingPresetId').value) {
                    $('savePresetButton').classList.add('d-none');
                    $('updatePresetButton').classList.remove('d-none');
                } else {
                    $('savePresetButton').classList.remove('d-none');
                    $('updatePresetButton').classList.add('d-none');
                }
                const libraryElement = $('layoutLibraryModal');
                libraryElement.addEventListener('hidden.bs.modal', () => {
                    bootstrap.Modal.getOrCreateInstance($('presetEditorModal')).show();
                }, { once: true });
                bootstrap.Modal.getOrCreateInstance(libraryElement).hide();
            });
            host.append(create);
        }
        if (!presets.length) {
            const empty = document.createElement('div');
            empty.className = 'text-secondary';
            empty.textContent = 'No layouts have been created for this dashboard yet.';
            host.append(empty);
            return;
        }
        presets.forEach(preset => {
            const id = preset.presetId || preset.PresetId;
            const name = preset.presetName || preset.PresetName;
            const description = preset.description || preset.Description || '';
            const item = document.createElement('div');
            item.className = 'border rounded p-3 d-flex flex-wrap justify-content-between align-items-center gap-2';
            const details = document.createElement('div');
            const title = document.createElement('strong');
            title.textContent = name;
            const subtitle = document.createElement('div');
            subtitle.className = 'small text-secondary';
            subtitle.textContent = description;
            details.append(title, subtitle);
            const actions = document.createElement('div');
            actions.className = 'd-flex flex-wrap gap-2';
            const addAction = (action, label, css) => {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = css;
                button.textContent = label;
                button.dataset.action = action;
                button.dataset.id = id;
                actions.append(button);
            };
            addAction('apply', 'Apply to my dashboard', 'btn btn-sm btn-primary');
            if (isAdmin) {
                addAction('default', 'Set as department default', 'btn btn-sm btn-outline-primary');
                addAction('edit', 'Edit', 'btn btn-sm btn-outline-secondary');
                addAction('delete', 'Delete', 'btn btn-sm btn-outline-danger');
            }
            item.append(details, actions);
            host.append(item);
        });
    }

    async function openLayoutManager() {
        clearError($('presetModalError'));
        try {
            await renderPresetList(true);
            bootstrap.Modal.getOrCreateInstance($('layoutLibraryModal')).show();
        } catch (error) { showError(error.message); }
    }

    async function openLayoutLibrary() {
        clearError($('presetListError'));
        try {
            await renderPresetList(false);
            bootstrap.Modal.getOrCreateInstance($('layoutLibraryModal')).show();
        } catch (error) { showError(error.message); }
    }

    function bindPageEvents() {
        builder.bindEvents();

        $('saveLayoutButton').addEventListener('click', async () => {
            try {
                await saveUserLayout(
                    getDepartment(),
                    getDashboardKey(),
                    widgets
                );

                sessionStorage.removeItem(storageKey('draft'));
                setLayoutButtons(false);

                notice.textContent = 'Layout saved for this demo user.';
                notice.classList.remove('d-none');
            } catch (error) {
                showError(error.message);
            }
        });


        $('manageLayoutsButton').addEventListener('click', openLayoutManager);

        $('openLayoutLibraryButton').addEventListener('click', openLayoutLibrary);

        $('savePresetButton').addEventListener('click', async () => {
            const name = $('presetNameInput').value.trim();
            const description = $('presetDescriptionInput').value.trim();
            if (!name) {
                showError('Enter a name for this preset.', $('presetModalError'));
                return;
            }
            try {
                await createDashboardPreset(getDepartment(), getDashboardKey(), {
                    presetName: name,
                    description,
                    layout: widgets
                });
                $('presetNameInput').value = '';
                $('presetDescriptionInput').value = '';
                clearError($('presetModalError'));
                await renderPresetList(true);
                bootstrap.Modal.getOrCreateInstance($('presetEditorModal')).hide();
                showNotice('Preset saved for this department.');
            } catch (error) {
                showError(error.message, $('presetModalError'));
            }
        });

        $('presetList').addEventListener('click', async event => {
            const button = event.target.closest('button[data-action]');
            if (!button) return;
            const presetId = Number(button.dataset.id);
            const preset = (await loadDashboardPresets(getDepartment(), getDashboardKey()))
                .find(item => Number(item.presetId || item.PresetId) === presetId);
            if (!preset) return;
            const presetJson = preset.layoutJson || preset.LayoutJson;
            if (button.dataset.action === 'apply') {
                try {
                    const selectedWidgets = JSON.parse(presetJson);
                    await saveUserLayout(getDepartment(), getDashboardKey(), selectedWidgets);
                    sessionStorage.removeItem(storageKey('draft'));
                    setLayoutButtons(false);
                    await loadDashboard();
                    showNotice('Preset applied to your personal dashboard.');
                    bootstrap.Modal.getOrCreateInstance($('layoutLibraryModal')).hide();
                } catch (error) { showError(error.message, $('presetListError')); }
            } else if (button.dataset.action === 'default') {
                try {
                    await publishDepartmentLayout(getDepartment(), getDashboardKey(), JSON.parse(presetJson));
                    showNotice('This preset is now the department default.');
                    await renderPresetList(true);
                } catch (error) { showError(error.message, $('presetListError')); }
            } else if (button.dataset.action === 'edit') {
                widgets = JSON.parse(presetJson);
                $('editingPresetId').value = String(presetId);
                $('presetNameInput').value = preset.presetName || preset.PresetName || '';
                $('presetDescriptionInput').value = preset.description || preset.Description || '';
                bootstrap.Modal.getOrCreateInstance($('layoutLibraryModal')).hide();
                await builder.renderLocalDashboard();
                showNotice('Preset loaded for editing. Modify the widgets, then open Manage layouts and choose Save current dashboard as a preset to update it.');
            } else if (button.dataset.action === 'delete') {
                if (!confirm('Delete this preset? Users who already applied it keep their personal layouts.')) return;
                try {
                    await deleteDashboardPreset(getDepartment(), getDashboardKey(), presetId);
                    await renderPresetList(true);
                    showNotice('Preset deleted.');
                } catch (error) { showError(error.message, $('presetListError')); }
            }
        });

        $('updatePresetButton').addEventListener('click', async () => {
            const presetId = Number($('editingPresetId').value);
            try {
                await updateDashboardPreset(getDepartment(), getDashboardKey(), presetId, {
                    presetName: $('presetNameInput').value.trim(),
                    description: $('presetDescriptionInput').value.trim(),
                    layout: widgets
                });
                $('editingPresetId').value = '';
                $('savePresetButton').classList.remove('d-none');
                $('updatePresetButton').classList.add('d-none');
                bootstrap.Modal.getOrCreateInstance($('presetEditorModal')).hide();
                await openLayoutManager();
                showNotice('Preset updated.');
            } catch (error) { showError(error.message, $('presetModalError')); }
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

                const departments = await loadDepartments();
                departmentRoles = new Map(
                    departments.map(item => [item.key, item.role])
                );
                if (!departments.length) {
                    throw new Error('This user has no active department memberships.');
                }

                departmentSelect.replaceChildren(
                    ...departments.map(item => new Option(item.label, item.key))
                );

                await saveDepartment(departments[0].key);
                updateDepartmentLayoutControls();
                await refreshDashboardList();
                await loadDashboard();
            } catch (error) {
                showError(error.message);
            }
        });

        $('editLayoutButton').addEventListener('click', () => {
            editMode = !editMode;

            $('editLayoutButton').textContent =
                editMode ? 'Done editing' : 'Edit layout';

            widgetsHost.classList.toggle(
                'dashboard-edit-mode',
                editMode
            );

            renderer.wireDragAndDrop();
        });

        $('drilldownPrevious').addEventListener('click', () => {
            if (activeDrilldown && activeDrilldown.page > 1) {
                fetchDrilldownPage(activeDrilldown.page - 1);
            }
        });

        $('drilldownNext').addEventListener('click', () => {
            if (activeDrilldown) {
                fetchDrilldownPage(activeDrilldown.page + 1);
            }
        });

        dashboardSelect.addEventListener('change', loadDashboard);

        departmentSelect.addEventListener('change', async () => {
            clearError();

            try {
                await saveDepartment(getDepartment());
                updateDepartmentLayoutControls();
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
            if (!demoUsers.length) {
                throw new Error('No active demo users with department memberships are configured.');
            }

            demoUserSelect.replaceChildren(
                ...demoUsers.map(user => new Option(
                    user.displayName || user.DisplayName,
                    user.userId || user.UserId
                ))
            );

            try {
                const selected = await loadSelectedDemoUser();
                activeUserId = Number(selected.userId || selected.UserId);
            } catch {
                activeUserId = Number(
                    demoUsers[0].userId || demoUsers[0].UserId
                );
                await selectDemoUser(activeUserId);
            }

            if (!demoUsers.some(user =>
                Number(user.userId || user.UserId) === activeUserId
            )) {
                activeUserId = Number(
                    demoUsers[0].userId || demoUsers[0].UserId
                );
                await selectDemoUser(activeUserId);
            }

            demoUserSelect.value = String(activeUserId);

            const departments = await loadDepartments();
            departmentRoles = new Map(
                departments.map(item => [item.key, item.role]));
            if (!departments.length) {
                throw new Error('This user has no active department memberships.');
            }

            departmentSelect.replaceChildren(
                ...departments.map(item => new Option(item.label, item.key))
            );

            let selectedDepartment = null;
            try {
                const selected = await loadSelectedDepartment();
                selectedDepartment = selected.department || selected.Department;
            } catch {
                // Department selection is reset when the demo user changes.
            }

            if (!departments.some(item => item.key === selectedDepartment)) {
                selectedDepartment = departments[0].key;
                await saveDepartment(selectedDepartment);
            }

            departmentSelect.value = selectedDepartment;
            updateDepartmentLayoutControls();

            uiConfiguration = await loadUIConfiguration();
            applyUILabels();

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
    