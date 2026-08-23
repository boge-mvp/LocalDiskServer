// availableShells is injected globally in footer.html
if (typeof window.t !== 'function') {
    window.t = function(key, ...args) {
        let str = (window.I18N_DICT && window.I18N_DICT[key]) || key;
        if (args.length > 0) {
            args.forEach((val, idx) => {
                str = str.replace(new RegExp('\\{' + idx + '\\}', 'g'), val);
            });
        }
        return str;
    };
}

// Table Column Sorting State
let currentSortCol = null;
let currentSortDir = 'none'; // 'asc' | 'desc' | 'none'

// Progressive Incremental Rendering & Status Bar State
let allDataSourceRows = [];
let renderedRowCount = 0;
const BATCH_SIZE = 100;
let isIncrementalRenderActive = false;

function initIncrementalRender() {
    const table = document.getElementById('file-table');
    if (!table) {
        updateStatusBar();
        return;
    }
    const tbody = table.querySelector('tbody');
    if (!tbody) {
        updateStatusBar();
        return;
    }

    allDataSourceRows = Array.from(tbody.querySelectorAll('tr.item-row'));
    if (allDataSourceRows.length === 0) {
        updateStatusBar();
        return;
    }

    // 当列表条目较多时启用动态分批渲染
    if (allDataSourceRows.length > BATCH_SIZE) {
        isIncrementalRenderActive = true;
        tbody.innerHTML = '';
        renderedRowCount = 0;
        appendNextBatch(BATCH_SIZE);

        const scrollArea = document.querySelector('.explorer-scroll-area');
        if (scrollArea) {
            scrollArea.addEventListener('scroll', () => {
                if (!isIncrementalRenderActive) return;
                const scrollBottom = scrollArea.scrollHeight - scrollArea.scrollTop - scrollArea.clientHeight;
                if (scrollBottom < 300) {
                    appendNextBatch(BATCH_SIZE);
                }
            });
        }
    }

    updateStatusBar();
}

function appendNextBatch(count) {
    const table = document.getElementById('file-table');
    if (!table) return;
    const tbody = table.querySelector('tbody');
    if (!tbody) return;

    const searchInput = document.getElementById('search');
    const filter = searchInput ? searchInput.value.toLowerCase().trim() : '';

    const currentList = filter
        ? allDataSourceRows.filter(r => (r.getAttribute('data-name') || '').indexOf(filter) > -1)
        : allDataSourceRows;

    if (renderedRowCount >= currentList.length) return;

    const frag = document.createDocumentFragment();
    const nextLimit = Math.min(renderedRowCount + count, currentList.length);

    for (let i = renderedRowCount; i < nextLimit; i++) {
        frag.appendChild(currentList[i]);
    }

    tbody.appendChild(frag);
    renderedRowCount = nextLimit;
}

function handleHeaderSort(colKey) {
    const table = document.getElementById('file-table');
    if (!table) return;
    const tbody = table.querySelector('tbody');
    if (!tbody) return;

    // Toggle sort direction: none -> asc -> desc -> none
    if (currentSortCol === colKey) {
        if (currentSortDir === 'asc') {
            currentSortDir = 'desc';
        } else if (currentSortDir === 'desc') {
            currentSortDir = 'none';
            currentSortCol = null;
        } else {
            currentSortDir = 'asc';
        }
    } else {
        currentSortCol = colKey;
        currentSortDir = 'asc';
    }

    const rows = (allDataSourceRows && allDataSourceRows.length > 0)
        ? allDataSourceRows
        : Array.from(tbody.querySelectorAll('tr.item-row'));

    if (rows.length === 0) return;

    if (currentSortDir === 'none') {
        // Reset to default original order
        rows.sort((a, b) => {
            const idxA = parseInt(a.getAttribute('data-original-index') || '0', 10);
            const idxB = parseInt(b.getAttribute('data-original-index') || '0', 10);
            return idxA - idxB;
        });
    } else {
        const mult = currentSortDir === 'asc' ? 1 : -1;
        const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

        rows.sort((a, b) => {
            const typeA = a.getAttribute('data-type') || 'file';
            const typeB = b.getAttribute('data-type') || 'file';

            // Always keep directories above files (Windows Explorer style)
            if (typeA !== typeB) {
                return typeA === 'dir' ? -1 : 1;
            }

            if (colKey === 'name') {
                const nameA = (a.querySelector('.name-text')?.innerText || a.getAttribute('data-name') || '').trim();
                const nameB = (b.querySelector('.name-text')?.innerText || b.getAttribute('data-name') || '').trim();
                return mult * collator.compare(nameA, nameB);
            } else if (colKey === 'type') {
                const descA = (a.getAttribute('data-type-desc') || '').trim();
                const descB = (b.getAttribute('data-type-desc') || '').trim();
                return mult * collator.compare(descA, descB);
            } else if (colKey === 'time') {
                const timeA = parseFloat(a.getAttribute('data-time') || '0');
                const timeB = parseFloat(b.getAttribute('data-time') || '0');
                return mult * (timeA - timeB);
            } else if (colKey === 'size') {
                const sizeA = parseFloat(a.getAttribute('data-size') || '0');
                const sizeB = parseFloat(b.getAttribute('data-size') || '0');
                return mult * (sizeA - sizeB);
            } else if (colKey === 'favorite') {
                const favA = a.getAttribute('data-favorite') === 'true' ? 1 : 0;
                const favB = b.getAttribute('data-favorite') === 'true' ? 1 : 0;
                return mult * (favA - favB);
            }
            return 0;
        });
    }

    if (isIncrementalRenderActive) {
        tbody.innerHTML = '';
        renderedRowCount = 0;
        appendNextBatch(BATCH_SIZE);
    } else {
        rows.forEach(r => tbody.appendChild(r));
    }

    // Update Header Indicators
    const headers = table.querySelectorAll('th.col-sortable');
    headers.forEach(th => {
        const arrow = th.querySelector('.sort-arrow');
        const col = th.getAttribute('data-col');
        if (col === currentSortCol && currentSortDir !== 'none') {
            if (arrow) arrow.textContent = currentSortDir === 'asc' ? '▲' : '▼';
            th.style.color = 'var(--accent-hover)';
        } else {
            if (arrow) arrow.textContent = '';
            th.style.color = '';
        }
    });

    updateStatusBar();
}

// Reset sorting to default programmatically
function resetTableSort() {
    currentSortCol = null;
    currentSortDir = 'none';
    const table = document.getElementById('file-table');
    if (!table) return;
    const tbody = table.querySelector('tbody');
    if (!tbody) return;

    const rows = (allDataSourceRows && allDataSourceRows.length > 0)
        ? allDataSourceRows
        : Array.from(tbody.querySelectorAll('tr.item-row'));

    rows.sort((a, b) => {
        const idxA = parseInt(a.getAttribute('data-original-index') || '0', 10);
        const idxB = parseInt(b.getAttribute('data-original-index') || '0', 10);
        return idxA - idxB;
    });

    if (isIncrementalRenderActive) {
        tbody.innerHTML = '';
        renderedRowCount = 0;
        appendNextBatch(BATCH_SIZE);
    } else {
        rows.forEach(r => tbody.appendChild(r));
    }

    const headers = table.querySelectorAll('th.col-sortable');
    headers.forEach(th => {
        const arrow = th.querySelector('.sort-arrow');
        if (arrow) arrow.textContent = '';
        th.style.color = '';
    });

    updateStatusBar();
}

// Table Column Resizing Logic: Adjacent Column Pair Only, Zero Interference to Other Columns
let isColResizing = false;
let currentLeftTh = null;
let currentRightTh = null;
let startResizeX = 0;
let startLeftWidth = 0;
let startRightWidth = 0;
let minLeftWidth = 40;
let minRightWidth = 40;
let totalPairWidth = 0;
let resizeRafId = null;

function getThMinWidth(th) {
    if (!th) return 40;
    const labelEl = th.querySelector('.th-label');
    const labelWidth = labelEl ? Math.ceil(labelEl.getBoundingClientRect().width) : 26;
    const arrowEl = th.querySelector('.sort-arrow');
    const arrowWidth = arrowEl ? Math.ceil(arrowEl.getBoundingClientRect().width || 12) : 12;
    // th 内边距 (左右各 8px = 16px) + 手柄与排序箭头间距余量 (8px)
    return Math.max(45, labelWidth + arrowWidth + 20);
}

function initColResize(e, handle) {
    if (e.button !== 0) return; // 仅限鼠标左键拖拽
    e.preventDefault();
    e.stopPropagation();
    
    const leftTh = handle.parentElement;
    const rightTh = leftTh ? leftTh.nextElementSibling : null;
    if (!leftTh || !rightTh) return; // 最后一列右侧无相邻列，不进行调节

    isColResizing = true;
    currentLeftTh = leftTh;
    currentRightTh = rightTh;
    startResizeX = e.pageX;
    
    // 获取当前相邻两列的精确物理像素宽度
    startLeftWidth = currentLeftTh.getBoundingClientRect().width;
    startRightWidth = currentRightTh.getBoundingClientRect().width;
    totalPairWidth = startLeftWidth + startRightWidth;

    // 分别计算左列与右列的最小安全宽度（以表头文字为基准防截断）
    minLeftWidth = getThMinWidth(currentLeftTh);
    minRightWidth = getThMinWidth(currentRightTh);

    handle.classList.add('resizing');
    document.body.classList.add('resizing-col');

    function onMouseMove(moveEvent) {
        if (!isColResizing || !currentLeftTh || !currentRightTh) return;
        const diff = moveEvent.pageX - startResizeX;
        
        // 仅在当前相邻两列之间分配宽度，左列 + diff，右列相应 - diff
        let newLeftWidth = startLeftWidth + diff;
        if (newLeftWidth < minLeftWidth) {
            newLeftWidth = minLeftWidth;
        } else if (newLeftWidth > totalPairWidth - minRightWidth) {
            newLeftWidth = totalPairWidth - minRightWidth;
        }
        
        const newRightWidth = totalPairWidth - newLeftWidth;

        if (resizeRafId) cancelAnimationFrame(resizeRafId);
        resizeRafId = requestAnimationFrame(() => {
            if (currentLeftTh && currentRightTh) {
                // 仅更新被该分隔符分割的相邻两列，其它所有列纹丝不动
                currentLeftTh.style.width = newLeftWidth + 'px';
                currentRightTh.style.width = newRightWidth + 'px';
            }
        });
    }

    function onMouseUp() {
        if (isColResizing) {
            isColResizing = false;
            if (resizeRafId) cancelAnimationFrame(resizeRafId);
            handle.classList.remove('resizing');
            document.body.classList.remove('resizing-col');
            saveColumnWidths();
            window.removeEventListener('mousemove', onMouseMove);
            window.removeEventListener('mouseup', onMouseUp);
        }
    }

    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
}

function saveColumnWidths() {
    const table = document.getElementById('file-table');
    if (!table) return;
    const ths = table.querySelectorAll('thead th[data-col]');
    const widths = {};
    ths.forEach(th => {
        const col = th.getAttribute('data-col');
        if (col && th.style.width) {
            widths[col] = th.style.width;
        }
    });
    try {
        localStorage.setItem('lds_col_widths', JSON.stringify(widths));
    } catch(e) {}
}

function restoreColumnWidths() {
    try {
        const saved = localStorage.getItem('lds_col_widths');
        if (!saved) return;
        const widths = JSON.parse(saved);
        const table = document.getElementById('file-table');
        if (!table) return;
        const ths = table.querySelectorAll('thead th[data-col]');
        ths.forEach(th => {
            const col = th.getAttribute('data-col');
            if (col && widths[col]) {
                th.style.width = widths[col];
            }
        });
    } catch(e) {}
}

function filterList() {
    var input = document.getElementById('search');
    var filter = input.value.toLowerCase().trim();
    
    if (isIncrementalRenderActive) {
        const table = document.getElementById('file-table');
        if (table) {
            const tbody = table.querySelector('tbody');
            if (tbody) {
                tbody.innerHTML = '';
                renderedRowCount = 0;
                appendNextBatch(BATCH_SIZE);
            }
        }
    } else {
        var rows = document.getElementsByClassName('item-row');
        for (var i = 0; i < rows.length; i++) {
            var name = rows[i].getAttribute('data-name');
            if (name.indexOf(filter) > -1) {
                rows[i].style.display = '';
            } else {
                rows[i].style.display = 'none';
            }
        }
    }

    updateStatusBar();
}

