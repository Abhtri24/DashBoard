
(() => {
    const $ = id => document.getElementById(id);

    const dashboardSelect = $('dashboardSelect');
    const departmentSelect = $('departmentSelect');
    const widgetsHost = $('dashboardWidgets');
    const loading = $('dashboardLoading');
    const errorBox = $('dashboardError');
    const notice = $('dashboardNotice');
    const modalElement = $('widgetModal');
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);

    let currentDashboard = null;
    let widgets = [];
    let dimensions = [];
    let measures = [];
    let lastPreviewRows = [];
    let editMode = false;
    let chartInstances = [];

    const dashboardUrl = key =>
        `/api/dash/dashboard/${encodeURIComponent(key)}?department=${encodeURIComponent(departmentSelect.value)}`;

    const widgetUrl = (key, widgetKey) =>
        `/api/dash/dashboard/${encodeURIComponent(key)}/widget/${encodeURIComponent(widgetKey)}?department=${encodeURIComponent(departmentSelect.value)}&page=1&pageSize=100`;

    const queryBuilderUrl = '/api/dash/query-builder';

    // ---------------------------------------------------------
    // Common helpers
    // ---------------------------------------------------------

    function showError(message, target = errorBox) {
        target.textContent = message;
        target.classList.remove('d-none');
    }

    function clearError(target = errorBox) {
        target.textContent = '';
        target.classList.add('d-none');
    }

    function el(tag, className, text) {
        const node = document.createElement(tag);

        if (className) node.className = className;
        if (text !== undefined && text !== null) {
            node.textContent = text;
        }

        return node;
    }

    async function request(url, options = {}) {
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

    function keyOf(item) {
        return item?.columnKey || item?.ColumnKey || '';
    }

    function labelOf(item) {
        return item?.labelOverride ||
            item?.LabelOverride ||
            item?.label ||
            item?.Label ||
            keyOf(item);
    }

    function getRows(result) {
        if (Array.isArray(result)) return result;

        return result?.value ||
            result?.Value ||
            result?.rows ||
            result?.Rows ||
            [];
    }

    function getProp(obj, lower, upper) {
        return obj?.[lower] ?? obj?.[upper];
    }

    function measureOf(widget) {
        return getProp(widget, 'measures', 'Measures')?.[0] || null;
    }

    function dimensionOf(widget) {
        return getProp(widget, 'dimensions', 'Dimensions')?.[0] || null;
    }

    function findValue(row, key) {
        const actual = Object.keys(row || {}).find(
            k => k.toLowerCase() === String(key || '').toLowerCase()
        );

        return actual ? row[actual] : undefined;
    }

    function format(value) {
        if (value == null) return '—';

        return typeof value === 'number'
            ? new Intl.NumberFormat('en-IN', {
                maximumFractionDigits: 2
            }).format(value)
            : String(value);
    }

    // ---------------------------------------------------------
    // Department and dashboard loading
    // ---------------------------------------------------------

    async function saveDepartment() {
        const response = await fetch('/api/dash/department', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                department: departmentSelect.value
            })
        });

        const data = await response.json().catch(() => null);

        if (!response.ok) {
            throw new Error(
                data?.error || 'Could not update department.'
            );
        }
    }

    async function loadDashboards() {
        const list = await request(
            `/api/dash/dashboards?department=${encodeURIComponent(departmentSelect.value)}`
        );

        const items = Array.isArray(list)
            ? list
            : (list?.value || list?.dashboards || list?.Dashboards || []);

        dashboardSelect.replaceChildren();

        if (!items.length) {
            throw new Error(
                'No dashboards are configured for this department.'
            );
        }

        items.forEach(item => {
            const key = item.dashboardKey || item.DashboardKey;
            const label = item.displayName || item.DisplayName || key;

            dashboardSelect.add(new Option(label, key));
        });

        dashboardSelect.selectedIndex = 0;
    }

    async function loadDashboard() {
        clearError();

        loading.classList.remove('d-none');
        widgetsHost.classList.add('d-none');
        widgetsHost.replaceChildren();

        destroyCharts();

        try {
            const data = await request(
                dashboardUrl(dashboardSelect.value)
            );

            currentDashboard = data;

            widgets = [
                ...(getProp(data, 'widgets', 'Widgets') || [])
            ];

            $('dashboardTitle').textContent =
                getProp(data, 'displayName', 'DisplayName') ||
                'Dashboard';

            $('dashboardDescription').textContent =
                getProp(data, 'description', 'Description') || '';

            loading.classList.add('d-none');
            widgetsHost.classList.remove('d-none');

            if (!widgets.length) {
                widgetsHost.append(
                    el(
                        'p',
                        'text-secondary',
                        'No cards are configured. Use Add card to create one.'
                    )
                );
                return;
            }

            for (let i = 0; i < widgets.length; i++) {
                await renderOne(widgets[i], i);
            }

            wireDragAndDrop();
        } catch (error) {
            loading.classList.add('d-none');
            showError(error.message);
        }
    }

    // ---------------------------------------------------------
    // Query Builder: department-scoped datasets and metadata
    // ---------------------------------------------------------

    async function loadDatasets(selectedDataset = null) {
        const department = departmentSelect.value;

        const result = await request(
            `${queryBuilderUrl}/datasets?department=${encodeURIComponent(department)}`
        );

        const datasets = Array.isArray(result)
            ? result
            : (
                result?.value ||
                result?.datasets ||
                result?.Datasets ||
                []
            );

        const datasetSelect = $('datasetSelect');

        datasetSelect.replaceChildren();

        if (!datasets.length) {
            dimensions = [];
            measures = [];

            throw new Error(
                `No datasets are available for ${department}.`
            );
        }

        datasets.forEach(dataset => {
            const key = dataset.key || dataset.Key;
            const label = dataset.label || dataset.Label || key;

            datasetSelect.add(new Option(label, key));
        });

        const matchingDataset = datasets.find(dataset => {
            const key = dataset.key || dataset.Key;

            return String(key).toLowerCase() ===
                String(selectedDataset || '').toLowerCase();
        });

        datasetSelect.value = matchingDataset
            ? (matchingDataset.key || matchingDataset.Key)
            : (datasets[0].key || datasets[0].Key);

        await loadMetadata();
    }

    async function loadMetadata() {
        const department = departmentSelect.value;
        const dataset = $('datasetSelect').value;

        if (!dataset) {
            throw new Error('Select a dataset.');
        }

        const metadata = await request(
            `${queryBuilderUrl}/metadata?department=${encodeURIComponent(department)}&dataset=${encodeURIComponent(dataset)}`
        );

        dimensions = metadata.dimensions || metadata.Dimensions || [];
        measures = metadata.measures || metadata.Measures || [];

        const dimensionSelect = $('dimensionSelect');
        const measureSelect = $('measureSelect');

        dimensionSelect.replaceChildren(
            new Option('(No grouping)', '')
        );

        measureSelect.replaceChildren();

        dimensions.forEach(item => {
            dimensionSelect.add(
                new Option(
                    item.label || item.Label,
                    item.key || item.Key
                )
            );
        });

        measures.forEach(item => {
            measureSelect.add(
                new Option(
                    item.label || item.Label,
                    item.key || item.Key
                )
            );
        });

        updateDimensionVisibility();
    }

    async function executeQuery(payload) {
        const department = departmentSelect.value;

        return await request(
            `${queryBuilderUrl}/execute?department=${encodeURIComponent(department)}`,
            {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(payload)
            }
        );
    }

    // ---------------------------------------------------------
    // Widget rendering
    // ---------------------------------------------------------

    function destroyCharts() {
        chartInstances.forEach(chart => chart.destroy());
        chartInstances = [];
    }

    function setGridSpan(node, widget) {
        const col = Number(
            getProp(widget, 'gridColumnSpan', 'GridColumnSpan') ||
            (
                String(
                    getProp(widget, 'widgetType', 'WidgetType')
                ).toUpperCase() === 'KPI' ? 3 : 6
            )
        );

        const row = Number(
            getProp(widget, 'gridRowSpan', 'GridRowSpan') || 1
        );

        node.style.gridColumn =
            `span ${Math.max(1, Math.min(12, col))}`;

        node.style.gridRow = `span ${Math.max(1, row)}`;
    }

    function makeWidgetShell(widget, index) {
        const section = el('section', 'dashboard-widget');

        setGridSpan(section, widget);
        section.dataset.index = index;

        const card = el('div', 'card');
        const body = el('div', 'card-body');

        const header = el(
            'div',
            'd-flex justify-content-between align-items-start gap-2 mb-3'
        );

        const title = el(
            'h3',
            'h6 mb-0',
            getProp(widget, 'title', 'Title') || 'Untitled card'
        );

        const actions = el('div', 'dropdown widget-actions');

        const menuButton = el(
            'button',
            'btn btn-sm btn-light dropdown-toggle'
        );

        menuButton.type = 'button';
        menuButton.dataset.bsToggle = 'dropdown';
        menuButton.setAttribute('aria-label', 'Widget actions');
        menuButton.textContent = '⋯';

        const menu = el('ul', 'dropdown-menu dropdown-menu-end');

        const edit = el('button', 'dropdown-item', 'Edit');
        edit.type = 'button';
        edit.addEventListener('click', () => openWidgetModal(widget));

        const remove = el(
            'button',
            'dropdown-item text-danger',
            'Delete'
        );

        remove.type = 'button';
        remove.addEventListener('click', () => deleteWidget(index));

        const editLi = el('li');
        editLi.append(edit);

        const deleteLi = el('li');
        deleteLi.append(remove);

        menu.append(editLi, deleteLi);
        actions.append(menuButton, menu);

        header.append(title, actions);
        body.append(header);
        card.append(body);
        section.append(card);

        return { section, body };
    }

    function renderKpi(body, widget, rows) {
        const measure = measureOf(widget);
        const key = keyOf(measure);

        const value = rows.length
            ? findValue(rows[0], key)
            : null;

        body.append(
            el('div', 'widget-kpi-value', format(value))
        );
    }

    function renderTable(body, rows) {
        if (!rows.length) {
            body.append(
                el('p', 'text-secondary mb-0', 'No data available.')
            );
            return;
        }

        const table = el(
            'table',
            'table table-sm table-hover mb-0'
        );

        const head = el('thead', 'table-light');
        const headerRow = document.createElement('tr');

        Object.keys(rows[0]).forEach(key => {
            headerRow.append(el('th', '', key));
        });

        head.append(headerRow);
        table.append(head);

        const tableBody = document.createElement('tbody');

        rows.forEach(row => {
            const tr = document.createElement('tr');

            Object.keys(rows[0]).forEach(key => {
                tr.append(el('td', '', format(row[key])));
            });

            tableBody.append(tr);
        });

        table.append(tableBody);

        const wrap = el('div', 'widget-table-container');
        wrap.append(table);
        body.append(wrap);
    }

    function renderChart(body, widget, rows, type) {
        const measure = measureOf(widget);
        const dimension = dimensionOf(widget);

        const measureKey = keyOf(measure);
        const dimensionKey = keyOf(dimension);

        if (!measureKey || !dimensionKey || !rows.length) {
            body.append(
                el(
                    'p',
                    'text-secondary mb-0',
                    rows.length
                        ? 'Chart needs a measure and dimension.'
                        : 'No data available.'
                )
            );
            return;
        }

        const wrap = el('div', 'chart-container');
        const canvas = document.createElement('canvas');

        wrap.append(canvas);
        body.append(wrap);

        const chart = new Chart(canvas, {
            type: type.toLowerCase(),
            data: {
                labels: rows.map(row =>
                    String(findValue(row, dimensionKey) ?? '')
                ),
                datasets: [{
                    label: labelOf(measure),
                    data: rows.map(row =>
                        Number(findValue(row, measureKey) ?? 0)
                    ),
                    borderWidth: 1
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: {
                        display: type === 'PIE'
                    }
                },
                scales: type === 'PIE'
                    ? {}
                    : { y: { beginAtZero: true } }
            }
        });

        chartInstances.push(chart);
    }

    async function renderOne(widget, index) {
        const { section, body } = makeWidgetShell(widget, index);

        widgetsHost.append(section);

        try {
            const result = await request(
                widgetUrl(
                    dashboardSelect.value,
                    getProp(widget, 'widgetKey', 'WidgetKey')
                ),
                { method: 'POST' }
            );

            const rows = getRows(result);

            const type = String(
                getProp(widget, 'widgetType', 'WidgetType') || ''
            ).toUpperCase();

            if (type === 'KPI') {
                renderKpi(body, widget, rows);
            } else if (type === 'TABLE') {
                renderTable(body, rows);
            } else if (['BAR', 'LINE', 'PIE'].includes(type)) {
                renderChart(body, widget, rows, type);
            } else {
                body.append(
                    el('p', 'text-secondary', 'Unsupported widget type: ' + type)
                );
            }
        } catch (error) {
            body.append(
                el('div', 'alert alert-danger small mb-0', error.message)
            );
        }
    }

    // ---------------------------------------------------------
    // Widget configuration
    // ---------------------------------------------------------

    function updateDimensionVisibility() {
        const type = $('widgetTypeSelect').value;

        $('dimensionField').classList.toggle(
            'd-none',
            type === 'KPI'
        );

        $('dimensionSelect').required = type !== 'KPI';
    }

    function addFilterRow(existing = null) {
        const row = el(
            'div',
            'row g-2 align-items-center widget-filter-row'
        );

        const field = el('select', 'form-select');
        const operator = el('select', 'form-select');
        const value = el('input', 'form-control');

        value.placeholder = 'Value';

        dimensions.forEach(item => {
            field.add(
                new Option(
                    item.label || item.Label,
                    item.key || item.Key
                )
            );
        });

        [
            ['eq', 'Equals'],
            ['neq', 'Does not equal'],
            ['gt', 'Greater than'],
            ['gte', 'At least'],
            ['lt', 'Less than'],
            ['lte', 'At most']
        ].forEach(item => {
            operator.add(new Option(item[1], item[0]));
        });

        const remove = el(
            'button',
            'btn btn-outline-secondary',
            'Remove'
        );

        remove.type = 'button';
        remove.addEventListener('click', () => row.remove());

        const a = el('div', 'col-md-4');
        const b = el('div', 'col-md-3');
        const c = el('div', 'col-md-3');
        const d = el('div', 'col-md-2');

        a.append(field);
        b.append(operator);
        c.append(value);
        d.append(remove);

        row.append(a, b, c, d);

        if (existing) {
            field.value =
                existing.dimension ||
                existing.Dimension ||
                existing.columnKey ||
                '';

            operator.value =
                existing.operator ||
                existing.Operator ||
                'eq';

            value.value =
                existing.value ||
                existing.Value ||
                existing.filterValue ||
                '';
        }

        $('widgetFilters').append(row);
    }

    function collectFilters() {
        const filters = [];

        document.querySelectorAll('.widget-filter-row').forEach(row => {
            const value = row.querySelector('input').value.trim();

            if (!value) {
                throw new Error(
                    'Enter a value for each filter or remove the empty filter.'
                );
            }

            const selects = row.querySelectorAll('select');

            filters.push({
                dimension: selects[0].value,
                operator: selects[1].value,
                value
            });
        });

        return filters;
    }

    function queryPayload() {
        const type = $('widgetTypeSelect').value;
        const dimension = $('dimensionSelect').value;
        const measure = $('measureSelect').value;

        if (!measure) {
            throw new Error('Select a measure.');
        }

        if (type !== 'KPI' && !dimension) {
            throw new Error(
                'Select a dimension for this visualization.'
            );
        }

        return {
            dataset: $('datasetSelect').value,
            dimensions: dimension ? [dimension] : [],
            measures: [measure],
            filters: collectFilters(),
            sort: [{
                field: measure,
                direction: 'desc'
            }],
            page: 1,
            pageSize: 100
        };
    }

    async function runPreview() {
        clearError($('widgetModalError'));

        $('previewWidgetButton').disabled = true;
        $('widgetPreviewStatus').textContent = 'Running query...';
        $('widgetPreview').replaceChildren();

        try {
            const result = await executeQuery(queryPayload());

            lastPreviewRows = getRows(result);

            $('widgetPreviewStatus').textContent =
                `${lastPreviewRows.length} preview row(s).`;

            const type = $('widgetTypeSelect').value;

            if (type === 'KPI') {
                const value = lastPreviewRows.length
                    ? findValue(
                        lastPreviewRows[0],
                        $('measureSelect').value
                    )
                    : null;

                $('widgetPreview').append(
                    el('div', 'preview-kpi', format(value))
                );
            } else if (type === 'TABLE') {
                renderTable(
                    $('widgetPreview'),
                    lastPreviewRows
                );
            } else {
                renderChart(
                    $('widgetPreview'),
                    {
                        measures: [{
                            columnKey: $('measureSelect').value,
                            labelOverride:
                                $('measureSelect').selectedOptions[0]?.text
                        }],
                        dimensions: [{
                            columnKey: $('dimensionSelect').value
                        }]
                    },
                    lastPreviewRows,
                    type
                );
            }
        } catch (error) {
            showError(error.message, $('widgetModalError'));
            $('widgetPreviewStatus').textContent = 'Preview failed.';
        } finally {
            $('previewWidgetButton').disabled = false;
        }
    }

    async function openWidgetModal(widget = null) {
        clearError($('widgetModalError'));

        $('widgetForm').reset();
        $('widgetFilters').replaceChildren();

        $('widgetPreview').replaceChildren(
            el('span', 'text-secondary', 'Your preview will appear here.')
        );

        $('widgetPreviewStatus').textContent =
            'Choose fields and run preview.';

        lastPreviewRows = [];

        $('editingWidgetKey').value = widget
            ? getProp(widget, 'widgetKey', 'WidgetKey')
            : '';

        $('widgetModalTitle').textContent = widget
            ? 'Edit dashboard card'
            : 'Add dashboard card';

        $('applyWidgetButton').textContent = widget
            ? 'Apply changes'
            : 'Add to dashboard';

        $('widgetTitleInput').value = widget
            ? getProp(widget, 'title', 'Title')
            : '';

        $('widgetTypeSelect').value = widget
            ? getProp(widget, 'widgetType', 'WidgetType')
            : 'KPI';

        modal.show();

        try {
            await loadDatasets(
                widget
                    ? getProp(widget, 'datasetKey', 'DatasetKey')
                    : null
            );

            const measure = measureOf(widget);
            const dimension = dimensionOf(widget);

            if (measure) {
                $('measureSelect').value = keyOf(measure);
            }

            if (dimension) {
                $('dimensionSelect').value = keyOf(dimension);
            }

            (
                getProp(widget, 'filters', 'Filters') || []
            ).forEach(filter => addFilterRow(filter));

            updateDimensionVisibility();
        } catch (error) {
            showError(error.message, $('widgetModalError'));
        }
    }

    // ---------------------------------------------------------
    // Local widget preview
    // ---------------------------------------------------------

    function localWidgetFromForm() {
        const type = $('widgetTypeSelect').value;
        const measureKey = $('measureSelect').value;
        const dimensionKey = $('dimensionSelect').value;

        const measure = measures.find(
            item => (item.key || item.Key) === measureKey
        );

        const dimension = dimensions.find(
            item => (item.key || item.Key) === dimensionKey
        );

        const filters = collectFilters();

        return {
            widgetId: 0,
            widgetKey:
                $('editingWidgetKey').value || `local_${Date.now()}`,
            title: $('widgetTitleInput').value.trim(),
            widgetType: type,
            datasetKey: $('datasetSelect').value,
            position: widgets.length,
            gridColumnSpan: type === 'KPI' ? 3 : 6,
            gridRowSpan: 1,
            measures: [{
                columnKey: measureKey,
                labelOverride: measure?.label || measure?.Label
            }],
            dimensions: dimensionKey
                ? [{
                    columnKey: dimensionKey,
                    labelOverride: dimension?.label || dimension?.Label
                }]
                : [],
            filters: filters.map((filter, index) => ({
                columnKey: filter.dimension,
                operator: filter.operator,
                filterValue: filter.value,
                position: index
            }))
        };
    }

    function applyLocalWidget() {
        const widget = localWidgetFromForm();
        const key = $('editingWidgetKey').value;

        const index = widgets.findIndex(
            item => getProp(item, 'widgetKey', 'WidgetKey') === key
        );

        if (index >= 0) {
            widgets[index] = widget;
        } else {
            widgets.push(widget);
        }

        modal.hide();

        notice.textContent =
            'This card is currently a local preview. It will not survive a refresh until the SQL persistence endpoints are implemented in Phase 3.';

        notice.classList.remove('d-none');

        renderLocalDashboard();
    }

    async function renderLocalDashboard() {
        widgetsHost.replaceChildren();
        destroyCharts();
        widgetsHost.classList.remove('d-none');

        for (let i = 0; i < widgets.length; i++) {
            const widget = widgets[i];

            if (
                String(
                    getProp(widget, 'widgetKey', 'WidgetKey')
                ).startsWith('local_')
            ) {
                const { section, body } = makeWidgetShell(widget, i);

                widgetsHost.append(section);

                const payload = {
                    dataset: widget.datasetKey,
                    dimensions: widget.dimensions.map(keyOf),
                    measures: widget.measures.map(keyOf),
                    filters: widget.filters.map(filter => ({
                        dimension: filter.columnKey,
                        operator: filter.operator,
                        value: filter.filterValue
                    })),
                    sort: [{
                        field: keyOf(widget.measures[0]),
                        direction: 'desc'
                    }],
                    page: 1,
                    pageSize: 100
                };

                try {
                    const result = await executeQuery(payload);
                    const rows = getRows(result);
                    const type = widget.widgetType;

                    if (type === 'KPI') {
                        renderKpi(body, widget, rows);
                    } else if (type === 'TABLE') {
                        renderTable(body, rows);
                    } else {
                        renderChart(body, widget, rows, type);
                    }
                } catch (error) {
                    body.append(
                        el('div', 'alert alert-danger small', error.message)
                    );
                }
            } else {
                await renderOne(widget, i);
            }
        }

        wireDragAndDrop();
    }

    function deleteWidget(index) {
        if (!confirm('Remove this card from the current preview?')) {
            return;
        }

        widgets.splice(index, 1);

        notice.textContent =
            'Card removed from this page preview. SQL deletion will be enabled in Phase 3.';

        notice.classList.remove('d-none');

        renderLocalDashboard();
    }

    // ---------------------------------------------------------
    // Drag and drop
    // ---------------------------------------------------------

    function wireDragAndDrop() {
        const nodes = widgetsHost.querySelectorAll('.dashboard-widget');

        nodes.forEach(node => {
            node.draggable = editMode;

            if (!editMode) {
                node.classList.remove(
                    'widget-draggable',
                    'widget-dragging'
                );
                return;
            }

            node.classList.add('widget-draggable');

            // Prevent duplicate event handlers on the same element.
            if (node.dataset.dragHandlersAttached === 'true') {
                return;
            }

            node.dataset.dragHandlersAttached = 'true';

            node.addEventListener('dragstart', event => {
                node.classList.add('widget-dragging');

                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData(
                    'text/plain',
                    node.dataset.index
                );
            });

            node.addEventListener('dragend', () => {
                node.classList.remove('widget-dragging');
            });

            node.addEventListener('dragover', event => {
                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
            });

            node.addEventListener('drop', event => {
                event.preventDefault();
                event.stopPropagation();

                const from = Number(
                    event.dataTransfer.getData('text/plain')
                );

                const to = Number(node.dataset.index);

                if (
                    !Number.isInteger(from) ||
                    !Number.isInteger(to) ||
                    from === to ||
                    from < 0 ||
                    to < 0 ||
                    from >= widgets.length ||
                    to >= widgets.length
                ) {
                    return;
                }

                const [moved] = widgets.splice(from, 1);
                widgets.splice(to, 0, moved);

                widgets.forEach((widget, index) => {
                    widget.position = index;
                });

                renderLocalDashboard();
            });
        });
    }

    // ---------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------

    $('addCardButton').addEventListener('click', () => {
        openWidgetModal();
    });

    $('editLayoutButton').addEventListener('click', () => {
        editMode = !editMode;

        $('editLayoutButton').textContent =
            editMode ? 'Done editing' : 'Edit layout';

        widgetsHost.classList.toggle(
            'dashboard-edit-mode',
            editMode
        );

        wireDragAndDrop();
    });

    $('widgetTypeSelect').addEventListener(
        'change',
        updateDimensionVisibility
    );

    $('datasetSelect').addEventListener('change', async () => {
        clearError($('widgetModalError'));
        $('widgetFilters').replaceChildren();
        lastPreviewRows = [];

        try {
            await loadMetadata();
        } catch (error) {
            showError(error.message, $('widgetModalError'));
        }
    });

    $('addWidgetFilter').addEventListener('click', () => {
        addFilterRow();
    });

    $('previewWidgetButton').addEventListener(
        'click',
        runPreview
    );

    $('widgetForm').addEventListener('submit', event => {
        event.preventDefault();

        clearError($('widgetModalError'));

        if (!$('widgetTitleInput').value.trim()) {
            showError(
                'Enter a card title.',
                $('widgetModalError')
            );
            return;
        }

        if (!lastPreviewRows.length) {
            showError(
                'Run a successful preview before adding the card.',
                $('widgetModalError')
            );
            return;
        }

        try {
            applyLocalWidget();
        } catch (error) {
            showError(error.message, $('widgetModalError'));
        }
    });

    dashboardSelect.addEventListener('change', loadDashboard);

    departmentSelect.addEventListener('change', async () => {
        clearError();

        try {
            await saveDepartment();
            await loadDashboards();
            await loadDashboard();
        } catch (error) {
            loading.classList.add('d-none');
            showError(error.message);
        }
    });

    // ---------------------------------------------------------
    // Initial load
    // ---------------------------------------------------------

    (async () => {
        try {
            await saveDepartment();
            await loadDashboards();
            await loadDashboard();
        } catch (error) {
            loading.classList.add('d-none');
            showError(error.message);
        }
    })();

})();
