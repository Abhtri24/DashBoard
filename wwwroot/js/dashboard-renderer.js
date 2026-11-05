// dashboard-renderer.js
// Rendering and drag/drop behaviour. State and callbacks are injected by dashboard.js.

export function createRenderer(ctx) {
    const {
        widgetsHost, getWidgets, getDepartment, getDashboardKey,
        getEditMode, requestWidget, openWidgetModal, deleteWidget,
        getUIConfiguration, renderAfterReorder, onDrilldown
    } = ctx;

    let chartInstances = [];

    const getProp = (obj, lower, upper) => obj?.[lower] ?? obj?.[upper];
    const keyOf = item => item?.columnKey || item?.ColumnKey || '';
    const labelOf = item => item?.labelOverride || item?.LabelOverride ||
        item?.label || item?.Label || keyOf(item);
    const measureOf = widget => getProp(widget, 'measures', 'Measures')?.[0] || null;
    const dimensionOf = widget => getProp(widget, 'dimensions', 'Dimensions')?.[0] || null;

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function findValue(row, key) {
        const actual = Object.keys(row || {}).find(
            name => name.toLowerCase() === String(key || '').toLowerCase()
        );
        return actual ? row[actual] : undefined;
    }

    function format(value) {
        if (value == null) return '—';
        return typeof value === 'number'
            ? new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2 }).format(value)
            : String(value);
    }

    function destroyCharts() {
        chartInstances.forEach(chart => chart.destroy());
        chartInstances = [];
    }

    function setGridSpan(node, widget) {
        const col = Number(
            getProp(widget, 'gridColumnSpan', 'GridColumnSpan') ||
            (String(getProp(widget, 'widgetType', 'WidgetType')).toUpperCase() === 'KPI' ? 3 : 6)
        );
        const row = Number(getProp(widget, 'gridRowSpan', 'GridRowSpan') || 1);
        node.style.gridColumn = `span ${Math.max(1, Math.min(12, col))}`;
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
        const menuButton = el('button', 'btn btn-sm btn-light dropdown-toggle');
        menuButton.type = 'button';
        menuButton.dataset.bsToggle = 'dropdown';
        menuButton.setAttribute('aria-label', 'Widget actions');
        menuButton.textContent = '⋯';

        const menu = el('ul', 'dropdown-menu dropdown-menu-end');
        const edit = el('button', 'dropdown-item', 'Edit');
        edit.type = 'button';
        edit.addEventListener('click', () => openWidgetModal(widget));

        const remove = el('button', 'dropdown-item text-danger', 'Delete');
        remove.type = 'button';
        remove.addEventListener('click', () => deleteWidget(index));

        const editLi = el('li');
        const deleteLi = el('li');
        editLi.append(edit);
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
        const key = keyOf(measureOf(widget));
        const value = rows.length ? findValue(rows[0], key) : null;
        body.append(el('div', 'widget-kpi-value', format(value)));
    }

    function renderTable(body, rows) {
        if (!rows.length) {
            body.append(el('p', 'text-secondary mb-0', 'No data available.'));
            return;
        }

        const table = el('table', 'table table-sm table-hover mb-0');
        const head = el('thead', 'table-light');
        const headerRow = document.createElement('tr');

        Object.keys(rows[0]).forEach(key => headerRow.append(el('th', '', key)));
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
            const labels = getUIConfiguration();

            body.append(el(
                'p',
                'text-secondary mb-0',
                rows.length
                    ? `Chart needs a ${labels.measureLabel.toLowerCase()} and ${labels.dimensionLabel.toLowerCase()}.`
                    : 'No data available.'
            ));
            return;
        }

        const isPie = type === 'PIE';

        // Chart wrapper
        const wrap = el('div', 'chart-container');

        // Toolbar
        const toolbar = el(
            'div',
            'chart-toolbar d-flex justify-content-end align-items-center gap-2 mb-2'
        );

        function createButton(label, title) {
            const button = el(
                'button',
                'btn btn-sm btn-outline-secondary',
                label
            );

            button.type = 'button';
            button.title = title;
            button.setAttribute('aria-label', title);

            return button;
        }

        const zoomIn = createButton('+', 'Zoom in');
        const zoomOut = createButton('−', 'Zoom out');
        const resetZoom = createButton('Reset', 'Reset zoom');
        const fullscreen = createButton('⛶', 'Full screen');

        if (isPie) {
            zoomIn.disabled = true;
            zoomOut.disabled = true;
            resetZoom.disabled = true;
        }

        toolbar.append(zoomIn, zoomOut, resetZoom, fullscreen);

        // Canvas area
        const chartArea = el('div', 'chart-canvas-container');
        const canvas = document.createElement('canvas');

        chartArea.append(canvas);
        wrap.append(toolbar, chartArea);
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
                        display: isPie
                    },

                    zoom: {
                        zoom: {
                            wheel: {
                                enabled: !isPie,
                                modifierKey: 'ctrl'
                            },
                            pinch: {
                                enabled: !isPie
                            },
                            drag: {
                                enabled: false
                            },
                            mode: 'xy'
                        },

                        pan: {
                            enabled: !isPie,
                            mode: 'xy',
                            modifierKey: 'shift'
                        }
                    }
                },

                scales: isPie
                    ? {}
                    : {
                        x: {
                            ticks: {
                                autoSkip: true,
                                maxRotation: 45,
                                minRotation: 0
                            }
                        },
                        y: {
                            beginAtZero: true
                        }
                    },

                onClick: (event, elements, chart) => {
                    if (getEditMode() || !elements.length) return;

                    const index = elements[0].index;
                    const selectedValue = chart.data.labels[index];
                    const clickedDimension = keyOf(dimensionOf(widget));

                    if (!clickedDimension || selectedValue == null) return;

                    onDrilldown(
                        widget,
                        clickedDimension,
                        String(selectedValue)
                    );
                },

                onHover: (event, elements) => {
                    if (event.native?.target) {
                        event.native.target.style.cursor =
                            elements.length && !getEditMode()
                                ? 'pointer'
                                : 'default';
                    }
                }
            }
        });

        // Zoom controls
        zoomIn.addEventListener('click', () => {
            if (!isPie) chart.zoom(1.2);
        });

        zoomOut.addEventListener('click', () => {
            if (!isPie) chart.zoom(0.8);
        });

        resetZoom.addEventListener('click', () => {
            if (!isPie) chart.resetZoom();
        });

        // Full-screen control
        fullscreen.addEventListener('click', async () => {
            try {
                if (document.fullscreenElement === wrap) {
                    await document.exitFullscreen();
                } else {
                    await wrap.requestFullscreen();
                }
            } catch (error) {
                console.error('Unable to enter full-screen mode:', error);
            }
        });

        // Keep the button label in sync, including when Esc is pressed.
        wrap.addEventListener('fullscreenchange', () => {
            const isFullscreen = document.fullscreenElement === wrap;

            fullscreen.textContent = isFullscreen ? '⛶ Exit' : '⛶';
            fullscreen.title = isFullscreen
                ? 'Exit full screen'
                : 'Full screen';

            requestAnimationFrame(() => chart.resize());
        });

        chartInstances.push(chart);
    }



    function renderRows(body, widget, result) {
        const rows = Array.isArray(result)
            ? result
            : (result?.value || result?.Value || result?.rows || result?.Rows || []);
        const type = String(
            getProp(widget, 'widgetType', 'WidgetType') || ''
        ).toUpperCase();

        if (type === 'KPI') renderKpi(body, widget, rows);
        else if (type === 'TABLE') renderTable(body, rows);
        else if (['BAR', 'LINE', 'PIE'].includes(type)) renderChart(body, widget, rows, type);
        else body.append(el('p', 'text-secondary', 'Unsupported widget type: ' + type));
    }

    async function renderOne(widget, index) {
        const { section, body } = makeWidgetShell(widget, index);
        widgetsHost.append(section);

        try {
            const result = await requestWidget(
                getDepartment(),
                getDashboardKey(),
                getProp(widget, 'widgetKey', 'WidgetKey')
            );
            renderRows(body, widget, result);
        } catch (error) {
            body.append(el('div', 'alert alert-danger small mb-0', error.message));
        }
    }

    async function renderDashboard() {
        widgetsHost.replaceChildren();
        destroyCharts();
        const widgets = getWidgets();

        if (!widgets.length) {
            widgetsHost.append(
                el('p', 'text-secondary', 'No cards are configured. Use Add card to create one.')
            );
            return;
        }

        for (let i = 0; i < widgets.length; i++) {
            await renderOne(widgets[i], i);
        }
        wireDragAndDrop();
    }

    function wireDragAndDrop() {
        const widgets = getWidgets();
        const nodes = widgetsHost.querySelectorAll('.dashboard-widget');

        nodes.forEach(node => {
            node.draggable = getEditMode();

            if (!getEditMode()) {
                node.classList.remove('widget-draggable', 'widget-dragging');
                return;
            }

            node.classList.add('widget-draggable');
            if (node.dataset.dragHandlersAttached === 'true') return;
            node.dataset.dragHandlersAttached = 'true';

            node.addEventListener('dragstart', event => {
                node.classList.add('widget-dragging');
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', node.dataset.index);
            });

            node.addEventListener('dragend', () => {
                node.classList.remove('widget-dragging');
            });

            node.addEventListener('dragover', event => {
                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
            });

            node.addEventListener('drop', async event => {
                event.preventDefault();
                event.stopPropagation();

                const from = Number(event.dataTransfer.getData('text/plain'));
                const to = Number(node.dataset.index);

                if (!Number.isInteger(from) || !Number.isInteger(to) ||
                    from === to || from < 0 || to < 0 ||
                    from >= widgets.length || to >= widgets.length) return;

                const [moved] = widgets.splice(from, 1);
                widgets.splice(to, 0, moved);
                widgets.forEach((widget, index) => { widget.position = index; });
                await renderAfterReorder();
            });
        });
    }

    return {
        destroyCharts,
        makeWidgetShell,
        renderRows,
        renderOne,
        renderDashboard,
        wireDragAndDrop
    };
}