function filterCards() {
    var input = document.getElementById('search');
    var filter = input.value.toLowerCase();
    var cards = document.querySelectorAll('.card');
    
    cards.forEach(card => {
        var title = card.querySelector('.title') ? card.querySelector('.title').innerText.toLowerCase() : '';
        var desc = card.querySelector('.desc') ? card.querySelector('.desc').innerText.toLowerCase() : '';
        var name = card.getAttribute('data-path') ? card.getAttribute('data-path').toLowerCase() : '';
        if (title.indexOf(filter) > -1 || desc.indexOf(filter) > -1 || name.indexOf(filter) > -1) {
            card.style.display = '';
        } else {
            card.style.display = 'none';
        }
    });
}

function toggleFavIcon(e, path) {
    e.preventDefault();
    e.stopPropagation();
    fetch(`/api/favorite/toggle?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                window.location.reload();
            }
        });
}

function toggleSidebar(side) {
    if (side === 'left') {
        const sidebar = document.getElementById('sidebar-pane');
        if (sidebar) {
            const isCollapsed = sidebar.classList.toggle('collapsed');
            localStorage.setItem('explorer-sidebar-collapsed', isCollapsed ? 'true' : 'false');
        }
    } else if (side === 'right') {
        const preview = document.getElementById('preview-pane');
        if (preview) {
            const isCollapsed = preview.classList.toggle('collapsed');
            localStorage.setItem('explorer-preview-collapsed', isCollapsed ? 'true' : 'false');
        }
    }
}

function initCollapsibleSidebars() {
    const leftCollapsed = localStorage.getItem('explorer-sidebar-collapsed') === 'true';
    const rightCollapsed = localStorage.getItem('explorer-preview-collapsed') === 'true';

    const sidebar = document.getElementById('sidebar-pane');
    if (sidebar) {
        if (leftCollapsed) {
            sidebar.classList.add('collapsed');
        }
        sidebar.addEventListener('click', function(e) {
            if (this.classList.contains('collapsed')) {
                toggleSidebar('left');
                e.stopPropagation();
            }
        });
    }

    const preview = document.getElementById('preview-pane');
    if (preview) {
        if (rightCollapsed) {
            preview.classList.add('collapsed');
        }
        preview.addEventListener('click', function(e) {
            if (this.classList.contains('collapsed')) {
                toggleSidebar('right');
                e.stopPropagation();
            }
        });
    }
}

function formatStatusFileSize(bytes) {
    if (bytes === 0) return '0 B';
    if (bytes < 0 || isNaN(bytes)) return '-';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return (bytes / Math.pow(k, i)).toFixed(1) + ' ' + sizes[i];
}

function updateStatusBar() {
    const statusLeft = document.getElementById('status-left');
    const statusRight = document.getElementById('status-right');
    if (!statusLeft && !statusRight) return;

    const countElem = document.getElementById('status-count');
    const detailElem = document.getElementById('status-detail');
    const selectedElem = document.getElementById('status-selected');

    const i18n = window.I18N_STATUS || {
        totalItems: t('status_total_items', '{0}'),
        totalDetail: t('status_total_detail', '{0}', '{1}', '{2}'),
        selectedItems: t('status_selected_items', '{0}', '{1}'),
        noSelection: t('status_no_selection')
    };

    let totalItems = 0;
    let dirCount = 0;
    let fileCount = 0;
    let totalSizeBytes = 0;

    const rows = (allDataSourceRows && allDataSourceRows.length > 0)
        ? allDataSourceRows
        : Array.from(document.querySelectorAll('#file-table tbody tr.item-row'));

    const searchInput = document.getElementById('search');
    const filter = searchInput ? searchInput.value.toLowerCase().trim() : '';

    rows.forEach(r => {
        if (filter) {
            const name = (r.getAttribute('data-name') || '').toLowerCase();
            if (name.indexOf(filter) === -1) return;
        }
        totalItems++;
        const type = r.getAttribute('data-type');
        if (type === 'dir' || type === 'directory') {
            dirCount++;
        } else {
            fileCount++;
            const sz = parseFloat(r.getAttribute('data-size') || '0');
            if (sz > 0) totalSizeBytes += sz;
        }
    });

    if (countElem) {
        countElem.textContent = i18n.totalItems.replace('{0}', totalItems);
    }
    if (detailElem) {
        detailElem.textContent = i18n.totalDetail
            .replace('{0}', dirCount)
            .replace('{1}', fileCount)
            .replace('{2}', formatStatusFileSize(totalSizeBytes));
    }

    if (selectedElem) {
        const selCount = selectedRows.size;
        if (selCount === 0) {
            selectedElem.textContent = i18n.noSelection;
        } else {
            let selSizeBytes = 0;
            selectedRows.forEach(row => {
                const sz = parseFloat(row.getAttribute('data-size') || '0');
                if (sz > 0) selSizeBytes += sz;
            });
            selectedElem.textContent = i18n.selectedItems
                .replace('{0}', selCount)
                .replace('{1}', formatStatusFileSize(selSizeBytes));
        }
    }
}

let lastSelected = null;
let selectedRows = new Set();
let contextMenu = null;

document.addEventListener('DOMContentLoaded', () => {
    initCollapsibleSidebars();
    initProtocolSwitcher();
    restoreColumnWidths();
    
    if (typeof currentView !== 'undefined' && currentView === 'gradle') {
        initGradleDashboard();
        return;
    }

    initSelection();
    initIncrementalRender();
    initDragSelect();
    initContextMenu();
    initShortcuts();
    initViewSwitcher();

    const urlParams = new URLSearchParams(window.location.search);
    if (urlParams.get('showlogs') === '1') {
        showLogs();
        window.history.replaceState({}, document.title, window.location.pathname);
    }

    document.addEventListener('click', (e) => {
        const starBtn = e.target.closest('.fav-star-btn');
        if (starBtn) {
            e.preventDefault();
            e.stopPropagation();
            toggleFavIcon(e, starBtn.getAttribute('data-path'));
        }
    });

    // Close modal when clicking background overlay
    window.addEventListener('click', (e) => {
        if (e.target && e.target.classList && e.target.classList.contains('modal')) {
            e.target.style.display = 'none';
        }
    });

    // Close modal on Escape key
    window.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            document.querySelectorAll('.modal').forEach(m => m.style.display = 'none');
        }
    });
});

function getSelectableItems() {
    return Array.from(document.querySelectorAll('.item-row, .drive-card, .fav-card'));
}

function initSelection() {
    // 使用全局事件委托支持动态流式加载的行
    document.addEventListener('click', (e) => {
        const item = e.target.closest('.item-row, .drive-card, .fav-card');
        if (!item) {
            if (!e.target.closest('.context-menu, .toolbar, .explorer-statusbar')) {
                clearAllSelections();
            }
            return;
        }

        if (e.target.tagName === 'INPUT' || e.target.tagName === 'BUTTON') return;
        if (e.target.closest('.fav-star-btn')) return;

        const isCard = item.classList.contains('drive-card') || item.classList.contains('fav-card') || item.classList.contains('card');
        if (isCard) return;

        e.preventDefault();
        e.stopPropagation();
        handleItemSelection(item, e.ctrlKey, e.shiftKey);
    });

    document.addEventListener('dblclick', (e) => {
        const item = e.target.closest('.item-row');
        if (!item) return;
        const link = item.querySelector('a');
        if (link) {
            window.location.href = link.href;
        }
    });
}

function handleItemSelection(item, ctrl, shift) {
    const selectables = getSelectableItems();
    if (shift && lastSelected) {
        const idx1 = selectables.indexOf(lastSelected);
        const idx2 = selectables.indexOf(item);
        const start = Math.min(idx1, idx2);
        const end = Math.max(idx1, idx2);
        
        if (!ctrl) clearAllSelections();
        for (let i = start; i <= end; i++) {
            selectRow(selectables[i]);
        }
    } else if (ctrl) {
        if (selectedRows.has(item)) {
            deselectRow(item);
        } else {
            selectRow(item);
            lastSelected = item;
        }
    } else {
        clearAllSelections();
        selectRow(item);
        lastSelected = item;
    }
}

function selectRow(item) {
    item.classList.add('selected');
    selectedRows.add(item);
    if (typeof updateLivePreview === 'function') updateLivePreview();
    updateStatusBar();
}

function deselectRow(item) {
    item.classList.remove('selected');
    selectedRows.delete(item);
    if (typeof updateLivePreview === 'function') updateLivePreview();
    updateStatusBar();
}

function clearAllSelections() {
    const selectables = getSelectableItems();
    selectables.forEach(item => item.classList.remove('selected'));
    selectedRows.clear();
    lastSelected = null;
    if (typeof updateLivePreview === 'function') updateLivePreview();
    updateStatusBar();
}

function initDragSelect() {
    let startX, startY;
    let box = null;
    let isDragging = false;

    document.addEventListener('mousedown', (e) => {
        if (e.button !== 0) return; // Left click only
        if (e.target.closest('input, button, a, .context-menu, .toolbar')) return;

        isDragging = true;
        startX = e.pageX;
        startY = e.pageY;

        if (!e.ctrlKey && !e.shiftKey) {
            clearAllSelections();
        }
    });

    document.addEventListener('mousemove', (e) => {
        if (!isDragging) return;

        if (!box) {
            box = document.createElement('div');
            box.className = 'drag-select-box';
            document.body.appendChild(box);
        }

        const currentX = e.pageX;
        const currentY = e.pageY;

        const left = Math.min(startX, currentX);
        const top = Math.min(startY, currentY);
        const width = Math.abs(startX - currentX);
        const height = Math.abs(startY - currentY);

        box.style.left = left + 'px';
        box.style.top = top + 'px';
        box.style.width = width + 'px';
        box.style.height = height + 'px';

        const boxRect = {
            left: left - window.scrollX,
            top: top - window.scrollY,
            right: (left + width) - window.scrollX,
            bottom: (top + height) - window.scrollY
        };

        const selectables = getSelectableItems();
        selectables.forEach(item => {
            const rect = item.getBoundingClientRect();
            const isIntersect = !(rect.left > boxRect.right || 
                                  rect.right < boxRect.left || 
                                  rect.top > boxRect.bottom || 
                                  rect.bottom < boxRect.top);

            if (isIntersect) {
                selectRow(item);
            } else if (!e.ctrlKey) {
                deselectRow(item);
            }
        });
    });

    document.addEventListener('mouseup', () => {
        if (isDragging) {
            isDragging = false;
            if (box) {
                box.remove();
                box = null;
            }
        }
    });
}

function initContextMenu() {
    contextMenu = document.createElement('div');
    contextMenu.className = 'context-menu';
    document.body.appendChild(contextMenu);

    document.addEventListener('contextmenu', (e) => {
        const item = e.target.closest('.item-row, .drive-card, .fav-card');
        if (e.target.closest('input, .toolbar')) return;

        e.preventDefault();
        e.stopPropagation();

        if (item) {
            if (!selectedRows.has(item)) {
                clearAllSelections();
                selectRow(item);
            }
            renderContextMenu(e.clientX, e.clientY, true);
        } else {
            renderContextMenu(e.clientX, e.clientY, false);
        }
    });

    document.addEventListener('click', () => {
        if (contextMenu) contextMenu.style.display = 'none';
    });
}

function renderContextMenu(clientX, clientY, onTarget) {
    contextMenu.innerHTML = '';

    const selectables = Array.from(selectedRows);
    const targetItem = selectables.length > 0 ? selectables[0] : null;
    const isDir = targetItem && (targetItem.getAttribute('data-type') === 'directory' || targetItem.getAttribute('data-type') === 'dir');
    const isDrive = targetItem && targetItem.getAttribute('data-drive') === 'true';
    const isLobbyCard = targetItem && (targetItem.classList.contains('drive-card') || targetItem.classList.contains('fav-card') || targetItem.classList.contains('card'));

    const items = [];
    if (onTarget) {
        if (isDrive) {
            items.push({ label: t('ctx_open'), action: 'open' });
            items.push({ label: t('ctx_properties'), action: 'properties' });
        } else if (isLobbyCard) {
            items.push({ label: t('ctx_open'), action: 'open' });
            items.push({ label: t('ctx_favorite'), action: 'favorite' });
            items.push({ label: t('ctx_properties'), action: 'properties' });
        } else {
            items.push({ label: t('ctx_open'), action: 'open' });
            
            const submenuItems = [];
            if (!isDir) {
                submenuItems.push({ label: t('ctx_open_with_text'), action: 'openWith_text' });
            }
            submenuItems.push({ label: t('ctx_open_with_host'), action: 'openWith_host' });
            submenuItems.push({ label: isDir ? t('ctx_open_with_enter') : t('ctx_open_with_download'), action: 'openWith_standard' });

            items.push({ 
                label: t('ctx_open_with'), 
                action: 'openWith', 
                submenu: submenuItems 
            });

            items.push({ label: t('ctx_favorite'), action: 'favorite' });
            items.push({ label: t('ctx_rename'), action: 'rename' });
            items.push({ label: t('ctx_copy'), action: 'copy' });
            items.push({ label: t('ctx_cut'), action: 'cut' });
            items.push({ label: t('ctx_delete'), action: 'delete', danger: true });
            items.push({ label: t('ctx_properties'), action: 'properties' });
        }
    }
    const isLobby = typeof isLobbyPage !== 'undefined' && isLobbyPage;
    if (!isLobby) {
        items.push({ label: t('ctx_paste'), action: 'paste' });
    }

    const submenuShells = [];
    submenuShells.push({ 
        label: "🖥️ Windows Terminal", 
        action: "openTerminal_path",
        param: "wt.exe"
    });
    // Add simple other shells if available on startup, we dynamically generate elements
    if (typeof availableShells !== 'undefined') {
        availableShells.forEach((shell, index) => {
            const labelSuffix = index === 0 ? t('ctx_system_recommended') : "";
            submenuShells.push({ 
                label: "🖥️ " + shell.name + labelSuffix, 
                action: "openTerminal_path",
                param: shell.exePath
            });
        });
    }
    items.push({ 
        label: t('ctx_open_terminal'), 
        action: 'openTerminal',
        submenu: submenuShells 
    });

    if (!onTarget && typeof currentSortDir !== 'undefined' && currentSortDir !== 'none') {
        items.push({ label: t('ctx_reset_sort'), action: 'resetSort' });
    }

    items.push({ label: t('ctx_refresh'), action: 'refresh' });

    const menuWidth = 190;

    items.forEach(cfg => {
        const el = document.createElement('div');
        el.className = 'context-menu-item' + (cfg.danger ? ' danger' : '');
        
        if (cfg.submenu) {
            el.className += ' has-submenu';
            el.innerHTML = `<span>${cfg.label}</span><span style="font-size: 0.65rem; color: var(--text-muted); margin-left: 12px; flex-shrink: 0;">▶</span>`;
            
            const subEl = document.createElement('div');
            subEl.className = 'context-submenu';
            
            cfg.submenu.forEach(subCfg => {
                const subItem = document.createElement('div');
                subItem.className = 'context-menu-item';
                subItem.innerText = subCfg.label;
                subItem.addEventListener('click', (e) => {
                    e.stopPropagation();
                    contextMenu.style.display = 'none';
                    triggerSubAction(subCfg.action, selectables[0], subCfg.param);
                });
                subEl.appendChild(subItem);
            });
            
            el.appendChild(subEl);
            
            // 鼠标悬停在含子菜单项时，动态计算并避开屏幕视口边界
            el.addEventListener('mouseenter', () => {
                subEl.classList.remove('edge-left', 'edge-bottom');
                subEl.style.top = '';
                subEl.style.bottom = '';

                subEl.style.display = 'block';
                subEl.style.visibility = 'hidden';
                const subRect = subEl.getBoundingClientRect();
                const parentRect = el.getBoundingClientRect();
                subEl.style.display = '';
                subEl.style.visibility = '';

                // 水平方向视口碰撞检测 (X轴)
                if (parentRect.right + subRect.width + 8 > window.innerWidth) {
                    subEl.classList.add('edge-left');
                }

                // 垂直方向视口碰撞检测 (Y轴)
                if (parentRect.top + subRect.height + 8 > window.innerHeight) {
                    subEl.classList.add('edge-bottom');
                    const bottomAlignedTop = parentRect.bottom - subRect.height;
                    if (bottomAlignedTop < 8) {
                        subEl.classList.remove('edge-bottom');
                        subEl.style.top = (8 - parentRect.top) + 'px';
                    }
                }
            });
        } else {
            el.innerText = cfg.label;
            el.addEventListener('click', () => {
                contextMenu.style.display = 'none';
                triggerAction(cfg.action);
            });
        }
        contextMenu.appendChild(el);
    });

    // 预渲染以获取准确的物理尺寸，防止闪烁
    contextMenu.style.visibility = 'hidden';
    contextMenu.style.display = 'block';

    const actualWidth = contextMenu.offsetWidth || menuWidth;
    const actualHeight = contextMenu.offsetHeight || 220;
    const padding = 8;

    let x = clientX + 2;
    let y = clientY + 2;

    // X 轴智能边界检测：超出右边界则翻转至光标左侧，仍不足则吸附安全边距
    if (x + actualWidth > window.innerWidth - padding) {
        x = clientX - actualWidth - 2;
        if (x < padding) {
            x = Math.max(padding, window.innerWidth - actualWidth - padding);
        }
    }

    // Y 轴智能边界检测：超出下边界则向上翻转，仍不足则底对齐在视口安全区域
    if (y + actualHeight > window.innerHeight - padding) {
        y = clientY - actualHeight - 2;
        if (y < padding) {
            y = Math.max(padding, window.innerHeight - actualHeight - padding);
        }
    }

    contextMenu.style.left = Math.round(x) + 'px';
    contextMenu.style.top = Math.round(y) + 'px';
    contextMenu.style.visibility = 'visible';
}

function triggerAction(action) {
    const selectables = Array.from(selectedRows);
    if (action === 'open') {
        if (selectables.length === 1) {
            const link = selectables[0].querySelector('a');
            if (link) window.location.href = link.href;
        }
    } else if (action === 'favorite') {
        if (selectables.length > 0) {
            const promises = selectables.map(item => {
                const path = item.getAttribute('data-path');
                return fetch(`/api/favorite/toggle?path=${encodeURIComponent(path)}`);
            });
            Promise.all(promises).then(() => window.location.reload());
        }
    } else if (action === 'rename') {
        if (selectables.length === 1) {
            const item = selectables[0];
            const path = item.getAttribute('data-path');
            const oldName = item.querySelector('.name-text') ? item.querySelector('.name-text').innerText : item.getAttribute('data-name');
            const newName = prompt(t('prompt_new_name'), oldName);
            if (newName && newName !== oldName) {
                fetch(`/api/file/rename?path=${encodeURIComponent(path)}&newName=${encodeURIComponent(newName)}`)
                    .then(res => res.json())
                    .then(data => {
                        if (data.success) {
                            window.location.reload();
                        } else {
                            alert(data.message);
                        }
                    });
            }
        }
    } else if (action === 'copy' || action === 'cut') {
        if (selectables.length > 0) {
            const paths = selectables.map(item => item.getAttribute('data-path')).join('|');
            fetch(`/api/clipboard/set?paths=${encodeURIComponent(paths)}&action=${action}`)
                .then(res => res.json())
                .then(data => {
                    if (data.success) {
                        console.log('Clipboard set:', data);
                    }
                });
        }
    } else if (action === 'delete') {
        if (selectables.length > 0) {
            if (confirm(t('confirm_delete'))) {
                const paths = selectables.map(item => item.getAttribute('data-path')).join('|');
                fetch(`/api/file/delete?paths=${encodeURIComponent(paths)}`)
                    .then(res => res.json())
                    .then(data => {
                        if (data.success) {
                            window.location.reload();
                        } else {
                            alert(data.message);
                        }
                    });
            }
        }
    } else if (action === 'paste') {
        if (typeof currentDirPath === 'undefined') {
            alert(t('alert_paste_in_dir'));
            return;
        }
        fetch(`/api/file/paste?targetDir=${encodeURIComponent(currentDirPath)}`)
            .then(res => res.json())
            .then(data => {
                if (data.success) {
                    window.location.reload();
                } else {
                    alert(data.message);
                }
            });
    } else if (action === 'refresh') {
        window.location.reload();
    } else if (action === 'resetSort') {
        resetTableSort();
    } else if (action === 'properties') {
        showProperties();
    }
}

function initShortcuts() {
    window.addEventListener('keydown', (e) => {
        if (document.activeElement.tagName === 'INPUT' || document.activeElement.tagName === 'TEXTAREA') return;

        const isCtrl = e.ctrlKey || e.metaKey;
        const selectables = Array.from(selectedRows);

        if (isCtrl && e.key.toLowerCase() === 'c') {
            if (window.getSelection() && window.getSelection().toString().length > 0) {
                return;
            }
            e.preventDefault();
            triggerAction('copy');
        } else if (isCtrl && e.key.toLowerCase() === 'x') {
            e.preventDefault();
            triggerAction('cut');
        } else if (isCtrl && e.key.toLowerCase() === 'v') {
            e.preventDefault();
            triggerAction('paste');
        } else if (isCtrl && e.key.toLowerCase() === 'a') {
            e.preventDefault();
            const all = getSelectableItems();
            all.forEach(item => selectRow(item));
        } else if (e.key === 'Delete') {
            e.preventDefault();
            triggerAction('delete');
        } else if (e.key === 'F2') {
            e.preventDefault();
            triggerAction('rename');
        } else if (e.key === 'Enter') {
            if (selectables.length === 1) {
                e.preventDefault();
                triggerAction('open');
            }
        }
    });
}

function initViewSwitcher() {
    const currentView = localStorage.getItem('explorer-view-mode') || 'details';
    setViewMode(currentView);
}

function setViewMode(view) {
    localStorage.setItem('explorer-view-mode', view);
    document.body.classList.remove('view-details', 'view-large', 'view-medium');
    document.body.classList.add('view-' + view);
    
    const table = document.getElementById('file-table');
    if (table) {
        table.className = '';
        table.classList.add('view-' + view);
    }

    const select = document.getElementById('view-select');
    if (select) {
        select.value = view;
    }
}

function triggerSubAction(action, item, param) {
    if (action && action.indexOf("openWith_") === 0) {
        if (!item) return;
        const path = item.getAttribute("data-path");
        const href = item.querySelector("a").href;

        if (action === "openWith_text") {
            window.open(href + (href.indexOf('?') !== -1 ? '&' : '?') + "force_text=1", "_blank");
        } else if (action === "openWith_host") {
            openWithHost(path);
        } else if (action === "openWith_standard") {
            window.location.href = href;
        }
    } else if (action === "openTerminal_path") {
        let path = typeof currentDirPath !== 'undefined' ? currentDirPath : '';
        if (item) {
            path = item.getAttribute("data-path") || "";
        }
        fetch(`/api/file/open-terminal?path=${encodeURIComponent(path)}&exe=${encodeURIComponent(param)}`)
            .then(res => res.json())
            .then(data => {
                if (!data.success) {
                    alert(data.message);
                }
            })
            .catch(err => alert(t('alert_network_error', err.message)));
    }
}

function openWithHost(path) {
    fetch(`/api/file/open-host?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                alert(t('alert_open_fail', data.message));
            }
        })
        .catch(err => alert(t('alert_network_error', err.message)));
}

