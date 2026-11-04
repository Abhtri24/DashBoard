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
    loadWidget
} from './dashboard-api.js';

import { createRenderer } from './dashboard-renderer.js';
import { createWidgetBuilder } from './widget-builder.js';

(() => {
    const $ = id => document.getElementById(id);

    const dashboardSelect = $('dashboardSelect');
    const departmentSelect = $('departmentSelect');
    const widgetsHost = $('dashboardWidgets');
    const loading = $('dashboardLoading');
    const errorBox = $('dashboardError');
    const notice = $('dashboardNotice');
    const modal = bootstrap.Modal.getOrCreateInstance($('widgetModal'));

    let currentDashboard = null;
    let widgets = [];
    let editMode = false;
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

    function getWidgets() {
        return widgets;
    }

    function setWidgets(value) {
        widgets = value;
    }

    const renderer = createRenderer({
        widgetsHost,
        getWidgets,
        getDepartment,
        getDashboardKey,
        getEditMode: () => editMode,
        requestWidget: loadWidget,
        openWidgetModal: widget => builder.openWidgetModal(widget),
        deleteWidget: index => builder.deleteWidget(index),
        getUIConfiguration: () => uiConfiguration,
        renderAfterReorder: () => builder.renderLocalDashboard()
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

            await renderer.renderDashboard();
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

        $('editLayoutButton').addEventListener('click', () => {
            editMode = !editMode;
            $('editLayoutButton').textContent =
                editMode ? 'Done editing' : 'Edit layout';

            widgetsHost.classList.toggle('dashboard-edit-mode', editMode);
            renderer.wireDragAndDrop();
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
