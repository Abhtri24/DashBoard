// widget-builder.js
// Add/edit card modal, metadata selectors, filters, validation and local preview.

export function createWidgetBuilder(ctx) {
    const {
        $, modal, requestDatasets, requestMetadata, executeQuery,
        getDepartment, getWidgets, setWidgets, renderer, showError, clearError,
        getUIConfiguration, notice
    } = ctx;

    let dimensions = [];
    let measures = [];

    const getProp = (obj, lower, upper) => obj?.[lower] ?? obj?.[upper];
    const keyOf = item => item?.columnKey || item?.ColumnKey || '';

    function updateDimensionVisibility() {
        const type = $('widgetTypeSelect').value;
        $('dimensionField').classList.toggle('d-none', type === 'KPI');
        $('dimensionSelect').required = type !== 'KPI';
    }

    async function loadMetadata() {
        const dataset = $('datasetSelect').value;
        if (!dataset) throw new Error('Select a dataset.');

        const metadata = await requestMetadata(getDepartment(), dataset);
        dimensions = metadata.dimensions;
        measures = metadata.measures;

        const dimensionSelect = $('dimensionSelect');
        const measureSelect = $('measureSelect');

        dimensionSelect.replaceChildren(new Option('(No grouping)', ''));
        measureSelect.replaceChildren();

        dimensions.forEach(item => {
            dimensionSelect.add(new Option(item.label || item.Label, item.key || item.Key));
        });

        measures.forEach(item => {
            measureSelect.add(new Option(item.label || item.Label, item.key || item.Key));
        });

        updateDimensionVisibility();
    }

    async function loadDatasets(selectedDataset = null) {
        const datasets = await requestDatasets(getDepartment());
        const select = $('datasetSelect');
        select.replaceChildren();

        if (!datasets.length) {
            dimensions = [];
            measures = [];
            throw new Error(`No datasets are available for ${getDepartment()}.`);
        }

        datasets.forEach(dataset => select.add(new Option(dataset.label, dataset.key)));

        const matching = datasets.find(item =>
            String(item.key).toLowerCase() === String(selectedDataset || '').toLowerCase()
        );
        select.value = matching ? matching.key : datasets[0].key;
        await loadMetadata();
    }

    function getColumnType(columnKey) {
        const column = [...dimensions, ...measures].find(item =>
            String(item.key || item.Key || keyOf(item)).toLowerCase() === String(columnKey).toLowerCase()
        );
        return String(getProp(column, 'type', 'Type') || 'string').toLowerCase();
    }

    function getFilterType(dataType) {
        const type = String(dataType || '').toLowerCase();

        if (/date|time/.test(type)) return 'date';
        if (/int|decimal|numeric|number|float|real|money/.test(type)) return 'number';
        return 'text';
    }

    function getFilterOperators(dataType) {
        const inputType = getFilterType(dataType);
        const common = [
            ['eq', 'Equals'],
            ['neq', 'Does not equal'],
            ['in', 'Is one of']
        ];

        if (inputType === 'text') {
            return [
                ...common,
                ['contains', 'Contains'],
                ['startsWith', 'Starts with'],
                ['endsWith', 'Ends with'],
                ['isNull', 'Is empty'],
                ['isNotNull', 'Is not empty']
            ];
        }

        return [
            ...common,
            ['gt', 'Greater than'],
            ['gte', 'At least'],
            ['lt', 'Less than'],
            ['lte', 'At most'],
            ['isNull', 'Is empty'],
            ['isNotNull', 'Is not empty']
        ];
    }

    function updateFilterControls(row, preferredOperator = null) {
        const field = row.querySelector('.filter-field');
        const operator = row.querySelector('.filter-operator');
        const value = row.querySelector('.filter-value');
        const inputType = getFilterType(getColumnType(field.value));
        const previousValue = value.value;
        const previousOperator = preferredOperator || operator.value;

        operator.replaceChildren(
            ...getFilterOperators(getColumnType(field.value)).map(
                ([key, label]) => new Option(label, key)
            )
        );

        const validOperator = Array.from(operator.options)
            .some(option => option.value === previousOperator);
        operator.value = validOperator ? previousOperator : 'eq';

        value.type = inputType === 'date' ? 'date' :
            inputType === 'number' ? 'number' : 'text';
        value.step = inputType === 'number' ? 'any' : '';
        value.placeholder = inputType === 'number' ? 'Enter a number' :
            inputType === 'date' ? 'Select a date' : 'Enter a value';

        const noValueRequired = ['isNull', 'isNotNull'].includes(operator.value);
        value.disabled = noValueRequired;
        value.required = !noValueRequired;
        value.classList.toggle('d-none', noValueRequired);
        if (!noValueRequired) value.value = previousValue;
    }

    function addFilterRow(existing = null) {
        const row = document.createElement('div');
        row.className = 'row g-2 align-items-center widget-filter-row';

        const field = document.createElement('select');
        field.className = 'form-select filter-field';

        const operator = document.createElement('select');
        operator.className = 'form-select filter-operator';

        const value = document.createElement('input');
        value.className = 'form-control filter-value';
        value.placeholder = 'Value';

        [...dimensions, ...measures].forEach(item => {
            field.add(new Option(item.label || item.Label, item.key || item.Key));
        });

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'btn btn-outline-secondary';
        remove.textContent = 'Remove';
        remove.addEventListener('click', () => row.remove());

        const columns = ['col-md-4', 'col-md-3', 'col-md-3', 'col-md-2'];
        [field, operator, value, remove].forEach((control, index) => {
            const column = document.createElement('div');
            column.className = columns[index];
            column.append(control);
            row.append(column);
        });

        field.addEventListener('change', () => updateFilterControls(row));
        operator.addEventListener('change', () => updateFilterControls(row));

        if (existing) {
            field.value = existing.dimension || existing.Dimension ||
                existing.columnKey || '';
        }

        updateFilterControls(
            row,
            existing?.operator || existing?.Operator || 'eq'
        );

        if (existing) {
            value.value = existing.value ?? existing.Value ??
                existing.filterValue ?? '';
        }

        $('widgetFilters').append(row);
    }

    function collectFilters() {
        const filters = [];

        document.querySelectorAll('.widget-filter-row').forEach(row => {
            const field = row.querySelector('.filter-field');
            const operator = row.querySelector('.filter-operator');
            const value = row.querySelector('.filter-value');
            const noValueRequired = ['isNull', 'isNotNull'].includes(operator.value);

            if (!noValueRequired && !value.value.trim()) {
                throw new Error('Enter a value for each filter or remove the empty filter.');
            }

            filters.push({
                dimension: field.value,
                operator: operator.value,
                value: noValueRequired ? '' : value.value.trim()
            });
        });

        return filters;
    }

    function queryPayload() {
        const type = $('widgetTypeSelect').value;
        const dimension = $('dimensionSelect').value;
        const measure = $('measureSelect').value;
        const labels = getUIConfiguration();

        if (!measure) {
            throw new Error(`Select a ${labels.measureLabel.toLowerCase()}.`);
        }

        if (type !== 'KPI' && !dimension) {
            throw new Error(
                `Select a ${labels.dimensionLabel.toLowerCase()} for this visualization.`
            );
        }

        return {
            dataset: $('datasetSelect').value,
            dimensions: dimension ? [dimension] : [],
            measures: [measure],
            filters: collectFilters(),
            sort: [{ field: measure, direction: 'desc' }],
            page: 1,
            pageSize: 100
        };
    }

    async function openWidgetModal(widget = null) {
        clearError($('widgetModalError'));
        $('widgetForm').reset();
        $('widgetFilters').replaceChildren();

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
                widget ? getProp(widget, 'datasetKey', 'DatasetKey') : null
            );

            const measure = getProp(widget, 'measures', 'Measures')?.[0];
            const dimension = getProp(widget, 'dimensions', 'Dimensions')?.[0];

            if (measure) $('measureSelect').value = keyOf(measure);
            if (dimension) $('dimensionSelect').value = keyOf(dimension);

            (getProp(widget, 'filters', 'Filters') || [])
                .forEach(filter => addFilterRow(filter));

            updateDimensionVisibility();
        } catch (error) {
            showError(error.message, $('widgetModalError'));
        }
    }

    function localWidgetFromForm() {
        const type = $('widgetTypeSelect').value;
        const measureKey = $('measureSelect').value;
        const dimensionKey = $('dimensionSelect').value;

        const measure = measures.find(x => (x.key || x.Key) === measureKey);
        const dimension = dimensions.find(x => (x.key || x.Key) === dimensionKey);
        const filters = collectFilters();
        const widgets = getWidgets();

        return {
            widgetId: 0,
            widgetKey: $('editingWidgetKey').value || `local_${Date.now()}`,
            isLocalPreview: true,
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
            dimensions: dimensionKey ? [{
                columnKey: dimensionKey,
                labelOverride: dimension?.label || dimension?.Label
            }] : [],
            filters: filters.map((filter, index) => ({
                columnKey: filter.dimension,
                operator: filter.operator,
                filterValue: filter.value,
                position: index
            }))
        };
    }

    async function renderLocalDashboard() {
        const widgets = getWidgets();
        const host = $('dashboardWidgets');
        host.replaceChildren();
        renderer.destroyCharts();
        host.classList.remove('d-none');

        for (let index = 0; index < widgets.length; index++) {
            const widget = widgets[index];

            if (!getProp(widget, 'isLocalPreview', 'IsLocalPreview')) {
                await renderer.renderOne(widget, index);
                continue;
            }

            const { section, body } = renderer.makeWidgetShell(widget, index);
            host.append(section);

            const payload = {
                dataset: getProp(widget, 'datasetKey', 'DatasetKey'),
                dimensions: (getProp(widget, 'dimensions', 'Dimensions') || []).map(keyOf),
                measures: (getProp(widget, 'measures', 'Measures') || []).map(keyOf),
                filters: (getProp(widget, 'filters', 'Filters') || []).map(filter => ({
                    dimension: getProp(filter, 'columnKey', 'ColumnKey'),
                    operator: getProp(filter, 'operator', 'Operator'),
                    value: getProp(filter, 'filterValue', 'FilterValue')
                })),
                sort: [{
                    field: keyOf(getProp(widget, 'measures', 'Measures')?.[0]),
                    direction: 'desc'
                }],
                page: 1,
                pageSize: 100
            };

            try {
                const result = await executeQuery(getDepartment(), payload);
                renderer.renderRows(body, widget, result);
            } catch (error) {
                const alert = document.createElement('div');
                alert.className = 'alert alert-danger small';
                alert.textContent = error.message;
                body.append(alert);
            }
        }

        renderer.wireDragAndDrop();
    }

    function applyLocalWidget() {
        const widget = localWidgetFromForm();
        const key = $('editingWidgetKey').value;
        const widgets = getWidgets();

        const index = widgets.findIndex(item =>
            getProp(item, 'widgetKey', 'WidgetKey') === key
        );

        if (index >= 0) widgets[index] = widget;
        else widgets.push(widget);

        setWidgets(widgets);
        modal.hide();

        notice.textContent =
            'Unsaved changes are stored temporarily in this browser tab. Click Save Layout to keep them for future sessions.';
        notice.classList.remove('d-none');

        renderLocalDashboard();
    }

    function deleteWidget(index) {
        if (!confirm('Remove this card from the current preview?')) return;

        const widgets = getWidgets();
        widgets.splice(index, 1);
        setWidgets(widgets);

        notice.textContent =
            'Card removed from this page preview. SQL deletion will be enabled in Phase 3.';
        notice.classList.remove('d-none');

        renderLocalDashboard();
    }

    function bindEvents() {
        $('addCardButton').addEventListener('click', () => openWidgetModal());

        $('widgetTypeSelect').addEventListener('change', updateDimensionVisibility);

        $('datasetSelect').addEventListener('change', async () => {
            clearError($('widgetModalError'));
            $('widgetFilters').replaceChildren();

            try {
                await loadMetadata();
            } catch (error) {
                showError(error.message, $('widgetModalError'));
            }
        });

        $('addWidgetFilter').addEventListener('click', () => addFilterRow());

        $('widgetForm').addEventListener('submit', event => {
            event.preventDefault();
            clearError($('widgetModalError'));

            if (!$('widgetTitleInput').value.trim()) {
                showError('Enter a card title.', $('widgetModalError'));
                return;
            }

            try {
                queryPayload();
                applyLocalWidget();
            } catch (error) {
                showError(error.message, $('widgetModalError'));
            }
        });
    }

    return {
        bindEvents,
        openWidgetModal,
        loadDatasets,
        loadMetadata,
        queryPayload,
        deleteWidget,
        renderLocalDashboard
    };
}