function closeProperties() {
    document.getElementById('properties-modal').style.display = 'none';
}

function showProperties() {
    const selectables = Array.from(selectedRows);
    if (selectables.length === 0) return;

    const paths = selectables.map(item => item.getAttribute('data-path')).join('|');
    const body = document.getElementById('properties-body');
    body.innerHTML = `<div style="text-align: center; padding: 20px;">${t('prop_calculating')}</div>`;
    document.getElementById('properties-modal').style.display = 'flex';

    fetch(`/api/file/properties?paths=${encodeURIComponent(paths)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                body.innerHTML = `<div style="color: #e74c3c;">${t('prop_failed', data.message)}</div>`;
                return;
            }

            let html = '<table class="properties-table">';
            if (!data.multi) {
                html += `<tr><td class="label">${t('prop_name')}</td><td class="val" style="font-weight: bold;">${data.name}</td></tr>`;
                html += `<tr><td class="label">${t('prop_type')}</td><td class="val">${data.isDir ? t('type_folder') : t('type_file_suffix', data.ext || 'File')}</td></tr>`;
                html += `<tr><td class="label">${t('prop_location')}</td><td class="val">${data.folder || '/'}</td></tr>`;
                html += `<tr><td class="label">${t('prop_size')}</td><td class="val">${data.size} (${t('prop_bytes_suffix', data.sizeBytes.toLocaleString())})</td></tr>`;
                if (data.isDir) {
                    html += `<tr><td class="label">${t('prop_contains')}</td><td class="val">${t('prop_contains_val', data.files, data.folders)}</td></tr>`;
                }
                html += `<tr><td colspan="2"><div class="properties-divider"></div></td></tr>`;
                html += `<tr><td class="label">${t('prop_path')}</td><td class="val">${data.path}</td></tr>`;
                html += `<tr><td class="label">${t('prop_created')}</td><td class="val">${data.created}</td></tr>`;
                html += `<tr><td class="label">${t('prop_modified')}</td><td class="val">${data.modified}</td></tr>`;
                if (data.attrs) {
                    html += `<tr><td class="label">${t('prop_attrs')}</td><td class="val">${data.attrs}</td></tr>`;
                }
            } else {
                html += `<tr><td class="label">${t('prop_name')}</td><td class="val" style="font-weight: bold;">${t('prop_selected_count', data.count)}</td></tr>`;
                html += `<tr><td class="label">${t('prop_contains')}</td><td class="val">${t('prop_contains_val', data.files, data.folders)}</td></tr>`;
                html += `<tr><td class="label">${t('prop_location')}</td><td class="val">${data.folder}</td></tr>`;
                html += `<tr><td class="label">${t('prop_total_size')}</td><td class="val">${data.size} (${t('prop_bytes_suffix', data.sizeBytes.toLocaleString())})</td></tr>`;
            }
            html += '</table>';
            body.innerHTML = html;
        })
        .catch(err => {
            body.innerHTML = `<div style="color: #e74c3c;">${t('prop_failed', err.message)}</div>`;
        });
}

function closeLogs() {
    document.getElementById('log-modal').style.display = 'none';
}

function clearLogs() {
    if (confirm(t('logs_confirm_clear'))) {
        fetch('/api/logs/clear')
            .then(res => res.json())
            .then(data => {
                if (data.success) {
                    showLogs();
                }
            });
    }
}

function showLogs() {
    const container = document.getElementById('log-container');
    container.innerHTML = `<div style="color: var(--text-muted); text-align: center; padding: 20px;">${t('logs_loading')}</div>`;
    document.getElementById('log-modal').style.display = 'flex';

    fetch('/api/logs')
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                container.innerHTML = `<div style="color: #f48771;">${t('logs_load_fail', data.message)}</div>`;
                return;
            }
            if (data.logs.length === 0) {
                container.innerHTML = `<div style="color: #777; text-align: center; padding: 20px;">${t('logs_empty')}</div>`;
                return;
            }
            let html = '';
            data.logs.forEach(line => {
                let cls = 'info';
                if (line.toLowerCase().includes('error') || line.toLowerCase().includes('失败') || line.toLowerCase().includes('出错') || line.toLowerCase().includes('❌')) {
                    cls = 'error';
                } else if (line.toLowerCase().includes('warn') || line.toLowerCase().includes('警告') || line.toLowerCase().includes('⚠️')) {
                    cls = 'warn';
                }
                html += `<div class="log-line ${cls}">${escapeHtml(line)}</div>`;
            });
            container.innerHTML = html;
            container.scrollTop = container.scrollHeight;
        })
        .catch(err => {
            container.innerHTML = `<div style="color: #f48771;">${t('logs_error', err.message)}</div>`;
        });
}

function escapeHtml(text) {
    if (typeof text !== 'string') text = text ? String(text) : '';
    return text
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}

// Tree navigation expand and collapse
function toggleTreeNode(id) {
    const children = document.getElementById('children-' + id);
    if (children) {
        const row = document.querySelector('#node-' + id + ' > .tree-row');
        const arrow = row.querySelector('.tree-arrow');
        if (children.style.display === 'none') {
            children.style.display = 'flex';
            arrow.classList.remove('collapsed');
            arrow.innerText = '▼';
        } else {
            children.style.display = 'none';
            arrow.classList.add('collapsed');
            arrow.innerText = '▶';
        }
    }
}

function toggleDevEcosystem(event) {
    if (event) {
        event.preventDefault();
        event.stopPropagation();
    }
    const children = document.getElementById('children-dev-ecosystem');
    if (children) {
        const parentNode = children.parentElement;
        const arrow = parentNode ? parentNode.querySelector('.tree-arrow') : null;
        if (children.style.display === 'none') {
            children.style.display = 'block';
            if (arrow) {
                arrow.classList.remove('collapsed');
                arrow.innerText = '▼';
            }
        } else {
            children.style.display = 'none';
            if (arrow) {
                arrow.classList.add('collapsed');
                arrow.innerText = '▶';
            }
        }
    }
}

function expandTreeNode(event, path) {
    if (event) {
        event.preventDefault();
        event.stopPropagation();
    }
    const arrow = event ? event.target : null;
    const containerId = 'dir-' + path.replace(/\\/g, '_').replace(/:/g, '_');
    const container = document.getElementById(containerId);
    
    if (!container) return;

    if (container.style.display !== 'none' && arrow) {
        container.style.display = 'none';
        arrow.classList.add('collapsed');
        arrow.innerText = '▶';
        return;
    }

    if (arrow) {
        arrow.classList.remove('collapsed');
        arrow.innerText = '▼';
    }
    container.style.display = 'flex';

    if (container.children.length > 0) {
        return;
    }

    container.innerHTML = `<div style='padding: 2px 10px; color: var(--text-muted); font-size: 0.8rem;'>${t('tree_loading')}</div>`;

    fetch(`/api/explorer/tree?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success || data.folders.length === 0) {
                container.innerHTML = `<div style='padding: 2px 10px; color: var(--text-muted); font-size: 0.8rem;'>${t('tree_empty')}</div>`;
                if (arrow && data.folders.length === 0) {
                    arrow.style.visibility = 'hidden';
                }
                return;
            }

            container.innerHTML = '';
            data.folders.forEach(fold => {
                const childContainerId = 'dir-' + fold.path.replace(/\\/g, '_').replace(/:/g, '_');
                const isNodeActive = typeof currentDirPath !== 'undefined' && currentDirPath.toLowerCase() === fold.path.toLowerCase();
                
                const node = document.createElement('div');
                node.className = 'tree-node';
                
                const row = document.createElement('div');
                row.className = 'tree-row' + (isNodeActive ? ' active' : '');
                row.setAttribute('data-path', fold.path);
                
                const parts = fold.path.split(/[\\/]/).filter(p => p);
                let webLink = '/';
                if (parts.length > 0) {
                    const driveLetter = parts[0].replace(':', '').toLowerCase();
                    const sub = parts.slice(1).join('/');
                    webLink = '/' + driveLetter + '/' + (sub ? sub + '/' : '');
                }

                row.innerHTML = `
                    <span class='tree-arrow collapsed' onclick='expandTreeNode(event, "${fold.path.replace(/\\/g, '\\\\\\\\')}")'>▶</span>
                    <a href='${webLink}' class='tree-link-inline' style='color:inherit;'>📁 ${fold.name}</a>
                `;
                
                const childContainer = document.createElement('div');
                childContainer.className = 'tree-children';
                childContainer.id = childContainerId;
                childContainer.style.display = 'none';

                node.appendChild(row);
                node.appendChild(childContainer);
                container.appendChild(node);
            });
        })
        .catch(err => {
            container.innerHTML = `<div style='padding: 2px 10px; color: #e74c3c; font-size: 0.8rem;'>${t('tree_load_fail')}</div>`;
        });
}

// Live Preview Update Logic
function updateLivePreview() {
    const preview = document.getElementById('preview-pane');
    if (!preview) return;

    const content = document.getElementById('preview-content');
    const selectables = Array.from(selectedRows);

    if (selectables.length === 0) {
        content.innerHTML = `<div style='color: var(--text-muted); font-size: 0.9rem; padding-top: 40px;'>${t('preview_unselected')}</div>`;
        return;
    }

    if (selectables.length > 1) {
        let filesCount = 0;
        let dirsCount = 0;
        selectables.forEach(row => {
            const type = row.getAttribute('data-type');
            if (type === 'file') filesCount++;
            else if (type === 'dir') dirsCount++;
        });
        content.innerHTML = `
            <div style='font-size: 3rem; margin-bottom: 10px;'>📚</div>
            <div style='font-weight: bold; font-size: 1rem;'>${t('preview_multi_title')}</div>
            <div class='preview-meta' style='margin-top: 15px;'>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>${t('preview_meta_total_objects')}</span>
                    <span class='preview-meta-value'>${t('preview_meta_items_val', selectables.length)}</span>
                </div>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>${t('preview_meta_files_count')}</span>
                    <span class='preview-meta-value'>${t('preview_meta_files_val', filesCount)}</span>
                </div>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>${t('preview_meta_folders_count')}</span>
                    <span class='preview-meta-value'>${t('preview_meta_folders_val', dirsCount)}</span>
                </div>
            </div>
        `;
        return;
    }

    const row = selectables[0];
    const name = row.getAttribute('data-name') || '';
    const path = row.getAttribute('data-path') || '';
    const type = row.getAttribute('data-type') || '';
    const isFav = row.getAttribute('data-favorite') === 'true';

    const displayName = row.querySelector('.name-text').innerText.trim();
    const timeCell = row.querySelector('td:nth-child(3)');
    const sizeCell = row.querySelector('td:nth-child(4)');
    const modifiedTime = timeCell ? timeCell.innerText.trim() : '';
    const sizeText = sizeCell ? sizeCell.innerText.trim() : '-';

    content.innerHTML = `<div style='text-align: center; padding: 20px;'>${t('preview_loading')}</div>`;

    if (type === 'dir') {
        content.innerHTML = `
            <div style='font-size: 3.5rem; margin-bottom: 5px;'>📁</div>
            <div style='font-weight: bold; font-size: 0.95rem; word-break: break-all;'>${escapeHtml(displayName)}</div>
            <div class='preview-meta' style='margin-top: 15px;'>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>${t('preview_meta_type')}</span>
                    <span class='preview-meta-value'>${t('preview_type_folder')}</span>
                </div>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>${t('preview_meta_modified')}</span>
                    <span class='preview-meta-value'>${modifiedTime}</span>
                </div>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>${t('preview_meta_fav_status')}</span>
                    <span class='preview-meta-value'>${isFav ? t('preview_meta_fav_yes') : t('preview_meta_fav_no')}</span>
                </div>
            </div>
            <div style='margin-top: 15px; font-size: 0.75rem; color: var(--text-muted); word-break: break-all; text-align: left; width: 100%; border-top: 1px solid var(--border-color); padding-top: 10px;'>
                <strong>${t('preview_meta_physical_path')}</strong><br>${escapeHtml(path)}
            </div>
        `;
    } else {
        const ext = displayName.split('.').pop().toLowerCase();
        const webLink = row.querySelector('td:nth-child(1) a').getAttribute('href');

        const imgExts = ['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'ico'];
        const audioExts = ['mp3', 'wav', 'ogg'];
        const videoExts = ['mp4', 'webm', 'mov'];
        const textExtensions = ["txt", "md", "log", "ini", "conf", "cfg", "json", "js", "css", "html", "htm", "xml", "bat", "sh", "py", "java", "cs", "go", "rs", "cpp", "h", "c", "properties", "yaml", "yml", "sql", "ts"];

        if (imgExts.includes(ext)) {
            content.innerHTML = `
                <img class='preview-thumbnail' src='${webLink}' alt='preview' onerror="this.src='/favicon.ico';">
                <div style='font-weight: bold; font-size: 0.9rem; word-break: break-all; margin-top: 10px;'>${escapeHtml(displayName)}</div>
                <div class='preview-meta' style='margin-top: 10px;'>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>${t('preview_meta_type')}</span>
                        <span class='preview-meta-value'>${t('preview_type_image', ext.toUpperCase())}</span>
                    </div>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>${t('preview_meta_size')}</span>
                        <span class='preview-meta-value'>${sizeText}</span>
                    </div>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>${t('preview_meta_modified')}</span>
                        <span class='preview-meta-value'>${modifiedTime}</span>
                    </div>
                </div>
            `;
        } else if (audioExts.includes(ext)) {
            content.innerHTML = `
                <div style='font-size: 3.5rem;'>🎵</div>
                <audio class='preview-audio' controls src='${webLink}'></audio>
                <div style='font-weight: bold; font-size: 0.9rem; word-break: break-all; margin-top: 10px;'>${escapeHtml(displayName)}</div>
                <div class='preview-meta' style='margin-top: 10px;'>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>${t('preview_meta_type')}</span>
                        <span class='preview-meta-value'>${t('preview_type_audio')}</span>
                    </div>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>${t('preview_meta_size')}</span>
                        <span class='preview-meta-value'>${sizeText}</span>
                    </div>
                </div>
            `;
        } else if (videoExts.includes(ext)) {
            content.innerHTML = `
                <video class='preview-video' controls src='${webLink}'></video>
                <div style='font-weight: bold; font-size: 0.9rem; word-break: break-all; margin-top: 10px;'>${escapeHtml(displayName)}</div>
                <div class='preview-meta' style='margin-top: 10px;'>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>${t('preview_meta_type')}</span>
                        <span class='preview-meta-value'>${t('preview_type_video')}</span>
                    </div>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>${t('preview_meta_size')}</span>
                        <span class='preview-meta-value'>${sizeText}</span>
                    </div>
                </div>
            `;
        } else if (textExtensions.includes(ext)) {
            fetch(`/api/file/preview?path=${encodeURIComponent(path)}`)
                .then(res => res.json())
                .then(data => {
                    if (data.success) {
                        content.innerHTML = `
                            <div class='preview-text-block'><pre>${escapeHtml(data.content)}</pre></div>
                            <div style='font-weight: bold; font-size: 0.9rem; word-break: break-all; margin-top: 10px;'>${escapeHtml(displayName)}</div>
                            <div class='preview-meta' style='margin-top: 10px;'>
                                <div class='preview-meta-row'>
                                    <span class='preview-meta-label'>${t('preview_meta_type')}</span>
                                    <span class='preview-meta-value'>${t('preview_type_text', ext.toUpperCase())}</span>
                                </div>
                                <div class='preview-meta-row'>
                                    <span class='preview-meta-label'>${t('preview_meta_size')}</span>
                                    <span class='preview-meta-value'>${sizeText}</span>
                                </div>
                            </div>
                        `;
                    } else {
                        showGenericPreview(displayName, ext, sizeText, modifiedTime, path);
                    }
                })
                .catch(() => {
                    showGenericPreview(displayName, ext, sizeText, modifiedTime, path);
                });
        } else {
            showGenericPreview(displayName, ext, sizeText, modifiedTime, path);
        }
    }
}

function showGenericPreview(displayName, ext, sizeText, modifiedTime, path) {
    const content = document.getElementById('preview-content');
    content.innerHTML = `
        <div style='font-size: 3.5rem; margin-bottom: 5px;'>📄</div>
        <div style='font-weight: bold; font-size: 0.90rem; word-break: break-all;'>${escapeHtml(displayName)}</div>
        <div class='preview-meta' style='margin-top: 15px;'>
            <div class='preview-meta-row'>
                <span class='preview-meta-label'>${t('preview_meta_type')}</span>
                <span class='preview-meta-value'>${t('preview_type_file', ext.toUpperCase() || 'File')}</span>
            </div>
            <div class='preview-meta-row'>
                <span class='preview-meta-label'>${t('preview_meta_size')}</span>
                <span class='preview-meta-value'>${sizeText}</span>
            </div>
            <div class='preview-meta-row'>
                <span class='preview-meta-label'>${t('preview_meta_modified')}</span>
                <span class='preview-meta-value'>${modifiedTime}</span>
            </div>
        </div>
        <div style='margin-top: 15px; font-size: 0.75rem; color: var(--text-muted); word-break: break-all; text-align: left; width: 100%; border-top: 1px solid var(--border-color); padding-top: 10px;'>
            <strong>${t('preview_meta_physical_path')}</strong><br>${escapeHtml(path)}
        </div>
    `;
}

// Address Bar Interactions
function activateAddressInput(event) {
    if (event && event.target && event.target.id === 'address-input') {
        event.stopPropagation();
        return;
    }
    if (event && event.target && event.target.closest('a')) return;
    if (event) {
        event.preventDefault(); // 防止默认失焦
        event.stopPropagation();
    }

    const crumbs = document.getElementById('breadcrumbs-bar');
    const input = document.getElementById('address-input');
    if (crumbs && input) {
        crumbs.style.display = 'none';
        input.style.display = 'block';
        input.value = typeof currentDirPath !== 'undefined' ? currentDirPath : 'D:\\\\';
        setTimeout(() => {
            input.focus();
            input.select();
        }, 50);
    }
}

function deactivateAddressInput() {
    setTimeout(() => {
        const crumbs = document.getElementById('breadcrumbs-bar');
        const input = document.getElementById('address-input');
        if (crumbs && input) {
            crumbs.style.display = 'flex';
            input.style.display = 'none';
        }
    }, 200);
}

function handleAddressKey(event) {
    if (event.key === 'Escape') {
        deactivateAddressInput();
    } else if (event.key === 'Enter') {
        const input = document.getElementById('address-input');
        if (!input) return;

        const rawVal = input.value.trim();
        if (!rawVal) return;

        fetch(`/api/explorer/exists?path=${encodeURIComponent(rawVal)}`)
            .then(res => res.json())
            .then(data => {
                if (data.success && data.exists) {
                    const webLink = convertPhysicalToWebPath(rawVal);
                    window.location.href = webLink;
                } else {
                    alert(t('alert_path_not_found'));
                }
            })
            .catch(() => {
                alert(t('alert_network_check_fail'));
            });
    }
}

function convertPhysicalToWebPath(physPath) {
    let path = physPath.trim().replace(/^['"]+|['"]+$/g, ""); 
    path = path.replace(/\\/g, "/").replace(/\/\/+/g, "/"); 
    
    const driveMatch = path.match(/^([a-zA-Z]):(.*)$/);
    if (driveMatch) {
        const drive = driveMatch[1].toLowerCase();
        let sub = driveMatch[2];
        if (!sub.startsWith("/")) sub = "/" + sub;
        if (!sub.endsWith("/")) sub = sub + "/";
        return "/" + drive + sub;
    }
    return path;
}

// Gradle Dashboard Support
function initGradleDashboard() {
    loadGradleInfo();
    loadGradleDeps("");
}

function loadGradleInfo() {
    fetch('/api/gradle/info')
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                document.getElementById('gradle-stat-home').innerHTML = `<span style='color: var(--text-muted); font-size: 0.8rem;'>${t('gradle_js_stat_no_home')}</span>`;
                document.getElementById('gradle-wrappers-grid').innerHTML = `<div style='padding: 15px; color: #e74c3c; text-align: center; grid-column: 1/-1;'>❌ ${data.message}</div>`;
                document.getElementById('gradle-deps-tbody').innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: #e74c3c;'>❌ ${data.message}</td></tr>`;
                return;
            }
            
            const scanBtn = document.getElementById('gradle-refresh-btn');
            if (data.isScanning) {
                if (scanBtn) {
                    scanBtn.innerText = t('gradle_js_btn_scanning');
                    scanBtn.disabled = true;
                    scanBtn.style.opacity = '0.6';
                    scanBtn.style.cursor = 'not-allowed';
                }
                document.getElementById('gradle-stat-count').innerHTML = `<span style='color: var(--text-muted); font-size: 0.85rem;'>${t('gradle_js_scanning')}</span>`;
                document.getElementById('gradle-stat-size').innerHTML = `<span style='color: var(--text-muted); font-size: 0.85rem;'>${t('gradle_js_scanning')}</span>`;
                document.getElementById('gradle-stat-kmp').innerHTML = `<span style='color: var(--text-muted); font-size: 0.85rem;'>${t('gradle_js_scanning')}</span>`;
                
                if (!window.gradlePollTimer) {
                    window.gradlePollTimer = setInterval(loadGradleInfo, 2000);
                }
            } else {
                if (scanBtn) {
                    scanBtn.innerText = t('gradle_btn_rescan');
                    scanBtn.disabled = false;
                    scanBtn.style.opacity = '1';
                    scanBtn.style.cursor = 'pointer';
                }
                document.getElementById('gradle-stat-count').innerText = data.dependencyCount;
                document.getElementById('gradle-stat-size').innerText = data.totalSize;
                const ratio = data.dependencyCount > 0 ? Math.round((data.kmpCount / data.dependencyCount) * 100) : 0;
                document.getElementById('gradle-stat-kmp').innerText = `${data.kmpCount} (${ratio}%)`;
                
                if (window.gradlePollTimer) {
                    clearInterval(window.gradlePollTimer);
                    window.gradlePollTimer = null;
                    loadGradleDeps(document.getElementById('gradle-search-input').value.trim());
                }
            }
            
            document.getElementById('gradle-stat-home').innerText = data.gradleHome;

            const grid = document.getElementById('gradle-wrappers-grid');
            grid.innerHTML = '';
            if (data.wrappers.length === 0) {
                grid.innerHTML = `<div style='padding: 15px; color: var(--text-muted); text-align: center; grid-column: 1/-1;'>${t('gradle_js_no_wrappers')}</div>`;
                return;
            }
            data.wrappers.forEach(w => {
                const card = document.createElement('div');
                card.style.cssText = 'background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 6px; padding: 6px 12px; display: flex; align-items: center; justify-content: center; cursor: pointer; transition: all 0.15s; min-width: 140px; height: 36px; flex-shrink: 0; box-sizing: border-box;';
                card.addEventListener('mouseenter', () => { card.style.borderColor = 'var(--accent-color)'; card.style.transform = 'translateY(-2px)'; });
                card.addEventListener('mouseleave', () => { card.style.borderColor = 'var(--border-color)'; card.style.transform = 'translateY(0)'; });
                card.addEventListener('click', (e) => {
                    loadWrapperDetails(w.version, card);
                });
                card.innerHTML = `
                    <div style='font-weight: bold; font-size: 0.9rem; display: flex; align-items: center; gap: 6px;'>
                        <span style='font-size: 1.1rem;'>☕</span> Gradle ${w.version}
                    </div>
                `;
                grid.appendChild(card);
            });
        })
        .catch(err => {
            console.error('Failed to get Gradle summary:', err);
        });
}

function triggerGradleScan() {
    const btn = document.getElementById('gradle-refresh-btn');
    if (btn && btn.disabled) return;
    
    fetch('/api/gradle/refresh')
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert(t('gradle_js_scan_triggered'));
                loadGradleInfo();
            } else {
                alert(t('gradle_js_scan_fail', data.message));
            }
        })
        .catch(err => {
            alert(t('gradle_js_scan_fail', err.message));
        });
}

function openInExplorer(path) {
    fetch(`/api/file/open-host?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(t('gradle_js_open_dir_fail', data.message));
        })
        .catch(err => {
            alert(t('gradle_js_open_dir_fail', err.message));
        });
}

function deleteWrapper(path, version) {
    if (!confirm(t('gradle_js_confirm_delete_wrapper', version, path))) {
        return;
    }
    fetch(`/api/gradle/delete-wrapper?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert(t('gradle_js_delete_wrapper_success'));
                loadGradleInfo();
            } else {
                alert(t('gradle_js_delete_wrapper_fail', data.message));
            }
        })
        .catch(err => {
            alert(t('gradle_js_delete_wrapper_fail', err.message));
        });
}

function loadWrapperDetails(version, cardElement) {
    const cards = document.querySelectorAll('#gradle-wrappers-grid > div');
    cards.forEach(c => {
        c.style.background = 'var(--bg-color)';
        c.style.boxShadow = 'none';
        c.style.borderColor = 'var(--border-color)';
    });
    if (cardElement) {
        cardElement.style.background = 'var(--row-hover)';
        cardElement.style.boxShadow = '0 0 10px rgba(0,0,0,0.15)';
        cardElement.style.borderColor = 'var(--accent-color)';
    }

    const preview = document.getElementById('preview-pane');
    if (preview && preview.classList.contains('collapsed')) {
        toggleSidebar('right');
    }

    const content = document.getElementById('gradle-preview-body');
    if (!content) return;
    
    content.innerHTML = `<div style="text-align: center; padding: 20px; color: var(--text-muted);">${t('gradle_js_wrapper_loading')}</div>`;

    fetch(`/api/gradle/wrapper-detail?version=${encodeURIComponent(version)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                content.innerHTML = `<div style="color: #e74c3c; padding: 15px;">❌ ${t('err_internal', data.message)}</div>`;
                return;
            }

            let subfoldersHtml = '';
            if (data.subfolders.length === 0) {
                subfoldersHtml = `<span style="color: var(--text-muted); font-size: 0.8rem;">${t('gradle_js_wrapper_subfolder_empty')}</span>`;
            } else {
                subfoldersHtml = data.subfolders.map(f => `<span style="background: rgba(41, 128, 185, 0.1); border: 1px solid rgba(41, 128, 185, 0.3); color: var(--accent-hover); font-weight: 500; font-size: 0.75rem; padding: 2px 6px; border-radius: 4px; display: inline-block;">${escapeHtml(f)}</span>`).join(' ');
            }

            content.innerHTML = `
                <div style="display: flex; align-items: center; gap: 10px; margin-bottom: 12px;">
                    <span style="font-size: 2.2rem;">☕</span>
                    <div>
                        <div style="font-weight: bold; font-size: 1.15rem; color: var(--accent-hover);">Gradle ${escapeHtml(data.version)}</div>
                        <div style="font-size: 0.75rem; color: var(--text-muted); margin-top: 2px;">${t('gradle_js_wrapper_pkg_tag')}</div>
                    </div>
                </div>
                
                <div style="background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 6px; padding: 10px; margin-bottom: 15px; font-size: 0.85rem; display: flex; flex-direction: column; gap: 6px;">
                    <div style="display: flex; justify-content: space-between;"><span style="color:var(--text-muted);">${t('gradle_js_wrapper_pkg_size')}</span><strong>${data.size}</strong></div>
                    <div style="display: flex; justify-content: space-between;"><span style="color:var(--text-muted);">${t('gradle_js_wrapper_total_files')}</span><span>${t('gradle_js_wrapper_total_files_val', data.fileCount.toLocaleString())}</span></div>
                    <div style="display: flex; justify-content: space-between;"><span style="color:var(--text-muted);">${t('gradle_js_wrapper_zip_file')}</span><span>${escapeHtml(data.zipFile)} (${data.zipExists ? t('gradle_js_wrapper_zip_downloaded') : t('gradle_js_wrapper_zip_missing')})</span></div>
                </div>

                <div style="margin-bottom: 15px;">
                    <h4 style="margin-top: 0; margin-bottom: 4px; font-size: 0.85rem; color: var(--text-muted);">${t('gradle_js_wrapper_path_title')}</h4>
                    <div style="font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 8px; border-radius: 4px; overflow-x: auto; white-space: nowrap; cursor: pointer; text-decoration: underline; width: 100%; max-width: 100%; box-sizing: border-box;" onclick="copyToClipboard(this, '${escapeJs(data.path)}' )" title="Copy Path">${escapeHtml(data.path)}</div>
                </div>

                <div style="margin-bottom: 15px;">
                    <h4 style="margin-top: 0; margin-bottom: 4px; font-size: 0.85rem; color: var(--text-muted);">${t('gradle_js_wrapper_hash_dir')}</h4>
                    <div style="font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 8px; border-radius: 4px; color: var(--text-muted); word-break: break-all;">${escapeHtml(data.hashFolder)}</div>
                </div>

                <div style="margin-bottom: 15px; display: flex; flex-direction: column; min-height: 100px;">
                    <h4 style="margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--text-muted);">${t('gradle_js_wrapper_subdirs')}</h4>
                    <div style="overflow-y: auto; background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 4px; padding: 8px; max-height: 140px; display: flex; flex-wrap: wrap; gap: 6px; align-content: flex-start;">
                        ${subfoldersHtml}
                    </div>
                </div>

                <div style="margin-top: 15px; padding-top: 10px; border-top: 1px solid var(--border-color); display: flex; flex-direction: column; gap: 8px;">
                    <button onclick="openInExplorer('${escapeJs(data.path)}' )" style="width: 100%; padding: 8px; border-radius: 4px; background: var(--border-color); border: 1px solid var(--border-color); color: var(--text-color); font-weight: bold; cursor: pointer;">${t('gradle_js_btn_open_in_explorer')}</button>
                    <button onclick="deleteWrapper('${escapeJs(data.path)}' , '${escapeJs(data.version)}' )" style="width: 100%; padding: 8px; border-radius: 4px; background: #e74c3c; border: none; color: white; font-weight: bold; cursor: pointer;">${t('gradle_js_btn_delete_wrapper')}</button>
                </div>
            `;
        })
        .catch(err => {
            content.innerHTML = `<div style="color: #e74c3c; padding: 15px;">❌ ${err.message}</div>`;
        });
}

function copyToClipboard(btn, text) {
    if (arguments.length === 1) {
        text = btn;
        btn = null;
    }
    const el = document.createElement('textarea');
    el.value = text;
    document.body.appendChild(el);
    el.select();
    document.execCommand('copy');
    document.body.removeChild(el);
    if (btn && btn.tagName === 'BUTTON') {
        const oldText = btn.innerText;
        btn.innerText = t('btn_copied');
        setTimeout(() => { btn.innerText = oldText; }, 2000);
    } else if (btn) {
        const oldTitle = btn.title;
        btn.title = t('btn_path_copied');
        setTimeout(() => { btn.title = oldTitle; }, 2000);
    }
}

let gradleSearchTimeout = null;
function onGradleSearchChange() {
    clearTimeout(gradleSearchTimeout);
    const q = document.getElementById('gradle-search-input').value.trim();
    gradleSearchTimeout = setTimeout(() => {
        loadGradleDeps(q);
    }, 300);
}

let gradleAllDeps = [];
let gradleCurrentPage = 1;
let gradlePageSize = 10;

function parseSizeToBytes(sizeStr) {
    if (!sizeStr) return 0;
    const num = parseFloat(sizeStr);
    if (isNaN(num)) return 0;
    const lower = sizeStr.toLowerCase();
    if (lower.includes('gb') || lower.includes('g')) return num * 1024 * 1024 * 1024;
    if (lower.includes('mb') || lower.includes('m')) return num * 1024 * 1024;
    if (lower.includes('kb') || lower.includes('k')) return num * 1024;
    return num;
}

function formatBytesToFriendly(bytes) {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

function compareVersions(a, b) {
    const partsA = String(a).split('.').map(Number);
    const partsB = String(b).split('.').map(Number);
    for (let i = 0; i < Math.max(partsA.length, partsB.length); i++) {
        const numA = partsA[i] || 0;
        const numB = partsB[i] || 0;
        if (numA !== numB) return numA - numB;
    }
    return 0;
}

function loadGradleDeps(query) {
    const tbody = document.getElementById('gradle-deps-tbody');
    tbody.innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: var(--text-muted);'>${t('gradle_js_deps_searching')}</td></tr>`;

    fetch(`/api/gradle/search?q=${encodeURIComponent(query)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                tbody.innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: #e74c3c;'>❌ ${data.message || t('gradle_js_dep_load_fail', '')}</td></tr>`;
                return;
            }
            
            const flatResults = data.results || [];
            const groupedMap = {};
            flatResults.forEach(item => {
                const key = `${item.group}:${item.artifact}`;
                if (!groupedMap[key]) {
                    groupedMap[key] = {
                        group: item.group,
                        artifact: item.artifact,
                        versions: [],
                        isKmp: false,
                        totalSizeBytes: 0
                    };
                }
                groupedMap[key].versions.push({
                    version: item.version,
                    isKmp: item.isKmp,
                    size: item.size,
                    path: item.path
                });
                if (item.isKmp) {
                    groupedMap[key].isKmp = true;
                }
                groupedMap[key].totalSizeBytes += parseSizeToBytes(item.size);
            });

            gradleAllDeps = Object.keys(groupedMap).map(key => {
                const g = groupedMap[key];
                const sortedVersions = g.versions.map(v => v.version).sort(compareVersions);
                const minVer = sortedVersions[0];
                const maxVer = sortedVersions[sortedVersions.length - 1];
                const versionText = minVer === maxVer ? minVer : `${minVer}~${maxVer}`;
                
                return {
                    group: g.group,
                    artifact: g.artifact,
                    versions: g.versions,
                    isKmp: g.isKmp,
                    size: formatBytesToFriendly(g.totalSizeBytes),
                    versionText: versionText
                };
            });

            gradleCurrentPage = 1;
            
            const title = document.getElementById('gradle-list-title');
            if (title) title.innerText = t('gradle_js_deps_list_count', gradleAllDeps.length);

            renderGradleDepsPage();
        })
        .catch(err => {
            tbody.innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: #e74c3c;'>${t('gradle_js_dep_load_fail', err.message)}</td></tr>`;
        });
}

function renderGradleDepsPage() {
    const tbody = document.getElementById('gradle-deps-tbody');
    if (!tbody) return;
    tbody.innerHTML = '';

    const totalItems = gradleAllDeps.length;
    if (totalItems === 0) {
        tbody.innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: var(--text-muted);'>${t('gradle_js_deps_no_match')}</td></tr>`;
        const info = document.getElementById('pagination-info');
        if (info) info.innerText = t('gradle_pagination_info', 1, 1, 0);
        return;
    }

    const totalPages = Math.ceil(totalItems / gradlePageSize) || 1;
    if (gradleCurrentPage > totalPages) gradleCurrentPage = totalPages;
    if (gradleCurrentPage < 1) gradleCurrentPage = 1;

    const startIndex = (gradleCurrentPage - 1) * gradlePageSize;
    const endIndex = Math.min(startIndex + gradlePageSize, totalItems);

    const pageItems = gradleAllDeps.slice(startIndex, endIndex);

    const info = document.getElementById('pagination-info');
    if (info) info.innerText = t('gradle_pagination_detailed', gradleCurrentPage, totalPages, totalItems, startIndex + 1, endIndex);

    pageItems.forEach(item => {
        const tr = document.createElement('tr');
        tr.className = 'item-row';
        tr.style.cssText = 'cursor: pointer; border-bottom: 1px solid var(--border-color); transition: background 0.15s;';
        tr.setAttribute('data-name', `${item.group}:${item.artifact}`);
        tr.setAttribute('data-group', item.group);
        tr.setAttribute('data-artifact', item.artifact);

        tr.onclick = (e) => {
            if (e.target.closest('.version-link')) return;
            document.querySelectorAll('#gradle-deps-tbody tr').forEach(r => r.style.background = '');
            tr.style.background = 'var(--row-hover)';
            
            const sorted = [...item.versions].sort((a, b) => compareVersions(a.version, b.version));
            const latest = sorted[sorted.length - 1];
            showGradleDetail(item.group, item.artifact, latest.version);
        };

        const versionTextHtml = `<span class="version-link" onclick="showVersionsModal(event, '${escapeJs(item.group)}', '${escapeJs(item.artifact)}')" style="color: var(--accent-hover); text-decoration: underline; cursor: pointer;" title="${escapeHtml(t('gradle_modal_versions'))}">${escapeHtml(item.versionText)}</span>`;

        tr.innerHTML = `
            <td style='padding: 6px 10px; font-family: monospace; font-size: 0.85rem;'>
                <span style='color: var(--text-muted);'>${escapeHtml(item.group)}:</span><strong>${escapeHtml(item.artifact)}</strong>
            </td>
            <td style='padding: 6px 10px; font-weight: 500; font-size: 0.85rem;'>${versionTextHtml}</td>
            <td style='padding: 6px 10px; text-align: center;'>${item.isKmp ? '<span style="color: var(--accent-hover); font-weight: bold; background: rgba(52, 152, 219, 0.1); padding: 2px 6px; border-radius: 4px; font-size: 0.75rem;">KMP</span>' : '<span style="color: var(--text-muted); font-size: 0.8rem;">-</span>'}</td>
            <td style='padding: 6px 10px; text-align: right; color: var(--text-muted); font-size: 0.8rem;'>${item.size}</td>
        `;
        tbody.appendChild(tr);
    });
}

function changeGradlePageSize() {
    const select = document.getElementById('gradle-page-size');
    if (select) {
        gradlePageSize = parseInt(select.value, 10);
        gradleCurrentPage = 1;
        renderGradleDepsPage();
    }
}

function gradleGoToPage(action) {
    const totalItems = gradleAllDeps.length;
    const totalPages = Math.ceil(totalItems / gradlePageSize) || 1;

    if (action === 'first') {
        gradleCurrentPage = 1;
    } else if (action === 'prev') {
        if (gradleCurrentPage > 1) gradleCurrentPage--;
    } else if (action === 'next') {
        if (gradleCurrentPage < totalPages) gradleCurrentPage++;
    } else if (action === 'last') {
        gradleCurrentPage = totalPages;
    }
    renderGradleDepsPage();
}

function showGradleDetail(group, artifact, version) {
    const pane = document.getElementById('gradle-preview-body');
    pane.innerHTML = `<div style="text-align: center; padding: 40px; color: var(--text-muted);">${t('gradle_js_pom_loading')}</div>`;

    fetch(`/api/gradle/detail?group=${encodeURIComponent(group)}&name=${encodeURIComponent(artifact)}&version=${encodeURIComponent(version)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                pane.innerHTML = `<div style="color: #e74c3c; padding: 20px;">${t('gradle_js_dep_load_fail', data.message)}</div>`;
                return;
            }

            let html = `
                <div style='display: flex; align-items: center; gap: 10px; margin-bottom: 12px;'>
                    <span style='font-size: 2.2rem;'>☕</span>
                    <div style='min-width: 0;'>
                        <div style='font-size: 0.8rem; color: var(--text-muted); word-break: break-all;'>${escapeHtml(data.group)}</div>
                        <div style='font-weight: bold; font-size: 1.15rem; word-break: break-all; margin-top: 2px;'>${escapeHtml(data.artifact)}</div>
                    </div>
                </div>
                
                <div style='background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 6px; padding: 10px; margin-bottom: 15px; font-size: 0.85rem; display: flex; flex-direction: column; gap: 6px;'>
                    <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>${t('gradle_js_meta_version')}</span><strong>${escapeHtml(data.version)}</strong></div>
                    <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>${t('gradle_js_meta_size')}</span><span>${data.size}</span></div>
                    <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>${t('gradle_js_meta_license')}</span><span>${escapeHtml(data.license)}</span></div>
                    ${data.organization ? `<div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>${t('gradle_js_meta_org')}</span><span>${escapeHtml(data.organization)}</span></div>` : ''}
                </div>
            `;

            if (data.isKmp) {
                html += `
                    <div style='margin-bottom: 15px;'>
                        <h4 style='margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--text-muted);'>${t('gradle_js_meta_kmp_platforms')}</h4>
                        <div style='display: flex; flex-wrap: wrap; gap: 6px;'>
                `;
                data.platforms.forEach(p => {
                    html += `<span style='background: rgba(46, 204, 113, 0.15); color: #2ecc71; font-weight: 500; font-size: 0.75rem; padding: 2px 8px; border-radius: 4px;'>${escapeHtml(p)}</span>`;
                });
                html += `
                        </div>
                    </div>
                `;
            }

            if (data.description) {
                html += `
                    <div style='margin-bottom: 15px;'>
                        <h4 style='margin-top: 0; margin-bottom: 4px; font-size: 0.85rem; color: var(--text-muted);'>${t('gradle_js_meta_desc')}</h4>
                        <div style='font-size: 0.8rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 8px; border-radius: 4px; max-height: 100px; overflow-y: auto; color: var(--text-muted); line-height: 1.4;'>${escapeHtml(data.description)}</div>
                    </div>
                `;
            }

            // Store dependencies and artifact name for full modal popup
            currentPreviewDeps = data.dependencies || [];
            currentPreviewArtifactName = data.artifact;

            html += `
                <div style='margin-bottom: 15px; flex: 1; display: flex; flex-direction: column; min-height: 120px;'>
                    <h4 onclick='showDepsListModal(event)' style='margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--accent-hover); text-decoration: underline; cursor: pointer;' title='${escapeHtml(t('gradle_modal_deps'))}'>${t('gradle_js_pom_deps_count', data.dependencies.length)}</h4>
                    <div style='flex: 1; overflow-y: auto; background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 4px; padding: 8px; max-height: 200px;'>
            `;
            if (data.dependencies.length === 0) {
                html += `<div style='font-size: 0.8rem; color: var(--text-muted); text-align: center; padding: 10px;'>${t('gradle_js_pom_no_deps')}</div>`;
            } else {
                data.dependencies.forEach(dep => {
                    html += `
                        <div onclick='showDepsListModal(event)' style='display: flex; align-items: center; justify-content: space-between; border-bottom: 1px solid var(--border-color); padding: 4px 0; font-size: 0.8rem; cursor: pointer; text-decoration: underline;' title='${escapeHtml(t('gradle_modal_deps'))}'>
                            <div style='min-width: 0; margin-right: 8px;' title='${escapeHtml(dep.group)}:${escapeHtml(dep.artifact)}:${escapeHtml(dep.version)}'>
                                <div style='font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;'>${escapeHtml(dep.artifact)}</div>
                                <div style='font-size: 0.7rem; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;'>${escapeHtml(dep.group)}:${escapeHtml(dep.version)}</div>
                            </div>
                            <div style='flex-shrink: 0;'>
                                ${dep.isDownloaded ? `<span style="color: #2ecc71; font-weight: bold;">${t('gradle_js_cached_yes')}</span>` : `<span style="color: #e67e22; font-weight: bold;">${t('gradle_js_cached_no')}</span>`}
                            </div>
                        </div>
                    `;
                });
            }
            html += `
                    </div>
                </div>
            `;

            html += `
                <div style='margin-bottom: 15px;'>
                    <h4 style='margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--text-muted);'>${t('gradle_js_quick_code_title')}</h4>
                    <div style='display: flex; flex-direction: column; gap: 8px;'>
                        <div>
                            <div style='display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;'><span style='font-size: 0.75rem; color: var(--text-muted);'>${t('gradle_js_code_jvm')}</span><button onclick='copyToClipboard(this, "${escapeJs(data.implementationCode)}")' style='background: var(--border-color); border: none; font-size: 0.75rem; padding: 2px 8px; border-radius: 3px; cursor: pointer; color: var(--text-color);'>${t('btn_copy')}</button></div>
                            <div style='font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 6px; border-radius: 4px; overflow-x: auto; white-space: nowrap; width: 100%; max-width: 100%; box-sizing: border-box;'>${escapeHtml(data.implementationCode)}</div>
                        </div>
                        <div>
                            <div style='display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;'><span style='font-size: 0.75rem; color: var(--text-muted);'>${t('gradle_js_code_kmp')}</span><button onclick='copyToClipboard(this, "${escapeJs(data.kmpCode)}")' style='background: var(--border-color); border: none; font-size: 0.75rem; padding: 2px 8px; border-radius: 3px; cursor: pointer; color: var(--text-color);'>${t('btn_copy')}</button></div>
                            <div style='font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 6px; border-radius: 4px; overflow-x: auto; white-space: nowrap; width: 100%; max-width: 100%; box-sizing: border-box;'>${escapeHtml(data.kmpCode)}</div>
                        </div>
                    </div>
                </div>
                
                <div style='margin-top: 15px; padding-top: 10px; border-top: 1px solid var(--border-color);'>
                    <button onclick='deleteGradleDep("${escapeJs(data.group)}", "${escapeJs(data.artifact)}", "${escapeJs(data.version)}")' style='width: 100%; padding: 8px; border-radius: 4px; background: #e74c3c; border: none; color: white; font-weight: bold; cursor: pointer; transition: background 0.15s;'>${t('gradle_js_btn_clean_dep')}</button>
                </div>
            `;

            pane.innerHTML = html;
        })
        .catch(err => {
            pane.innerHTML = `<div style="color: #e74c3c; padding: 20px;">${t('gradle_js_dep_load_fail', err.message)}</div>`;
        });
}

function deleteGradleDep(group, name, version) {
    if (!confirm(t('gradle_js_confirm_delete_dep', group, name, version))) {
        return;
    }

    fetch(`/api/gradle/delete?group=${encodeURIComponent(group)}&name=${encodeURIComponent(name)}&version=${encodeURIComponent(version)}`)
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert(t('gradle_js_delete_dep_success'));
                document.getElementById('gradle-preview-body').innerHTML = `
                    <div style='text-align: center; color: var(--text-muted); margin-top: 40px; padding: 10px;'>
                        <span style='font-size: 2.5rem; display: block; margin-bottom: 12px;'>✓</span>
                        ${t('gradle_js_delete_dep_success')}
                    </div>
                `;
                loadGradleInfo();
                onGradleSearchChange();
            } else {
                alert(t('gradle_js_delete_dep_fail', data.message));
            }
        })
        .catch(err => {
            alert(t('gradle_js_delete_dep_fail', err.message));
        });
}

function escapeJs(str) {
    return str.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/'/g, "\\'");
}

let currentPreviewDeps = [];
let currentPreviewArtifactName = "";

function showVersionsModal(event, group, artifact) {
    if (event) {
        event.preventDefault();
        event.stopPropagation();
    }
    
    const item = gradleAllDeps.find(d => d.group === group && d.artifact === artifact);
    if (!item) return;

    const modal = document.getElementById('versions-modal');
    const title = document.getElementById('versions-modal-title');
    const body = document.getElementById('versions-modal-body');
    
    if (modal && title && body) {
        title.innerHTML = t('gradle_js_modal_versions_title', escapeHtml(artifact));
        
        let html = `
            <div style='margin-bottom: 12px; font-size: 0.85rem; color: var(--text-muted);'>
                ${t('gradle_js_modal_group')} <span style='font-family: monospace;'>${escapeHtml(group)}</span>
            </div>
            <table class='file-table' style='width: 100%; border-collapse: collapse; margin-top: 10px;'>
                <thead>
                    <tr style='background: var(--bg-color); border-bottom: 1px solid var(--border-color); text-align: left;'>
                        <th style='padding: 6px 10px; font-size: 0.85rem;'>${t('gradle_th_version')}</th>
                        <th style='padding: 6px 10px; font-size: 0.85rem;'>${t('gradle_th_size')}</th>
                        <th style='padding: 6px 10px; text-align: right; font-size: 0.85rem;'>${t('th_actions')}</th>
                    </tr>
                </thead>
                <tbody>
        `;
        
        const sorted = [...item.versions].sort((a, b) => compareVersions(b.version, a.version));
        
        sorted.forEach(v => {
            html += `
                <tr style='border-bottom: 1px solid var(--border-color);'>
                    <td style='padding: 8px 10px; font-weight: bold; font-family: monospace; font-size: 0.85rem;'>
                        <span onclick="showVersionFilesModal(event, '${escapeJs(group)}', '${escapeJs(artifact)}', '${escapeJs(v.version)}')" style='color: var(--accent-hover); text-decoration: underline; cursor: pointer;' title='${escapeHtml(t('gradle_modal_files'))}'>${escapeHtml(v.version)}</span>
                    </td>
                    <td style='padding: 8px 10px; font-size: 0.85rem; color: var(--text-muted);'>${v.size}</td>
                    <td style='padding: 8px 10px; text-align: right; display: flex; justify-content: flex-end; gap: 6px;'>
                        <button onclick='copyToClipboard(this, "${escapeJs(v.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${escapeHtml(t('btn_copy_path'))}'>${t('btn_copy_path')}</button>
                        <button onclick='openInExplorer("${escapeJs(v.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${escapeHtml(t('btn_locate'))}'>${t('btn_locate')}</button>
                        <button onclick='deleteGradleDepFromModal("${escapeJs(group)}", "${escapeJs(artifact)}", "${escapeJs(v.version)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer; background: rgba(231, 76, 60, 0.1); border: 1px solid rgba(231, 76, 60, 0.3); color: #e74c3c;' title='${escapeHtml(t('btn_delete'))}'>${t('btn_delete')}</button>
                    </td>
                </tr>
            `;
        });
        
        html += `
                </tbody>
            </table>
        `;
        
        body.innerHTML = html;
        modal.style.display = 'flex';
    }
}

function closeVersionsModal() {
    const modal = document.getElementById('versions-modal');
    if (modal) modal.style.display = 'none';
}

function showDepsListModal(event) {
    if (event) {
        event.preventDefault();
        event.stopPropagation();
    }
    if (!currentPreviewDeps || currentPreviewDeps.length === 0) return;

    const modal = document.getElementById('dependencies-modal');
    const title = document.getElementById('dependencies-modal-title');
    const body = document.getElementById('dependencies-modal-body');
    
    if (modal && title && body) {
        title.innerHTML = t('gradle_js_modal_deps_title', escapeHtml(currentPreviewArtifactName));
        
        let html = `
            <div style='margin-bottom: 12px; font-size: 0.85rem; color: var(--text-muted);'>
                ${t('gradle_js_modal_deps_summary', currentPreviewDeps.length)}
            </div>
            <table class='file-table' style='width: 100%; border-collapse: collapse;'>
                <thead>
                    <tr style='background: var(--bg-color); border-bottom: 1px solid var(--border-color); text-align: left;'>
                        <th style='padding: 6px 10px; font-size: 0.85rem;'>${t('gradle_th_coord_full')}</th>
                        <th style='padding: 6px 10px; font-size: 0.85rem; text-align: right; width: 100px;'>${t('gradle_th_cache_status')}</th>
                    </tr>
                </thead>
                <tbody>
        `;
        
        currentPreviewDeps.forEach(dep => {
            html += `
                <tr style='border-bottom: 1px solid var(--border-color);'>
                    <td style='padding: 8px 10px; font-family: monospace; font-size: 0.8rem;'>
                        <span style='color: var(--text-muted);'>${escapeHtml(dep.group)}:</span><strong>${escapeHtml(dep.artifact)}</strong>:<span style='color: var(--accent-hover);'>${escapeHtml(dep.version)}</span>
                    </td>
                    <td style='padding: 8px 10px; text-align: right; font-size: 0.8rem;'>
                        ${dep.isDownloaded ? `<span style="color: #2ecc71; font-weight: bold;">${t('gradle_js_cached_yes')}</span>` : `<span style="color: #e67e22; font-weight: bold;">${t('gradle_js_cached_no')}</span>`}
                    </td>
                </tr>
            `;
        });
        
        html += `
                </tbody>
            </table>
        `;
        
        body.innerHTML = html;
        modal.style.display = 'flex';
    }
}

function closeDepsListModal() {
    const modal = document.getElementById('dependencies-modal');
    if (modal) modal.style.display = 'none';
}

function initProtocolSwitcher() {
    const btn = document.getElementById('protocol-switch-btn');
    if (!btn) return;

    if (typeof useHttps === 'undefined' || !useHttps) {
        btn.style.display = 'none';
        return;
    }

    const isHttps = window.location.protocol === 'https:';
    if (isHttps) {
        btn.innerHTML = t('proto_btn_http');
        btn.title = t('proto_toggle_to_http', httpPort);
    } else {
        btn.innerHTML = t('proto_btn_https');
        btn.title = t('proto_toggle_to_https', httpsPort);
    }
}

function toggleProtocol(event) {
    if (event) {
        event.preventDefault();
        event.stopPropagation();
    }
    
    if (typeof useHttps === 'undefined' || !useHttps) return;

    const isHttps = window.location.protocol === 'https:';
    const currentHost = window.location.hostname;
    const currentPath = window.location.pathname;
    const currentSearch = window.location.search;

    let targetUrl = '';
    if (isHttps) {
        targetUrl = 'http://' + currentHost + ':' + httpPort + currentPath + currentSearch;
    } else {
        targetUrl = 'https://' + currentHost + ':' + httpsPort + currentPath + currentSearch;
    }

    window.location.href = targetUrl;
}

function deleteGradleDepFromModal(group, name, version) {
    if (!confirm(t('gradle_js_confirm_delete_dep', group, name, version))) {
        return;
    }

    fetch(`/api/gradle/delete?group=${encodeURIComponent(group)}&name=${encodeURIComponent(name)}&version=${encodeURIComponent(version)}`)
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert(t('gradle_js_delete_dep_success'));
                loadGradleInfo();
                onGradleSearchChange();
                closeVersionsModal();
            } else {
                alert(t('gradle_js_delete_dep_fail', data.message));
            }
        })
        .catch(err => {
            alert(t('gradle_js_delete_dep_fail', err.message));
        });
}

function showVersionFilesModal(event, group, artifact, version) {
    if (event) {
        event.preventDefault();
        event.stopPropagation();
    }

    const modal = document.getElementById('files-modal');
    const title = document.getElementById('files-modal-title');
    const body = document.getElementById('files-modal-body');
    
    if (modal && title && body) {
        body.innerHTML = `<div style="text-align: center; padding: 30px; color: var(--text-muted);">${t('gradle_js_modal_files_loading')}</div>`;
        modal.style.display = 'flex';

        title.innerHTML = t('gradle_js_modal_files_title', escapeHtml(artifact), escapeHtml(version));

        fetch(`/api/gradle/version-files?group=${encodeURIComponent(group)}&name=${encodeURIComponent(artifact)}&version=${encodeURIComponent(version)}`)
            .then(res => res.json())
            .then(data => {
                if (!data.success) {
                    body.innerHTML = `<div style="color: #e74c3c; padding: 20px;">${t('gradle_js_modal_files_fail', data.message)}</div>`;
                    return;
                }

                if (!data.files || data.files.length === 0) {
                    body.innerHTML = `<div style="color: var(--text-muted); text-align: center; padding: 20px;">${t('gradle_js_modal_files_empty')}</div>`;
                    return;
                }

                let html = `
                    <table class='file-table' style='width: 100%; border-collapse: collapse; margin-top: 5px;'>
                        <thead>
                            <tr style='background: var(--bg-color); border-bottom: 1px solid var(--border-color); text-align: left;'>
                                <th style='padding: 6px 10px; font-size: 0.85rem;'>${t('th_filename')}</th>
                                <th style='padding: 6px 10px; font-size: 0.85rem;'>${t('gradle_th_size')}</th>
                                <th style='padding: 6px 10px; text-align: right; font-size: 0.85rem;'>${t('th_actions')}</th>
                            </tr>
                        </thead>
                        <tbody>
                `;

                data.files.forEach(f => {
                    html += `
                        <tr style='border-bottom: 1px solid var(--border-color);'>
                            <td style='padding: 8px 10px; font-family: monospace; font-size: 0.8rem; word-break: break-all;' title='${escapeHtml(f.path)}'>📄 ${escapeHtml(f.name)}</td>
                            <td style='padding: 8px 10px; font-size: 0.8rem; color: var(--text-muted); white-space: nowrap;'>${f.size}</td>
                            <td style='padding: 8px 10px; text-align: right; display: flex; justify-content: flex-end; gap: 6px; white-space: nowrap;'>
                                <button onclick='copyToClipboard(this, "${escapeJs(f.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${escapeHtml(t('btn_copy_path'))}'>${t('btn_copy_path')}</button>
                                <button onclick='openInExplorer("${escapeJs(f.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${escapeHtml(t('btn_locate'))}'>${t('btn_locate')}</button>
                            </td>
                        </tr>
                    `;
                });

                html += `
                        </tbody>
                    </table>
                `;
                body.innerHTML = html;
            })
            .catch(err => {
                body.innerHTML = `<div style="color: #e74c3c; padding: 20px;">${t('gradle_js_modal_files_fail', err.message)}</div>`;
            });
    }
}

function closeFilesModal() {
    const modal = document.getElementById('files-modal');
    if (modal) modal.style.display = 'none';
}

// --- Settings Modal Logic ---
function showSettingsModal() {
    const modal = document.getElementById('settings-modal');
    if (!modal) return;

    fetch('/api/settings')
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                alert(data.message || 'Failed to load settings');
                return;
            }

            document.getElementById('setting-port').value = data.port;
            document.getElementById('setting-https-port').value = data.https_port;
            document.getElementById('setting-use-https').checked = !!data.use_https;
            document.getElementById('setting-enable-dev').checked = !!data.enable_dev_ecosystem;
            document.getElementById('setting-startup').checked = !!data.startup_enabled;

            const exts = (data.text_extensions || '').split(/[,;\s\r\n]+/).filter(Boolean);
            document.getElementById('setting-text-ext').value = exts.join(', ');

            const langSelect = document.getElementById('setting-language');
            langSelect.innerHTML = '';
            if (data.languages && data.languages.length > 0) {
                data.languages.forEach(l => {
                    const opt = document.createElement('option');
                    opt.value = l.code;
                    opt.textContent = l.name + ' (' + l.code + ')';
                    if (l.code.toLowerCase() === (data.language || '').toLowerCase()) {
                        opt.selected = true;
                    }
                    langSelect.appendChild(opt);
                });
            }

            modal.style.display = 'flex';
            loadAppCacheInfo();
        })
        .catch(err => {
            alert('Failed to load settings: ' + err.message);
        });
}

function loadAppCacheInfo() {
    const sizeElem = document.getElementById('setting-cache-size');
    if (!sizeElem) return;
    sizeElem.textContent = window.t('settings_cache_calculating') || 'Calculating...';

    fetch('/api/settings/cache-info')
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                sizeElem.textContent = data.size || '0 B';
            } else {
                sizeElem.textContent = 'Error';
            }
        })
        .catch(() => {
            sizeElem.textContent = 'Error';
        });
}

function clearAppCache() {
    const sizeElem = document.getElementById('setting-cache-size');
    if (sizeElem) sizeElem.textContent = window.t('settings_cache_calculating') || 'Calculating...';

    fetch('/api/settings/clear-cache', { method: 'POST' })
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert(data.message || (window.t('settings_cache_cleared') || 'Cache cleared successfully!'));
                loadAppCacheInfo();
                // 延时刷新当前页面以应用最新的本地化和清除后的状态
                setTimeout(() => {
                    window.location.reload();
                }, 300);
            } else {
                alert(data.message || 'Failed to clear cache');
                loadAppCacheInfo();
            }
        })
        .catch(err => {
            alert('Network error: ' + err.message);
            loadAppCacheInfo();
        });
}

function openAppCacheDir() {
    fetch('/api/settings/open-cache-dir', { method: 'POST' })
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open cache directory');
        })
        .catch(err => alert('Network error: ' + err.message));
}

function closeSettingsModal() {
    const modal = document.getElementById('settings-modal');
    if (modal) modal.style.display = 'none';
}

function toggleSettingsTextExtFormat() {
    const textarea = document.getElementById('setting-text-ext');
    if (!textarea) return;
    const val = textarea.value.trim();
    if (!val) return;

    if (val.includes('\n')) {
        const items = val.split(/[\r\n]+/).map(s => s.trim().replace(/^\./, '')).filter(Boolean);
        textarea.value = items.join(', ');
    } else {
        const items = val.split(/[,;\s]+/).map(s => s.trim().replace(/^\./, '')).filter(Boolean);
        textarea.value = items.join('\n');
    }
}

function openSystemConfigFile() {
    fetch('/api/settings/open-config', { method: 'POST' })
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open config file');
        })
        .catch(err => alert('Network error: ' + err.message));
}

function openSystemAppDir() {
    fetch('/api/settings/open-app-dir', { method: 'POST' })
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open application directory');
        })
        .catch(err => alert('Network error: ' + err.message));
}

function saveSettingsForm() {
    const btn = document.getElementById('settings-save-btn');
    if (btn) btn.disabled = true;

    const payload = {
        port: parseInt(document.getElementById('setting-port').value, 10),
        https_port: parseInt(document.getElementById('setting-https-port').value, 10),
        use_https: document.getElementById('setting-use-https').checked,
        enable_dev_ecosystem: document.getElementById('setting-enable-dev').checked,
        startup_enabled: document.getElementById('setting-startup').checked,
        language: document.getElementById('setting-language').value,
        text_extensions: document.getElementById('setting-text-ext').value
    };

    fetch('/api/settings/save', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    })
    .then(res => res.json())
    .then(data => {
        if (btn) btn.disabled = false;
        if (data.success) {
            if (data.portChanged) {
                alert(t('settings_port_changed_reconnect'));
                const protocol = data.useHttps && location.protocol === 'https:' ? 'https:' : 'http:';
                const targetPort = protocol === 'https:' ? data.newHttpsPort : data.newPort;
                setTimeout(() => {
                    location.href = `${protocol}//${location.hostname}:${targetPort}/`;
                }, 1200);
            } else {
                alert(t('settings_save_success'));
                closeSettingsModal();
                location.reload();
            }
        } else {
            alert(data.message || 'Failed to save settings');
        }
    })
    .catch(err => {
        if (btn) btn.disabled = false;
        alert('Network error: ' + err.message);
    });
}
