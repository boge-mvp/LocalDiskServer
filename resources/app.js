// availableShells is injected globally in footer.html
function t(key, ...args) {
    if (typeof window !== 'undefined' && typeof window.t === 'function' && window.t !== t) {
        return window.t(key, ...args);
    }
    let dict = (typeof window !== 'undefined' && window.I18N_DICT) || {};
    let str = dict[key] || key;
    if (args.length > 0) {
        args.forEach((val, idx) => {
            str = str.replace(new RegExp('\\{' + idx + '\\}', 'g'), val);
        });
    }
    return str;
}
if (typeof window !== 'undefined') {
    window.t = t;
}

function formatBytes(bytes) {
    if (typeof bytes === 'string') {
        if (/^\d+(\.\d+)?\s*(B|KB|MB|GB|TB)$/i.test(bytes.trim())) return bytes;
        const num = parseFloat(bytes);
        if (isNaN(num)) return bytes;
        bytes = num;
    }
    if (bytes === 0) return '0 B';
    if (!bytes || bytes < 0 || isNaN(bytes)) return '-';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    if (i < 0) return '0 B';
    const idx = Math.min(i, sizes.length - 1);
    return parseFloat((bytes / Math.pow(k, idx)).toFixed(1)) + ' ' + sizes[idx];
}
if (typeof window !== 'undefined') {
    window.formatBytes = formatBytes;
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

// Global Client-Side Error Interceptor & Visual Notification
const recentClientErrors = new Set();

function reportAndDisplayClientError(errInfo) {
    const errorMsg = errInfo.message || String(errInfo.error || 'Unknown runtime error');
    const source = errInfo.source || '';
    const lineno = errInfo.lineno || '';
    const colno = errInfo.colno || '';
    const stack = (errInfo.error && errInfo.error.stack) || errInfo.stack || '';

    // Error fingerprint to prevent duplicate spamming within 6s
    const fingerprint = `${source}:${lineno}:${colno}:${errorMsg}`;
    if (recentClientErrors.has(fingerprint)) return;
    recentClientErrors.add(fingerprint);
    setTimeout(() => recentClientErrors.delete(fingerprint), 6000);

    // 1. Asynchronously report to backend system logs
    try {
        fetch('/api/logs/client-error', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                message: errorMsg,
                source: source,
                lineno: String(lineno),
                colno: String(colno),
                stack: stack
            })
        }).catch(() => {});
    } catch (e) {}

    // 2. Display Global Error Toast in UI
    if (typeof document === 'undefined' || !document.body) return;

    let container = document.getElementById('global-error-toast-container');
    if (!container) {
        container = document.createElement('div');
        container.id = 'global-error-toast-container';
        container.className = 'global-error-toast-container';
        document.body.appendChild(container);
    }

    const toast = document.createElement('div');
    toast.className = 'global-error-toast';

    let locationTag = '';
    if (source) {
        let lastSlash = Math.max(source.lastIndexOf('/'), source.lastIndexOf('\\'));
        let fileName = lastSlash >= 0 ? source.substring(lastSlash + 1) : source;
        if (lineno) fileName += `:${lineno}`;
        if (colno) fileName += `:${colno}`;
        locationTag = `<div class='global-error-toast-loc'>📍 ${fileName}</div>`;
    }

    const titleText = t('error_toast_title', '网页运行异常');
    const btnCopyText = t('error_toast_btn_copy', '复制详情');
    const btnLogsText = t('error_toast_btn_logs', '查看日志');
    const copiedText = t('error_toast_copied', '错误详情已复制到剪贴板');

    toast.innerHTML = `
        <div class='global-error-toast-header'>
            <div class='global-error-toast-title'>
                <span>⚠️</span>
                <span>${escapeHtml(titleText)}</span>
            </div>
            <button class='global-error-toast-close' title='Close'>✕</button>
        </div>
        <div class='global-error-toast-body'>
            <div style='font-weight: 500;'>${escapeHtml(errorMsg)}</div>
            ${locationTag}
        </div>
        <div class='global-error-toast-actions'>
            <button class='global-error-toast-btn btn-copy-err'>📋 ${escapeHtml(btnCopyText)}</button>
            <button class='global-error-toast-btn btn-view-logs'>📝 ${escapeHtml(btnLogsText)}</button>
        </div>
    `;

    const closeBtn = toast.querySelector('.global-error-toast-close');
    const dismiss = () => {
        toast.classList.add('toast-closing');
        setTimeout(() => {
            if (toast.parentElement) toast.parentElement.removeChild(toast);
        }, 300);
    };
    if (closeBtn) closeBtn.onclick = dismiss;

    const copyBtn = toast.querySelector('.btn-copy-err');
    if (copyBtn) {
        copyBtn.onclick = () => {
            const fullDetails = `[Web Error] ${errorMsg}\nLocation: ${source}:${lineno}:${colno}\nStack: ${stack}`;
            if (typeof copyToClipboard === 'function') {
                copyToClipboard(fullDetails, copiedText);
            } else if (navigator && navigator.clipboard) {
                navigator.clipboard.writeText(fullDetails);
            }
        };
    }

    const logsBtn = toast.querySelector('.btn-view-logs');
    if (logsBtn) {
        logsBtn.onclick = () => {
            dismiss();
            if (typeof showLogs === 'function') {
                showLogs();
            }
        };
    }

    container.appendChild(toast);
}

if (typeof window !== 'undefined') {
    window.addEventListener('error', function(event) {
        reportAndDisplayClientError({
            message: event.message,
            source: event.filename,
            lineno: event.lineno,
            colno: event.colno,
            error: event.error
        });
    });

    window.addEventListener('unhandledrejection', function(event) {
        const reason = event.reason;
        const msg = (reason && (reason.message || reason.stack)) || String(reason || 'Unhandled Promise Rejection');
        reportAndDisplayClientError({
            message: `Unhandled Promise: ${msg}`,
            source: '',
            lineno: '',
            colno: '',
            error: reason instanceof Error ? reason : null
        });
    });
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
    return formatBytes(bytes);
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
    
    if (typeof currentView !== 'undefined') {
        if (currentView === 'gradle') {
            initGradleDashboard();
            return;
        } else if (currentView === 'npm') {
            initNpmDashboard();
            return;
        } else if (currentView === 'pnpm') {
            initPnpmDashboard();
            return;
        }
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
        // 在输入控件中右键：放行浏览器原生菜单（方便原生复制/剪切/粘贴）
        if (e.target.closest('input, textarea, select')) return;

        const item = e.target.closest('.item-row, .drive-card, .fav-card');
        if (!item) {
            // 在页面空白处、表头、工具栏等非项目区域右键：放行浏览器默认菜单
            if (contextMenu) contextMenu.style.display = 'none';
            return;
        }

        // 仅在目标文件/目录/卡片项目上右键时拦截并弹出自定义操作菜单
        e.preventDefault();
        e.stopPropagation();

        if (!selectedRows.has(item)) {
            clearAllSelections();
            selectRow(item);
        }
        renderContextMenu(e.clientX, e.clientY, true);
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
    if (!content) return; // 非文件浏览页面（如 Maven/Gradle 页面）没有 preview-content 元素

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

    const displayName = (row.querySelector('.name-text') || {}).innerText?.trim() || name;
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

let gradleFullData = null;

function loadGradleInfo() {
    fetch('/api/gradle/info')
        .then(res => res.json())
        .then(data => {
            gradleFullData = data;
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
                    <button onclick="openInExplorer('${escapeJs(data.path)}' )" class='btn' style="width: 100%; padding: 8px; font-weight: bold;">${t('gradle_js_btn_open_in_explorer')}</button>
                    <button onclick="deleteWrapper('${escapeJs(data.path)}' , '${escapeJs(data.version)}' )" class='btn btn-danger' style="width: 100%; padding: 8px; font-weight: bold;">${t('gradle_js_btn_delete_wrapper')}</button>
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
    return formatBytes(bytes);
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
                            <div style='display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;'><span style='font-size: 0.75rem; color: var(--text-muted);'>${t('gradle_js_code_jvm')}</span><button onclick='copyToClipboard(this, "${escapeJs(data.implementationCode)}")' class='config-btn-sm'>${t('btn_copy')}</button></div>
                            <div style='font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 6px; border-radius: 4px; overflow-x: auto; white-space: nowrap; width: 100%; max-width: 100%; box-sizing: border-box;'>${escapeHtml(data.implementationCode)}</div>
                        </div>
                        <div>
                            <div style='display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;'><span style='font-size: 0.75rem; color: var(--text-muted);'>${t('gradle_js_code_kmp')}</span><button onclick='copyToClipboard(this, "${escapeJs(data.kmpCode)}")' class='config-btn-sm'>${t('btn_copy')}</button></div>
                            <div style='font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 6px; border-radius: 4px; overflow-x: auto; white-space: nowrap; width: 100%; max-width: 100%; box-sizing: border-box;'>${escapeHtml(data.kmpCode)}</div>
                        </div>
                    </div>
                </div>
                
                <div style='margin-top: 15px; padding-top: 10px; border-top: 1px solid var(--border-color);'>
                    <button onclick='deleteGradleDep("${escapeJs(data.group)}", "${escapeJs(data.artifact)}", "${escapeJs(data.version)}")' class='btn btn-danger' style='width: 100%; padding: 8px; font-weight: bold;'>${t('gradle_js_btn_clean_dep')}</button>
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
                        <button onclick='copyToClipboard(this, "${escapeJs(v.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem;' title='${escapeHtml(t('btn_copy_path'))}'>${t('btn_copy_path')}</button>
                        <button onclick='openInExplorer("${escapeJs(v.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem;' title='${escapeHtml(t('btn_locate'))}'>${t('btn_locate')}</button>
                        <button onclick='deleteGradleDepFromModal("${escapeJs(group)}", "${escapeJs(artifact)}", "${escapeJs(v.version)}")' class='btn btn-danger' style='padding: 2px 6px; font-size: 0.75rem;' title='${escapeHtml(t('btn_delete'))}'>${t('btn_delete')}</button>
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

function renderConfigPathRow(label, pathStr, apiPrefix) {
    const notFound = window.t('npm_cfg_not_found', '未检测到');
    const btnCopy = window.t('npm_cfg_btn_copy_path', '复制路径');
    const btnOpen = window.t('npm_cfg_btn_open_dir', '打开目录');
    const btnTerm = window.t('npm_cfg_btn_terminal', '打开终端');
    
    if (!pathStr) {
        return `
            <tr>
                <td class='config-key'>${escapeHtml(label)}</td>
                <td class='config-val' style='color: var(--text-muted);'>${notFound}</td>
                <td class='config-actions'></td>
            </tr>
        `;
    }
    
    const openFunc = apiPrefix === 'Gradle' ? 'openGradlePath' : (apiPrefix === 'Pnpm' ? 'openPnpmPath' : 'openNpmPath');
    const termFunc = apiPrefix === 'Gradle' ? 'openGradleTerminal' : (apiPrefix === 'Pnpm' ? 'openPnpmTerminal' : 'openNpmTerminal');

    return `
        <tr>
            <td class='config-key'>${escapeHtml(label)}</td>
            <td class='config-val'>${escapeHtml(pathStr)}</td>
            <td class='config-actions'>
                <div class='config-btn-group'>
                    <button class='config-btn-sm' onclick='copyToClipboard(this, "${escapeJs(pathStr)}")' title='${escapeHtml(btnCopy)}'>📋 ${btnCopy}</button>
                    <button class='config-btn-sm' onclick='${openFunc}("${escapeJs(pathStr)}")' title='${escapeHtml(btnOpen)}'>📂 ${btnOpen}</button>
                    <button class='config-btn-sm' onclick='${termFunc}("${escapeJs(pathStr)}")' title='${escapeHtml(btnTerm)}'>💻 ${btnTerm}</button>
                </div>
            </td>
        </tr>
    `;
}

function showGradleConfigModal() {
    const modal = document.getElementById('gradle-config-modal');
    const body = document.getElementById('gradle-config-modal-body');
    if (!modal || !body) return;

    modal.style.display = 'flex';
    if (!gradleFullData) {
        body.innerHTML = `<div style='text-align: center; padding: 30px; color: var(--text-muted);'>${window.t('gradle_js_scanning', '🔄 加载中...')}</div>`;
        return;
    }

    const d = gradleFullData;
    const notFound = window.t('npm_cfg_not_found', '未检测到');

    let propsHtml = '';
    if (d.gradleProperties && Object.keys(d.gradleProperties).length > 0) {
        propsHtml = `
            <table class='config-table' style='margin-bottom: 12px;'>
                ${Object.keys(d.gradleProperties).map(k => `
                    <tr>
                        <td class='config-key' style='width: 180px;'>${escapeHtml(k)}</td>
                        <td class='config-val' style='color: var(--accent-hover);'>${escapeHtml(d.gradleProperties[k])}</td>
                        <td class='config-actions'></td>
                    </tr>
                `).join('')}
            </table>
        `;
    } else {
        propsHtml = `<div style='color: var(--text-muted); font-size: 0.84rem; margin-bottom: 10px;'>${window.t('gradle_cfg_props_none', '未检测到全局 gradle.properties 配置文件')}</div>`;
    }

    let rawPropsHtml = '';
    if (d.gradlePropertiesContent) {
        rawPropsHtml = `
            <div style='font-size: 0.82rem; font-weight: 500; margin-bottom: 4px; color: var(--text-muted);'>${window.t('gradle_cfg_view_raw_props', '查看原始属性配置')}:</div>
            <div class='config-code-block'>${escapeHtml(d.gradlePropertiesContent)}</div>
        `;
    }

    body.innerHTML = `
        <div class='config-section'>
            <div class='config-section-title'>${window.t('gradle_cfg_sec_runtime', '☕ Java & Gradle 运行环境')}</div>
            <table class='config-table'>
                <tr>
                    <td class='config-key'>${window.t('gradle_cfg_java_ver', 'Java 运行时版本')}</td>
                    <td class='config-val'><strong style='color: var(--accent-color);'>${escapeHtml(d.javaVersion || notFound)}</strong></td>
                    <td class='config-actions'></td>
                </tr>
                ${renderConfigPathRow(window.t('gradle_cfg_java_home', 'JAVA_HOME 环境变量'), d.javaHome, 'Gradle')}
                ${renderConfigPathRow(window.t('gradle_cfg_java_path', 'Java 执行文件路径'), d.javaPath, 'Gradle')}
                <tr>
                    <td class='config-key'>${window.t('gradle_cfg_gradle_ver', 'Gradle CLI 版本')}</td>
                    <td class='config-val'><strong style='color: #02303a;'>${escapeHtml(d.gradleCliVersion ? 'v' + d.gradleCliVersion : notFound)}</strong></td>
                    <td class='config-actions'></td>
                </tr>
                ${renderConfigPathRow(window.t('gradle_cfg_gradle_path', 'Gradle CLI 执行文件'), d.gradleCliPath, 'Gradle')}
            </table>
        </div>

        <div class='config-section'>
            <div class='config-section-title'>${window.t('gradle_cfg_sec_paths', '📂 Gradle 核心存储与缓存路径')}</div>
            <table class='config-table'>
                ${renderConfigPathRow(window.t('gradle_cfg_gradle_home', 'GRADLE_USER_HOME'), d.gradleHome, 'Gradle')}
                ${renderConfigPathRow(window.t('gradle_cfg_caches_dir', '依赖缓存 (caches)'), d.cachesDir, 'Gradle')}
                ${renderConfigPathRow(window.t('gradle_cfg_wrapper_dists_dir', '发行版分发包 (wrapper/dists)'), d.wrapperDistsDir, 'Gradle')}
                ${renderConfigPathRow(window.t('gradle_cfg_daemon_dir', '守护进程日志 (daemon)'), d.daemonDir, 'Gradle')}
                ${renderConfigPathRow(window.t('gradle_cfg_jdks_dir', '自动预置 JDKs (jdks)'), d.jdksDir, 'Gradle')}
                ${renderConfigPathRow(window.t('gradle_cfg_init_dir', '全局初始化脚本 (init.d)'), d.initDir, 'Gradle')}
            </table>
        </div>

        <div class='config-section'>
            <div class='config-section-title'>${window.t('gradle_cfg_sec_props', '⚙️ 全局 gradle.properties 配置')}</div>
            <table class='config-table' style='margin-bottom: 10px;'>
                ${renderConfigPathRow(window.t('gradle_cfg_props_path', '属性文件路径'), d.gradlePropertiesPath, 'Gradle')}
            </table>
            ${propsHtml}
            ${rawPropsHtml}
        </div>
    `;
}

function closeGradleConfigModal() {
    const modal = document.getElementById('gradle-config-modal');
    if (modal) modal.style.display = 'none';
}

function openGradlePath(pathStr) {
    if (!pathStr) return;
    fetch('/api/gradle/open-path?path=' + encodeURIComponent(pathStr))
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open directory');
        })
        .catch(err => alert('Network error: ' + err.message));
}

function openGradleTerminal(pathStr) {
    if (!pathStr) return;
    fetch('/api/gradle/terminal?path=' + encodeURIComponent(pathStr))
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open terminal');
        })
        .catch(err => alert('Network error: ' + err.message));
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

// ----------------------------------------------------
// NPM Dashboard Frontend Logic
// ----------------------------------------------------
let npmFullData = null;
let npmRawPackages = [];
let npmFilteredPackages = [];
let npmCurrentPage = 1;
let npmPageSize = 15;

function initNpmDashboard() {
    initCollapsibleSidebars();
    initProtocolSwitcher();
    loadNpmInfo();
}

function loadNpmInfo() {
    if (!document.getElementById('npm-pkgs-tbody')) return;

    fetch('/api/npm/data')
        .then(res => res.json())
        .then(data => {
            npmFullData = data;
            const statPkgs = document.getElementById('npm-stat-pkgs');
            const statPkgsSize = document.getElementById('npm-stat-pkgs-size');
            const statCacache = document.getElementById('npm-stat-cacache');
            const statTemp = document.getElementById('npm-stat-temp');
            const statReg = document.getElementById('npm-stat-registry');

            if (statPkgs) statPkgs.textContent = data.packages ? data.packages.length : 0;
            if (statPkgsSize) statPkgsSize.textContent = formatBytes(data.totalPkgSize || 0);
            if (statCacache) statCacache.textContent = formatBytes(data.cacacheSize || 0);
            if (statTemp) statTemp.textContent = formatBytes((data.npxSize || 0) + (data.logsSize || 0));
            if (statReg) statReg.textContent = data.registry || '-';

            npmRawPackages = data.packages || [];
            onNpmSearchChange();

            if (data.scanning) {
                setTimeout(loadNpmInfo, 1500);
            }
        })
        .catch(err => console.error('Failed to load npm info:', err));
}

function triggerNpmScan() {
    const btn = document.getElementById('npm-refresh-btn');
    if (btn) { btn.disabled = true; btn.textContent = '🔄...'; }
    fetch('/api/npm/refresh')
        .then(res => res.json())
        .then(() => {
            setTimeout(() => {
                if (btn) { btn.disabled = false; btn.textContent = window.t('npm_btn_rescan') || '🔄 重新扫描'; }
                loadNpmInfo();
            }, 1000);
        })
        .catch(() => { if (btn) btn.disabled = false; });
}

function showNpmConfigModal() {
    const modal = document.getElementById('npm-config-modal');
    const body = document.getElementById('npm-config-modal-body');
    if (!modal || !body) return;

    modal.style.display = 'flex';
    if (!npmFullData) {
        body.innerHTML = `<div style='text-align: center; padding: 30px; color: var(--text-muted);'>${window.t('npm_loading')}</div>`;
        return;
    }

    const d = npmFullData;
    const notFound = window.t('npm_cfg_not_found', '未检测到');

    let configsHtml = '';
    if (d.npmrcConfigs && Object.keys(d.npmrcConfigs).length > 0) {
        configsHtml = `
            <table class='config-table' style='margin-bottom: 12px;'>
                ${Object.keys(d.npmrcConfigs).map(k => `
                    <tr>
                        <td class='config-key' style='width: 180px;'>${escapeHtml(k)}</td>
                        <td class='config-val' style='color: var(--accent-hover);'>${escapeHtml(d.npmrcConfigs[k])}</td>
                        <td class='config-actions'></td>
                    </tr>
                `).join('')}
            </table>
        `;
    } else {
        configsHtml = `<div style='color: var(--text-muted); font-size: 0.84rem; margin-bottom: 10px;'>${window.t('npm_cfg_npmrc_none')}</div>`;
    }

    let rawNpmrcHtml = '';
    if (d.npmrcContent) {
        rawNpmrcHtml = `
            <div style='font-size: 0.82rem; font-weight: 500; margin-bottom: 4px; color: var(--text-muted);'>${window.t('npm_cfg_view_raw_npmrc')}:</div>
            <div class='config-code-block'>${escapeHtml(d.npmrcContent)}</div>
        `;
    }

    body.innerHTML = `
        <div class='config-section'>
            <div class='config-section-title'>${window.t('npm_cfg_sec_runtime')}</div>
            <table class='config-table'>
                <tr>
                    <td class='config-key'>${window.t('npm_cfg_node_ver')}</td>
                    <td class='config-val'><strong style='color: var(--accent-color);'>${escapeHtml(d.nodeVersion || notFound)}</strong></td>
                    <td class='config-actions'></td>
                </tr>
                ${renderConfigPathRow(window.t('npm_cfg_node_path'), d.nodePath, 'Npm')}
                <tr>
                    <td class='config-key'>${window.t('npm_cfg_npm_ver')}</td>
                    <td class='config-val'><strong style='color: #cb3837;'>v${escapeHtml(d.npmVersion || notFound)}</strong></td>
                    <td class='config-actions'></td>
                </tr>
                ${renderConfigPathRow(window.t('npm_cfg_npm_path'), d.npmPath, 'Npm')}
            </table>
        </div>

        <div class='config-section'>
            <div class='config-section-title'>${window.t('npm_cfg_sec_paths')}</div>
            <table class='config-table'>
                ${renderConfigPathRow(window.t('npm_cfg_global_prefix'), d.globalPrefix, 'Npm')}
                ${renderConfigPathRow(window.t('npm_cfg_global_modules'), d.npmRoot, 'Npm')}
                ${renderConfigPathRow(window.t('npm_cfg_cache_dir'), d.cacheDir, 'Npm')}
                ${renderConfigPathRow(window.t('npm_cfg_logs_dir'), d.logsDir, 'Npm')}
                ${renderConfigPathRow(window.t('npm_cfg_npx_dir'), d.npxDir, 'Npm')}
                ${renderConfigPathRow(window.t('npm_cfg_cacache_dir'), d.cacacheDir, 'Npm')}
            </table>
        </div>

        <div class='config-section'>
            <div class='config-section-title'>${window.t('npm_cfg_sec_npmrc')}</div>
            <table class='config-table' style='margin-bottom: 10px;'>
                ${renderConfigPathRow(window.t('npm_cfg_npmrc_path'), d.npmrc, 'Npm')}
            </table>
            ${configsHtml}
            ${rawNpmrcHtml}
        </div>
    `;
}

function closeNpmConfigModal() {
    const modal = document.getElementById('npm-config-modal');
    if (modal) modal.style.display = 'none';
}

function openNpmPath(pathStr) {
    if (!pathStr) return;
    fetch('/api/npm/open-path?path=' + encodeURIComponent(pathStr))
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open directory');
        })
        .catch(err => alert('Network error: ' + err.message));
}

function openNpmTerminal(pathStr) {
    if (!pathStr) return;
    fetch('/api/npm/terminal?path=' + encodeURIComponent(pathStr))
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open terminal');
        })
        .catch(err => alert('Network error: ' + err.message));
}

function cleanNpmLogs() {
    if (!confirm(window.t('npm_clean_logs_confirm', 'npm-cache/_logs'))) return;
    fetch('/api/npm/clean-logs', { method: 'POST' })
        .then(res => res.json())
        .then(data => {
            alert(data.message || (data.success ? 'Success' : 'Fail'));
            loadNpmInfo();
        });
}

function cleanNpmNpx() {
    if (!confirm(window.t('npm_clean_npx_confirm', 'npm-cache/_npx'))) return;
    fetch('/api/npm/clean-npx', { method: 'POST' })
        .then(res => res.json())
        .then(data => {
            alert(data.message || (data.success ? 'Success' : 'Fail'));
            loadNpmInfo();
        });
}

function openNpmRoot() {
    if (npmFullData && npmFullData.npmRoot) {
        openNpmPath(npmFullData.npmRoot);
    } else {
        fetch('/api/npm/data')
            .then(res => res.json())
            .then(data => {
                if (data.npmRoot) openNpmPath(data.npmRoot);
            });
    }
}

function onNpmSearchChange() {
    const input = document.getElementById('npm-search-input');
    const query = input ? input.value.trim().toLowerCase() : '';
    if (!query) {
        npmFilteredPackages = [...npmRawPackages];
    } else {
        npmFilteredPackages = npmRawPackages.filter(p => 
            (p.name && p.name.toLowerCase().includes(query)) ||
            (p.description && p.description.toLowerCase().includes(query)) ||
            (p.bin && p.bin.toLowerCase().includes(query))
        );
    }
    npmCurrentPage = 1;
    renderNpmPkgsPage();
}

function changeNpmPageSize() {
    const sel = document.getElementById('npm-page-size');
    if (sel) npmPageSize = parseInt(sel.value, 10);
    npmCurrentPage = 1;
    renderNpmPkgsPage();
}

function npmGoToPage(action) {
    const totalPages = Math.ceil(npmFilteredPackages.length / npmPageSize) || 1;
    if (action === 'first') npmCurrentPage = 1;
    else if (action === 'prev') npmCurrentPage = Math.max(1, npmCurrentPage - 1);
    else if (action === 'next') npmCurrentPage = Math.min(totalPages, npmCurrentPage + 1);
    else if (action === 'last') npmCurrentPage = totalPages;
    renderNpmPkgsPage();
}

function renderNpmPkgsPage() {
    const tbody = document.getElementById('npm-pkgs-tbody');
    if (!tbody) return;

    if (npmFilteredPackages.length === 0) {
        tbody.innerHTML = `<tr><td colspan='5' style='padding: 20px; text-align: center; color: var(--text-muted);'>${window.t('npm_no_pkgs')}</td></tr>`;
        const info = document.getElementById('npm-pagination-info');
        if (info) info.textContent = window.t('npm_pagination_info', 0, 0, 0);
        return;
    }

    const startIdx = (npmCurrentPage - 1) * npmPageSize;
    const pageItems = npmFilteredPackages.slice(startIdx, startIdx + npmPageSize);
    const totalPages = Math.ceil(npmFilteredPackages.length / npmPageSize) || 1;

    const info = document.getElementById('npm-pagination-info');
    if (info) {
        const startItem = startIdx + 1;
        const endItem = Math.min(startIdx + npmPageSize, npmFilteredPackages.length);
        info.textContent = window.t('npm_pagination_detailed', npmCurrentPage, totalPages, npmFilteredPackages.length, startItem, endItem);
    }

    const btnFirst = document.getElementById('npm-btn-first');
    const btnPrev = document.getElementById('npm-btn-prev');
    const btnNext = document.getElementById('npm-btn-next');
    const btnLast = document.getElementById('npm-btn-last');

    if (btnFirst) btnFirst.disabled = (npmCurrentPage <= 1);
    if (btnPrev) btnPrev.disabled = (npmCurrentPage <= 1);
    if (btnNext) btnNext.disabled = (npmCurrentPage >= totalPages);
    if (btnLast) btnLast.disabled = (npmCurrentPage >= totalPages);

    let html = '';
    pageItems.forEach((p, idx) => {
        const rowClass = idx % 2 === 0 ? 'even-row' : 'odd-row';
        html += `
            <tr class='item-row ${rowClass}' onclick='showNpmDetail(${startIdx + idx})' style='cursor: pointer; transition: background 0.12s;'>
                <td style='padding: 6px 10px; font-weight: bold; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${escapeHtml(p.name)}'>
                    <span style='color: #cb3837; margin-right: 6px;'>📦</span>${escapeHtml(p.name)}
                </td>
                <td style='padding: 6px 10px; font-family: monospace; font-size: 0.85rem;'>v${escapeHtml(p.version || '-')}</td>
                <td style='padding: 6px 10px; font-size: 0.82rem; color: var(--text-muted);'>${escapeHtml(p.license || 'ISC')}</td>
                <td style='padding: 6px 10px; font-family: monospace; font-size: 0.82rem; color: var(--accent-hover); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${escapeHtml(p.bin || '-')}'>${escapeHtml(p.bin || '-')}</td>
                <td style='padding: 6px 10px; text-align: right; font-size: 0.85rem;'>${formatBytes(p.size)}</td>
            </tr>
        `;
    });
    tbody.innerHTML = html;
}

function showNpmDetail(index) {
    const pkg = npmFilteredPackages[index];
    const pane = document.getElementById('npm-preview-body');
    if (!pkg || !pane) return;

    // 声明依赖项
    let declaredDepsHtml = '';
    const declaredKeys = pkg.declaredDependencies ? Object.keys(pkg.declaredDependencies) : [];
    if (declaredKeys.length > 0) {
        declaredDepsHtml = `
            <div style='margin-top: 10px;'>
                <div style='font-size: 0.82rem; font-weight: bold; color: var(--text-muted); margin-bottom: 4px;'>
                    ${window.t('npm_detail_sec_declared_deps', declaredKeys.length)}
                </div>
                <div class='dep-tags-container'>
                    ${declaredKeys.map(k => `
                        <span class='dep-tag-pill'>
                            <span class='dep-name'>${escapeHtml(k)}</span>
                            <span class='dep-ver'>${escapeHtml(pkg.declaredDependencies[k])}</span>
                        </span>
                    `).join('')}
                </div>
            </div>
        `;
    }

    // 物理嵌套子模块 node_modules
    let nestedModulesHtml = '';
    const nestedList = pkg.nestedModules || [];
    if (nestedList.length > 0) {
        nestedModulesHtml = `
            <div style='margin-top: 12px;'>
                <div style='font-size: 0.82rem; font-weight: bold; color: var(--text-muted); margin-bottom: 6px;'>
                    ${window.t('npm_detail_sec_nested_modules', nestedList.length)}
                </div>
                <div style='display: flex; flex-direction: column; gap: 6px; max-height: 240px; overflow-y: auto;' class='custom-scrollbar'>
                    ${nestedList.map(sub => `
                        <div class='nested-module-card'>
                            <div class='nested-module-header'>
                                <div style='font-weight: bold; color: #cb3837; display: flex; align-items: center; gap: 4px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${escapeHtml(sub.name)}'>
                                    <span>📁</span> ${escapeHtml(sub.name)}
                                </div>
                                <span style='font-family: monospace; font-size: 0.75rem; color: var(--text-muted);'>${sub.version ? 'v' + escapeHtml(sub.version) : ''}</span>
                            </div>
                            <div style='display: flex; justify-content: space-between; align-items: center; margin-top: 2px;'>
                                <span style='font-size: 0.75rem; color: var(--accent-hover);'>${formatBytes(sub.size || 0)}</span>
                                <button onclick='openWithHost("${escapeJs(sub.installPath)}")' class='config-btn-sm' style='padding: 1px 5px; font-size: 0.72rem;' title='${escapeHtml(window.t('npm_detail_btn_open_nested', '定位子模块'))}'>📂 ${window.t('npm_detail_btn_open_nested', '定位')}</button>
                            </div>
                        </div>
                    `).join('')}
                </div>
            </div>
        `;
    }

    let html = `
        <div style='padding-bottom: 12px; border-bottom: 1px solid var(--border-color); margin-bottom: 12px;'>
            <div style='font-size: 1.15rem; font-weight: bold; color: #cb3837; display: flex; align-items: center; gap: 8px; margin-bottom: 4px;'>
                <span>📦</span> ${escapeHtml(pkg.name)}
            </div>
            <div style='font-size: 0.85rem; font-family: monospace; color: var(--text-muted); margin-bottom: 6px;'>v${escapeHtml(pkg.version || '')}</div>
            <div style='font-size: 0.85rem; line-height: 1.4; color: var(--text-color);'>${escapeHtml(pkg.description || '-')}</div>
        </div>

        <div style='display: flex; flex-direction: column; gap: 8px; font-size: 0.82rem; margin-bottom: 10px;'>
            <div><span style='color: var(--text-muted);'>${window.t('npm_js_pkg_bin')}</span> <strong style='font-family: monospace; color: var(--accent-hover);'>${escapeHtml(pkg.bin || '-')}</strong></div>
            <div><span style='color: var(--text-muted);'>${window.t('npm_js_pkg_license')}</span> <span>${escapeHtml(pkg.license || '-')}</span></div>
            <div><span style='color: var(--text-muted);'>${window.t('npm_js_pkg_author')}</span> <span>${escapeHtml(pkg.author || '-')}</span></div>
            <div><span style='color: var(--text-muted);'>${window.t('npm_js_pkg_homepage')}</span> <a href='${escapeHtml(pkg.homepage || '#')}' target='_blank' style='color: var(--accent-color); word-break: break-all;'>${escapeHtml(pkg.homepage || '-')}</a></div>
            <div><span style='color: var(--text-muted);'>${window.t('npm_js_pkg_path')}</span><br><span style='font-family: monospace; font-size: 0.75rem; word-break: break-all; color: var(--text-muted);'>${escapeHtml(pkg.installPath)}</span></div>
        </div>

        ${declaredDepsHtml}
        ${nestedModulesHtml}

        <div style='display: flex; flex-direction: column; gap: 6px; margin-top: auto; padding-top: 10px; border-top: 1px solid var(--border-color);'>
            <button onclick='openWithHost("${escapeJs(pkg.installPath)}")' class='btn' style='width: 100%; padding: 6px; font-size: 0.85rem; cursor: pointer;'>${window.t('npm_js_btn_open_pkg')}</button>
            <button onclick='openWithTerminal("${escapeJs(pkg.installPath)}")' class='btn' style='width: 100%; padding: 6px; font-size: 0.85rem; cursor: pointer;'>${window.t('npm_js_btn_terminal')}</button>
            <button onclick='copyToClipboard(this, "npm i -g ${escapeJs(pkg.name)}")' class='btn' style='width: 100%; padding: 6px; font-size: 0.85rem; cursor: pointer;'>${window.t('npm_js_btn_copy_install')}</button>
        </div>
    `;
    pane.innerHTML = html;
}

// ----------------------------------------------------
// PNPM Dashboard Frontend Logic
// ----------------------------------------------------
let pnpmFullData = null;
let pnpmRawStores = [];
let pnpmSelectedStoreIndex = 0;
let pnpmFilteredPackages = [];
let pnpmCurrentPage = 1;
let pnpmPageSize = 50;

function initPnpmDashboard() {
    initCollapsibleSidebars();
    initProtocolSwitcher();
    loadPnpmInfo();
}

function loadPnpmInfo() {
    if (!document.getElementById('pnpm-stores-grid')) return;

    fetch('/api/pnpm/data')
        .then(res => res.json())
        .then(data => {
            pnpmFullData = data;
            const statStoresSize = document.getElementById('pnpm-stat-stores-size');
            const statMeta = document.getElementById('pnpm-stat-meta');
            const statDlx = document.getElementById('pnpm-stat-dlx');

            if (statStoresSize) statStoresSize.textContent = formatBytes(data.totalStoreSize || 0);
            if (statMeta) statMeta.textContent = formatBytes(data.metadataSize || 0);
            if (statDlx) statDlx.textContent = formatBytes(data.dlxSize || 0);

            pnpmRawStores = data.stores || [];
            renderPnpmStores();
            selectPnpmStore(pnpmSelectedStoreIndex >= pnpmRawStores.length ? 0 : pnpmSelectedStoreIndex);

            if (data.scanning) {
                setTimeout(loadPnpmInfo, 1500);
            }
        })
        .catch(err => console.error('Failed to load pnpm info:', err));
}

function triggerPnpmScan() {
    const btn = document.getElementById('pnpm-refresh-btn');
    if (btn) { btn.disabled = true; btn.textContent = '🔄...'; }
    fetch('/api/pnpm/refresh')
        .then(res => res.json())
        .then(() => {
            setTimeout(() => {
                if (btn) { btn.disabled = false; btn.textContent = window.t('npm_btn_rescan') || '🔄 重新扫描'; }
                loadPnpmInfo();
            }, 1000);
        })
        .catch(() => { if (btn) btn.disabled = false; });
}

function cleanPnpmDlx() {
    const dlxPath = (typeof pnpmFullData !== 'undefined' && pnpmFullData && pnpmFullData.cacheDir)
        ? (pnpmFullData.cacheDir.replace(/[\\/]+$/, '') + '\\dlx')
        : 'pnpm-cache/dlx';
    if (!confirm(window.t('pnpm_clean_dlx_confirm', dlxPath))) return;
    fetch('/api/pnpm/clean-dlx', { method: 'POST' })
        .then(res => res.json())
        .then(data => {
            alert(data.message || (data.success ? 'Success' : 'Fail'));
            loadPnpmInfo();
        });
}

function renderPnpmStores() {
    const grid = document.getElementById('pnpm-stores-grid');
    if (!grid) return;

    if (pnpmRawStores.length === 0) {
        grid.innerHTML = `<div style='padding: 15px; color: var(--text-muted); text-align: center; grid-column: 1/-1;'>${window.t('pnpm_no_stores')}</div>`;
        return;
    }

    let html = '';
    pnpmRawStores.forEach((s, idx) => {
        const isSelected = (idx === pnpmSelectedStoreIndex);
        const pkgCount = s.packages ? s.packages.length : 0;
        html += `
            <div class='pnpm-store-card ${isSelected ? 'selected' : ''}' onclick='selectPnpmStore(${idx})'>
                <div style='display: flex; justify-content: space-between; align-items: center;'>
                    <div style='font-weight: bold; font-size: 1rem; color: #f69220; display: flex; align-items: center; gap: 6px;'>
                        <span>💾</span> Drive ${escapeHtml(s.driveLetter || '-')}
                    </div>
                    <span style='background: rgba(246, 146, 32, 0.15); color: #f69220; padding: 2px 6px; border-radius: 4px; font-size: 0.75rem; font-weight: bold;'>${escapeHtml(s.storeVersion || 'v3')}</span>
                </div>
                <div style='font-family: monospace; font-size: 0.75rem; color: var(--text-muted); word-break: break-all;'>${escapeHtml(s.storePath)}</div>
                <div style='display: flex; justify-content: space-between; font-size: 0.82rem; margin-top: 4px;'>
                    <span>${window.t('pnpm_store_files', '📦 模块/文件:')} <strong>${pkgCount} 模块 / ${s.fileCount} 文件</strong></span>
                    <span>${window.t('pnpm_store_size', '大小:')} <strong style='color: var(--accent-hover);'>${formatBytes(s.size)}</strong></span>
                </div>
                <div style='display: flex; gap: 6px; margin-top: 4px;' onclick='event.stopPropagation();'>
                    <button onclick='openWithHost("${escapeJs(s.storePath)}")' class='config-btn-sm' style='flex: 1; padding: 3px; font-size: 0.75rem; cursor: pointer;'>${window.t('pnpm_btn_open_store', '📂 打开目录')}</button>
                    <button onclick='openWithTerminal("${escapeJs(s.storePath)}")' class='config-btn-sm' style='flex: 1; padding: 3px; font-size: 0.75rem; cursor: pointer;'>${window.t('pnpm_btn_store_terminal', '💻 终端')}</button>
                </div>
            </div>
        `;
    });
    grid.innerHTML = html;
}

function selectPnpmStore(idx) {
    pnpmSelectedStoreIndex = idx;
    const cards = document.querySelectorAll('.pnpm-store-card');
    cards.forEach((c, i) => {
        if (i === idx) c.classList.add('selected');
        else c.classList.remove('selected');
    });

    const store = pnpmRawStores[idx];
    const titleText = document.getElementById('pnpm-store-pkgs-title-text');
    if (store && titleText) {
        const pkgs = store.packages || [];
        const driveLabel = store.driveLetter ? `Drive ${store.driveLetter}` : store.storePath;
        titleText.textContent = window.t('pnpm_store_selected_title', driveLabel, pkgs.length);
    }

    pnpmCurrentPage = 1;
    onPnpmSearchChange();
}

function onPnpmSearchChange() {
    const store = pnpmRawStores[pnpmSelectedStoreIndex];
    const allPkgs = store ? (store.packages || []) : [];

    const input = document.getElementById('pnpm-search-input');
    const query = input ? input.value.trim().toLowerCase() : '';
    let matched;
    if (!query) {
        matched = [...allPkgs];
    } else {
        matched = allPkgs.filter(p =>
            (p.name && p.name.toLowerCase().includes(query)) ||
            (p.version && p.version.toLowerCase().includes(query)) ||
            (p.hash && p.hash.toLowerCase().includes(query))
        );
    }

    // 按 name 聚合同名多版本包为一行（对齐 Gradle 管理页的分组展示）
    const groupedMap = {};
    matched.forEach(p => {
        if (!groupedMap[p.name]) {
            groupedMap[p.name] = { name: p.name, versions: [], totalFileCount: 0, totalSize: 0 };
        }
        groupedMap[p.name].versions.push(p);
        groupedMap[p.name].totalFileCount += (p.fileCount || 0);
        groupedMap[p.name].totalSize += (p.size || 0);
    });

    pnpmFilteredPackages = Object.keys(groupedMap).map(name => {
        const g = groupedMap[name];
        g.versions.sort((a, b) => compareVersions(b.version, a.version));
        const sortedAsc = g.versions.map(v => v.version).sort(compareVersions);
        g.versionText = sortedAsc[0] === sortedAsc[sortedAsc.length - 1]
            ? sortedAsc[0]
            : sortedAsc[0] + '~' + sortedAsc[sortedAsc.length - 1];
        return g;
    }).sort((a, b) => a.name.localeCompare(b.name));

    pnpmCurrentPage = 1;
    renderPnpmPkgsPage();
}

function changePnpmPageSize() {
    const select = document.getElementById('pnpm-page-size');
    if (select) {
        pnpmPageSize = parseInt(select.value, 10) || 50;
        pnpmCurrentPage = 1;
        renderPnpmPkgsPage();
    }
}

function pnpmGoToPage(action) {
    const totalPages = Math.ceil(pnpmFilteredPackages.length / pnpmPageSize) || 1;
    if (action === 'first') pnpmCurrentPage = 1;
    else if (action === 'prev') pnpmCurrentPage = Math.max(1, pnpmCurrentPage - 1);
    else if (action === 'next') pnpmCurrentPage = Math.min(totalPages, pnpmCurrentPage + 1);
    else if (action === 'last') pnpmCurrentPage = totalPages;
    renderPnpmPkgsPage();
}

function renderPnpmPkgsPage() {
    const tbody = document.getElementById('pnpm-pkgs-tbody');
    if (!tbody) return;

    if (pnpmFilteredPackages.length === 0) {
        tbody.innerHTML = `<tr><td colspan='4' style='padding: 25px; text-align: center; color: var(--text-muted);'>${window.t('pnpm_no_pkgs', '📭 当前虚拟存储中未找到匹配的包模块')}</td></tr>`;
        const info = document.getElementById('pnpm-pagination-info');
        if (info) info.textContent = window.t('pnpm_pagination_info', 0, 0, 0);
        return;
    }

    const startIdx = (pnpmCurrentPage - 1) * pnpmPageSize;
    const pageItems = pnpmFilteredPackages.slice(startIdx, startIdx + pnpmPageSize);
    const totalPages = Math.ceil(pnpmFilteredPackages.length / pnpmPageSize) || 1;

    const info = document.getElementById('pnpm-pagination-info');
    if (info) {
        const startItem = startIdx + 1;
        const endItem = Math.min(startIdx + pnpmPageSize, pnpmFilteredPackages.length);
        info.textContent = window.t('pnpm_pagination_detailed', pnpmCurrentPage, totalPages, pnpmFilteredPackages.length, startItem, endItem);
    }

    const btnFirst = document.getElementById('pnpm-btn-first');
    const btnPrev = document.getElementById('pnpm-btn-prev');
    const btnNext = document.getElementById('pnpm-btn-next');
    const btnLast = document.getElementById('pnpm-btn-last');

    if (btnFirst) btnFirst.disabled = (pnpmCurrentPage <= 1);
    if (btnPrev) btnPrev.disabled = (pnpmCurrentPage <= 1);
    if (btnNext) btnNext.disabled = (pnpmCurrentPage >= totalPages);
    if (btnLast) btnLast.disabled = (pnpmCurrentPage >= totalPages);

    let html = '';
    pageItems.forEach((p, idx) => {
        const rowClass = idx % 2 === 0 ? 'even-row' : 'odd-row';
        const versionTextHtml = `<span onclick="showPnpmVersionsModal(event, '${escapeJs(p.name)}')" style="color: var(--accent-hover); text-decoration: underline; cursor: pointer;" title="${escapeHtml(window.t('pnpm_modal_versions_title', p.name))}">${escapeHtml(p.versionText)}</span>`;
        html += `
            <tr class='item-row ${rowClass}' onclick='showPnpmDetail(${startIdx + idx})' style='cursor: pointer; transition: background 0.12s;'>
                <td style='padding: 6px 10px; font-weight: bold; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${escapeHtml(p.name)}'>
                    <span style='color: #f69220; margin-right: 6px;'>⚡</span>${escapeHtml(p.name)}
                </td>
                <td style='padding: 6px 10px; font-family: monospace; font-size: 0.85rem;'>${versionTextHtml}</td>
                <td style='padding: 6px 10px; font-family: monospace; font-size: 0.82rem; text-align: center; color: var(--text-muted);'>${p.totalFileCount || 0}</td>
                <td style='padding: 6px 10px; text-align: right; font-size: 0.85rem; color: var(--accent-hover); font-weight: 500;'>${formatBytes(p.totalSize)}</td>
            </tr>
        `;
    });
    tbody.innerHTML = html;

    // 默认展示第一项预览
    if (pageItems.length > 0) {
        showPnpmDetail(startIdx);
    }
}

let pnpmDetailGroupIndex = -1;
let pnpmDetailActiveVersion = '';

function showPnpmDetail(index) {
    const group = pnpmFilteredPackages[index];
    const pane = document.getElementById('pnpm-preview-body');
    if (!group || !pane) return;

    pnpmDetailGroupIndex = index;
    pnpmDetailActiveVersion = group.versions[0] ? group.versions[0].version : '';
    renderPnpmDetailPane();
}

function switchPnpmVersion(version) {
    pnpmDetailActiveVersion = version;
    renderPnpmDetailPane();
}

function renderPnpmDetailPane() {
    const group = pnpmFilteredPackages[pnpmDetailGroupIndex];
    const pane = document.getElementById('pnpm-preview-body');
    if (!group || !pane) return;

    const activePkg = group.versions.find(v => v.version === pnpmDetailActiveVersion) || group.versions[0];
    if (!activePkg) return;
    pnpmDetailActiveVersion = activePkg.version;

    // 版本切换 pill 条（多版本时显示，降序排列）
    let versionPillsHtml = '';
    if (group.versions.length > 1) {
        versionPillsHtml = `
            <div style='margin-bottom: 12px;'>
                <div style='font-size: 0.78rem; color: var(--text-muted); margin-bottom: 5px;'>${window.t('pnpm_ver_switch_hint')}</div>
                <div style='display: flex; flex-wrap: wrap; gap: 6px;'>
                    ${group.versions.map(v => {
                        const active = v.version === activePkg.version;
                        return `<span onclick='switchPnpmVersion("${escapeJs(v.version)}")' style='font-family: monospace; font-size: 0.75rem; padding: 2px 8px; border-radius: 4px; cursor: pointer; border: 1px solid ${active ? '#f69220' : 'var(--border-color)'}; background: ${active ? 'rgba(246, 146, 32, 0.15)' : 'var(--bg-color)'}; color: ${active ? '#f69220' : 'var(--text-muted)'}; font-weight: ${active ? 'bold' : 'normal'};'>v${escapeHtml(v.version)}</span>`;
                    }).join('')}
                </div>
            </div>
        `;
    }

    // 声明依赖项（从 store 内容文件的 package.json 解析）
    const depKeys = activePkg.dependencies ? Object.keys(activePkg.dependencies) : [];
    let pnpmDepsHtml = '';
    if (depKeys.length > 0) {
        pnpmDepsHtml = `
            <div style='margin-top: 4px; margin-bottom: 12px;'>
                <div style='font-size: 0.82rem; font-weight: bold; color: var(--text-muted); margin-bottom: 6px;'>
                    ${window.t('pnpm_detail_sec_deps', depKeys.length)}
                </div>
                <div class='dep-tags-container'>
                    ${depKeys.map(k => `
                        <span class='dep-tag-pill'>
                            <span class='dep-name'>${escapeHtml(k)}</span>
                            <span class='dep-ver'>${escapeHtml(activePkg.dependencies[k] || '')}</span>
                        </span>
                    `).join('')}
                </div>
            </div>
        `;
    } else {
        pnpmDepsHtml = `
            <div style='margin-top: 4px; margin-bottom: 12px;'>
                <div style='font-size: 0.82rem; font-weight: bold; color: var(--text-muted); margin-bottom: 6px;'>
                    ${window.t('pnpm_detail_sec_deps', 0)}
                </div>
                <div style='color: var(--text-muted); font-size: 0.78rem;'>${window.t('pnpm_detail_no_deps')}</div>
            </div>
        `;
    }

    pane.innerHTML = `
        <div style='padding-bottom: 12px; border-bottom: 1px solid var(--border-color); margin-bottom: 12px;'>
            <div style='font-size: 1.15rem; font-weight: bold; color: #f69220; display: flex; align-items: center; gap: 8px; margin-bottom: 4px; word-break: break-all;'>
                <span>⚡</span> ${escapeHtml(group.name)}
            </div>
            <div style='font-size: 0.85rem; font-family: monospace; color: var(--text-muted); margin-bottom: 6px;'>v${escapeHtml(activePkg.version || '')} · ${window.t('pnpm_detail_version_count', group.versions.length)}</div>
        </div>

        ${versionPillsHtml}

        <div style='display: flex; flex-direction: column; gap: 8px; font-size: 0.82rem; margin-bottom: 12px;'>
            <div><span style='color: var(--text-muted);'>${window.t('pnpm_detail_files_count')}</span> <strong style='font-family: monospace;'>${activePkg.fileCount || 0} 个文件</strong></div>
            <div><span style='color: var(--text-muted);'>${window.t('pnpm_detail_pkg_size')}</span> <strong style='font-family: monospace; color: var(--accent-hover);'>${formatBytes(activePkg.size || 0)}</strong></div>
            <div><span style='color: var(--text-muted);'>${window.t('pnpm_detail_index_file')}</span><br><span style='font-family: monospace; font-size: 0.72rem; word-break: break-all; color: var(--text-muted);'>${escapeHtml(activePkg.IndexFilePath || activePkg.indexFilePath || '-')}</span></div>
        </div>

        ${pnpmDepsHtml}

        <div style='margin-top: 8px; margin-bottom: 12px;'>
            <div style='display: flex; justify-content: space-between; align-items: center; font-size: 0.82rem; font-weight: bold; color: var(--text-muted); margin-bottom: 6px;'>
                <span>${window.t('pnpm_detail_sec_files', activePkg.fileCount || 0)}</span>
                <span id='pnpm-files-loading' style='font-size: 0.75rem; color: var(--accent-color); display: none;'>🔄 加载中...</span>
            </div>
            <div id='pnpm-pkg-files-list' style='max-height: 220px; overflow-y: auto; background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 4px; padding: 6px;' class='custom-scrollbar'>
                <div style='color: var(--text-muted); font-size: 0.75rem; text-align: center; padding: 10px;'>
                    <button class='config-btn-sm' onclick='loadPnpmPkgFiles("${escapeJs(activePkg.IndexFilePath || activePkg.indexFilePath)}")'>🔍 查看完整文件清单</button>
                </div>
            </div>
        </div>

        <div style='display: flex; flex-direction: column; gap: 6px; margin-top: auto; padding-top: 10px; border-top: 1px solid var(--border-color);'>
            <button onclick='openWithHost("${escapeJs(activePkg.IndexFilePath || activePkg.indexFilePath)}")' class='btn' style='width: 100%; padding: 6px; font-size: 0.85rem; cursor: pointer;'>📂 定位索引文件</button>
            <button onclick='copyToClipboard(this, "pnpm add ${escapeJs(group.name)}@${escapeJs(activePkg.version)}")' class='btn' style='width: 100%; padding: 6px; font-size: 0.85rem; cursor: pointer;'>📋 复制 pnpm add 命令</button>
        </div>
    `;
}

function showPnpmVersionsModal(event, name) {
    if (event) {
        event.preventDefault();
        event.stopPropagation();
    }
    const group = pnpmFilteredPackages.find(g => g.name === name);
    if (!group) return;

    const modal = document.getElementById('pnpm-versions-modal');
    const title = document.getElementById('pnpm-versions-modal-title');
    const body = document.getElementById('pnpm-versions-modal-body');
    if (!modal || !title || !body) return;

    title.textContent = window.t('pnpm_modal_versions_title', name);

    let html = `
        <table class='file-table' style='width: 100%; border-collapse: collapse; margin-top: 4px;'>
            <thead>
                <tr style='background: var(--bg-color); border-bottom: 1px solid var(--border-color); text-align: left;'>
                    <th style='padding: 6px 10px; font-size: 0.85rem;'>${window.t('npm_th_version')}</th>
                    <th style='padding: 6px 10px; font-size: 0.85rem; text-align: center;'>${window.t('pnpm_th_file_count')}</th>
                    <th style='padding: 6px 10px; font-size: 0.85rem;'>${window.t('npm_th_size')}</th>
                    <th style='padding: 6px 10px; font-size: 0.85rem; text-align: right; width: 140px;'>操作</th>
                </tr>
            </thead>
            <tbody>
    `;
    group.versions.forEach(v => {
        html += `
            <tr style='border-bottom: 1px solid var(--border-color);'>
                <td style='padding: 8px 10px; font-weight: bold; font-family: monospace; font-size: 0.85rem;'>v${escapeHtml(v.version || '-')}</td>
                <td style='padding: 8px 10px; text-align: center; font-family: monospace; font-size: 0.8rem; color: var(--text-muted);'>${v.fileCount || 0}</td>
                <td style='padding: 8px 10px; font-size: 0.85rem; color: var(--accent-hover);'>${formatBytes(v.size || 0)}</td>
                <td style='padding: 8px 10px; text-align: right;'>
                    <div style='display: inline-flex; gap: 6px;'>
                        <button onclick='copyToClipboard(this, "pnpm add ${escapeJs(group.name)}@${escapeJs(v.version)}")' class='config-btn-sm' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${escapeHtml(window.t('npm_js_btn_copy_install'))}'>📋</button>
                        <button onclick='openWithHost("${escapeJs(v.IndexFilePath || v.indexFilePath || '')}")' class='config-btn-sm' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${escapeHtml(window.t('btn_locate'))}'>📂</button>
                        <button onclick='viewPnpmVersionDetail("${escapeJs(group.name)}", "${escapeJs(v.version)}")' class='config-btn-sm' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer; white-space: nowrap;' title='${escapeHtml(window.t('pnpm_btn_view_detail'))}'>🔍</button>
                    </div>
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

function closePnpmVersionsModal() {
    const modal = document.getElementById('pnpm-versions-modal');
    if (modal) modal.style.display = 'none';
}

function viewPnpmVersionDetail(name, version) {
    closePnpmVersionsModal();
    const idx = pnpmFilteredPackages.findIndex(g => g.name === name);
    if (idx < 0) return;
    showPnpmDetail(idx);
    if (version) switchPnpmVersion(version);
}

// ====== Maven Local Repository Management ======
let mavenRawData = null;
let mavenFilteredGroups = [];
let mavenCurrentPage = 1;
let mavenPageSize = 50;
let mavenDetailGroupIndex = -1;
let mavenDetailActiveVersion = '';

function fetchMavenData() {
    return fetch('/api/maven/data').then(r => r.json()).then(data => {
        mavenRawData = data;
        if (data.artifacts && data.artifacts.length > 0) {
            document.getElementById('maven-stat-artifacts').textContent = data.totalArtifacts + ' 个';
            document.getElementById('maven-stat-size').textContent = formatBytes(data.totalSize);
            const pathEl = document.getElementById('maven-stat-path');
            if (pathEl) pathEl.title = pathEl.textContent = data.localRepoPath || '-';
        }
        updateMavenFailedBadge(data);
        return data;
    });
}

function updateMavenFailedBadge(data) {
    const btn = document.getElementById('maven-failed-btn');
    if (!btn) return;
    const failedCount = (data.artifacts || []).filter(a => a.parseFailed).length;
    const countEl = document.getElementById('maven-failed-count');
    if (countEl) countEl.textContent = failedCount;
    btn.style.display = failedCount > 0 ? 'inline-flex' : 'none';
}

function onMavenSearchChange() {
    const input = document.getElementById('maven-search-input');
    const query = input ? input.value.trim().toLowerCase() : '';
    let allArtifacts = mavenRawData ? (mavenRawData.artifacts || []) : [];

    let matched;
    if (!query) {
        matched = [...allArtifacts];
    } else {
        matched = allArtifacts.filter(a =>
            (a.groupId && a.groupId.toLowerCase().includes(query)) ||
            (a.artifactId && a.artifactId.toLowerCase().includes(query)) ||
            (a.version && a.version.toLowerCase().includes(query))
        );
    }

    // 按 groupId:artifactId 聚合同名多版本
    const groupedMap = {};
    matched.forEach(a => {
        const key = a.groupId + ':' + a.artifactId;
        if (!groupedMap[key]) {
            groupedMap[key] = { coord: key, groupId: a.groupId, artifactId: a.artifactId, versions: [], totalSize: 0, totalFileCount: 0 };
        }
        groupedMap[key].versions.push(a);
        groupedMap[key].totalSize += (a.size || 0);
        groupedMap[key].totalFileCount += 1; // 每个版本算1个逻辑文件
    });

    mavenFilteredGroups = Object.keys(groupedMap).map(key => {
        const g = groupedMap[key];
        g.versions.sort((a, b) => compareVersions(b.version, a.version));
        const sortedAsc = g.versions.map(v => v.version).sort(compareVersions);
        g.versionText = sortedAsc[0] === sortedAsc[sortedAsc.length - 1]
            ? sortedAsc[0] : sortedAsc[0] + '~' + sortedAsc[sortedAsc.length - 1];
        return g;
    }).sort((a, b) => a.coord.localeCompare(b.coord));

    mavenCurrentPage = 1;
    renderMavenPkgsPage();
}

function renderMavenPkgsPage() {
    const tbody = document.getElementById('maven-pkgs-tbody');
    if (!tbody) return;

    const total = mavenFilteredGroups.length;
    const totalPages = Math.max(1, Math.ceil(total / mavenPageSize));
    if (mavenCurrentPage > totalPages) mavenCurrentPage = totalPages;

    const startIdx = (mavenCurrentPage - 1) * mavenPageSize;
    const pageItems = mavenFilteredGroups.slice(startIdx, startIdx + mavenPageSize);

    let html = '';
    pageItems.forEach((g, idx) => {
        const rowClass = idx % 2 === 0 ? 'even-row' : 'odd-row';
        const verLinkHtml = `<span onclick="showMavenVersionsModal(event, '${escapeJs(g.coord)}')" style="color: var(--accent-hover); text-decoration: underline; cursor: pointer;" title="${window.t('maven_modal_versions_title', g.coord)}">${escapeHtml(g.versionText)}</span>`;
        html += `
            <tr class='item-row ${rowClass}' onclick='showMavenDetail(${startIdx + idx})' style='cursor: pointer; transition: background 0.12s;'>
                <td style='padding: 6px 10px; font-family: monospace; font-size: 0.8rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${g.groupId}'>${escapeHtml(g.groupId)}</td>
                <td style='padding: 6px 10px; font-weight: bold; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${g.artifactId}'>
                    <span style='color: #D22E2F; margin-right: 4px;'>🪶</span>${escapeHtml(g.artifactId)}
                </td>
                <td style='padding: 6px 10px; font-size: 0.85rem;'>${verLinkHtml}</td>
                <td style='padding: 6px 10px; text-align: center;'><span style='padding: 1px 6px; border-radius: 3px; font-size: 0.7rem; background: ${g.versions[0].packaging === 'pom' ? 'rgba(210,158,230,0.15)' : 'var(--bg-color)'}; color: inherit;'>${escapeHtml(g.versions[0].packaging)}</span></td>
                <td style='padding: 6px 10px; text-align: right; font-size: 0.85rem; color: var(--accent-hover);'>${formatBytes(g.totalSize)}</td>
            </tr>
        `;
    });
    tbody.innerHTML = html;

    // 分页信息
    const pageInfo = document.getElementById('maven-pagination-info');
    if (pageInfo) {
        const endIdx = Math.min(startIdx + pageItems.length, total);
        pageInfo.textContent = window.t('gradle_pagination_info', mavenCurrentPage, totalPages, total) + ` (当前显示 ${startIdx + 1} - ${endIdx})`;
    }
}

function changeMavenPageSize() {
    const sel = document.getElementById('maven-page-size');
    if (sel) { mavenPageSize = parseInt(sel.value) || 50; mavenCurrentPage = 1; renderMavenPkgsPage(); }
}

function mavenGoToPage(target) {
    const totalPages = Math.max(1, Math.ceil(mavenFilteredGroups.length / mavenPageSize));
    if (target === 'first') mavenCurrentPage = 1;
    else if (target === 'prev') mavenCurrentPage = Math.max(1, mavenCurrentPage - 1);
    else if (target === 'next') mavenCurrentPage = Math.min(totalPages, mavenCurrentPage + 1);
    else if (target === 'last') mavenCurrentPage = totalPages;
    renderMavenPkgsPage();
}

function showMavenDetail(index) {
    const group = mavenFilteredGroups[index];
    const pane = document.getElementById('maven-preview-body');
    if (!group || !pane) return;

    mavenDetailGroupIndex = index;
    mavenDetailActiveVersion = group.versions[0] ? group.versions[0].version : '';
    renderMavenDetailPane();
}

function switchMavenVersion(version) {
    mavenDetailActiveVersion = version;
    renderMavenDetailPane();
}

function renderMavenDetailPane() {
    const group = mavenFilteredGroups[mavenDetailGroupIndex];
    const pane = document.getElementById('maven-preview-body');
    if (!group || !pane) return;

    const activePkg = group.versions.find(v => v.version === mavenDetailActiveVersion) || group.versions[0];
    if (!activePkg) return;
    mavenDetailActiveVersion = activePkg.version;

    // 版本 pill 条
    let versionPillsHtml = '';
    if (group.versions.length > 1) {
        versionPillsHtml = `
            <div style='margin-bottom: 12px;'>
                <div style='font-size: 0.78rem; color: var(--text-muted); margin-bottom: 5px;'>${window.t('maven_ver_switch_hint')}</div>
                <div style='display: flex; flex-wrap: wrap; gap: 6px;'>
                    ${group.versions.map(v => {
                        const active = v.version === activePkg.version;
                        return `<span onclick='switchMavenVersion("${escapeJs(v.version)}")' style='font-family: monospace; font-size: 0.75rem; padding: 2px 8px; border-radius: 4px; cursor: pointer; border: 1px solid ${active ? '#D22E2F' : 'var(--border-color)'}; background: ${active ? 'rgba(210,34,242,0.15)' : 'var(--bg-color)'}; color: ${active ? '#D22E2F' : 'var(--text-muted)'}; font-weight: ${active ? 'bold' : 'normal'};'>v${escapeHtml(v.version)}</span>`;
                    }).join('')}
                </div>
            </div>
        `;
    }

    // 依赖标签
    const depKeys = activePkg.dependencies ? Object.keys(activePkg.dependencies) : [];
    let depsHtml = '';
    if (depKeys.length > 0) {
        depsHtml = `
            <div style='margin-top: 4px; margin-bottom: 12px;'>
                <div style='font-size: 0.82rem; font-weight: bold; color: var(--text-muted); margin-bottom: 6px;'>${window.t('maven_detail_sec_deps', depKeys.length)}</div>
                <div class='dep-tags-container'>
                    ${depKeys.slice(0, 20).map(k => {
                        const parts = k.split(':');
                        const gid = parts[0] || k;
                        const aid = parts[1] || '';
                        return `<span class='dep-tag-pill'><span class='dep-name' title='${escapeHtml(k)}'>${escapeHtml(aid)}</span><span class='dep-ver'>${escapeHtml(activePkg.dependencies[k] || '')}</span></span>`;
                    }).join('')}
                    ${depKeys.length > 20 ? `<span class='dep-tag-pill' style='border-style: dashed;'>+${depKeys.length - 20} more</span>` : ''}
                </div>
            </div>
        `;
    } else {
        depsHtml = `<div style='color: var(--text-muted); font-size: 0.78rem;'>${window.t('maven_detail_no_deps')}</div>`;
    }

    pane.innerHTML = `
        <div style='padding-bottom: 12px; border-bottom: 1px solid var(--border-color); margin-bottom: 12px;'>
            <div style='font-size: 1.15rem; font-weight: bold; color: #D22E2F; display: flex; align-items: center; gap: 8px; margin-bottom: 4px; word-break: break-all;'>
                <span>🪶</span> ${escapeHtml(activePkg.artifactId)}
            </div>
            <div style='font-size: 0.85rem; font-family: monospace; color: var(--text-muted); margin-bottom: 4px; word-break: break-all;'>${escapeHtml(activePkg.groupId)} : v${escapeHtml(activePkg.version)} · ${window.t('maven_detail_version_count', group.versions.length)}</div>
        </div>

        ${versionPillsHtml}

        <!-- KMP 平台变体（含下载状态，动态加载） -->
        ${activePkg.isKmp ? `
        <div id='maven-kmp-block' data-path='${escapeHtml(activePkg.localPath)}' style='margin-bottom: 12px;'>
            <h4 style='margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--text-muted);'>🧬 Kotlin Multiplatform</h4>
            <div style='display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 6px;'>
                ${(activePkg.kmpPlatforms || []).map(p => `<span style='background: rgba(46, 204, 113, 0.15); color: #2ecc71; font-weight: 500; font-size: 0.75rem; padding: 2px 8px; border-radius: 4px;'>${escapeHtml(p)}</span>`).join('')}
            </div>
            <div id='maven-kmp-variants' style='color: var(--text-muted); font-size: 0.78rem;'>⏳ …</div>
        </div>
        ` : ''}

        <!-- 元信息卡片 -->
        <div style='background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 6px; padding: 10px; margin-bottom: 12px; font-size: 0.85rem; display: flex; flex-direction: column; gap: 6px;'>
            <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>Packaging</span><strong style='font-family:monospace;'>${activePkg.packaging}</strong></div>
            <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>Size</span><span>${formatBytes(activePkg.size || 0)}</span></div>
            <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>Files</span><span>1 构件
                ${activePkg.hasSources ? '<span style="color: #4CAF50; margin-left: 6px;">📄</span>' : ''}
                ${activePkg.hasJavadoc ? '<span style="color: #2196F3; margin-left: 6px;">📚</span>' : ''}
            </span></div>
            ${activePkg.license ? `<div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>License</span><span>${escapeHtml(activePkg.license)}</span></div>` : ''}
        </div>

        <!-- 描述 -->
        ${activePkg.description ? `
        <div style='margin-bottom: 12px;'>
            <h4 style='margin-top: 0; margin-bottom: 4px; font-size: 0.85rem; color: var(--text-muted);'>Description</h4>
            <div style='font-size: 0.8rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 8px; border-radius: 4px; max-height: 100px; overflow-y: auto; color: var(--text-muted); line-height: 1.4;'>${escapeHtml(activePkg.description)}</div>
        </div>
        ` : ''}

        ${depsHtml}

        <!-- Tab 切换引入代码 -->
        <div style='margin-top: auto; padding-top: 10px; border-top: 1px solid var(--border-color);'>
            <div style='font-size: 0.82rem; font-weight: bold; color: var(--text-muted); margin-bottom: 8px;'>📋 ${window.t('maven_quick_code_title')}</div>
            <div id='maven-code-tabs' style='display: flex; gap: 0; border-bottom: 1px solid var(--border-color); margin-bottom: 0;'>
                <button onclick='switchMavenCodeTab(this, "xml")' class='maven-code-tab active' data-tab='xml' style='padding: 5px 12px; font-size: 0.78rem; cursor: pointer; border: 1px solid transparent; border-bottom: none; background: transparent; color: var(--text-muted); border-radius: 4px 4px 0 0;'>XML</button>
                <button onclick='switchMavenCodeTab(this, "gradle")' class='maven-code-tab' data-tab='gradle' style='padding: 5px 12px; font-size: 0.78rem; cursor: pointer; border: 1px solid transparent; border-bottom: none; background: transparent; color: var(--text-muted); border-radius: 4px 4px 0 0;'>Gradle</button>
                <button onclick='switchMavenCodeTab(this, "kotlin")' class='maven-code-tab' data-tab='kotlin' style='padding: 5px 12px; font-size: 0.78rem; cursor: pointer; border: 1px solid transparent; border-bottom: none; background: transparent; color: var(--text-muted); border-radius: 4px 4px 0 0;'>Kotlin</button>
                <button onclick='switchMavenCodeTab(this, "mvn")' class='maven-code-tab' data-tab='mvn' style='padding: 5px 12px; font-size: 0.78rem; cursor: pointer; border: 1px solid transparent; border-bottom: none; background: transparent; color: var(--text-muted); border-radius: 4px 4px 0 0;'>CLI</button>
            </div>
            <div id='maven-code-display' style='background: var(--bg-color); border: 1px solid var(--border-color); border-top: none; border-radius: 0 0 6px 6px; padding: 0; position: relative;'>
                <pre id='maven-code-content' style='margin: 0; padding: 10px 36px 10px 12px; font-family: monospace; font-size: 0.8rem; overflow-x: auto; overflow-y: auto; white-space: pre; line-height: 1.5; height: 116px; box-sizing: border-box;'></pre>
                <button id='maven-copy-btn' onclick='copyMavenCode()' style='position: absolute; top: 6px; right: 6px; padding: 3px 8px; font-size: 0.72rem; cursor: pointer; border: 1px solid var(--border-color); border-radius: 4px; background: var(--container-bg); color: var(--text-muted);'>📋 ${window.t('btn_copy')}</button>
            </div>
            <div style='display: flex; gap: 6px; margin-top: 8px;'>
                <button onclick='openWithHost("${escapeJs(activePkg.localPath)}")' class='btn' style='flex: 1; padding: 6px; font-size: 0.85rem; cursor: pointer;'>📂 定位目录</button>
            </div>
        </div>

        <script type='application/json' id='maven-codes-data'>${JSON.stringify({
            xml: `<dependency>\n    <groupId>${activePkg.groupId}</groupId>\n    <artifactId>${activePkg.artifactId}</artifactId>\n    <version>${activePkg.version}</version>\n</dependency>`,
            gradle: `implementation '${activePkg.groupId}:${activePkg.artifactId}:${activePkg.version}'`,
            kotlin: `implementation("${activePkg.groupId}:${activePkg.artifactId}:${activePkg.version}")`,
            mvn: `mvn install:${activePkg.groupId}:${activePkg.artifactId}:${activePkg.version}`
        })}</script>
    `;
    
    // 初始化代码显示
    initMavenCodeDisplay();

    // KMP 变体下载状态动态加载
    loadMavenKmpVariants();
}

// 加载 KMP 平台变体的本地下载状态（根模块才发起查询；服务端离线读取 .module 声明 + 目录存在性）
function loadMavenKmpVariants() {
    const block = document.getElementById('maven-kmp-block');
    if (!block) return;

    const group = mavenFilteredGroups[mavenDetailGroupIndex];
    if (!group) return;
    const pkg = group.versions.find(v => v.version === mavenDetailActiveVersion) || group.versions[0];
    if (!pkg || !pkg.isKmp) return;

    const targetEl = document.getElementById('maven-kmp-variants');
    const reqPath = pkg.localPath;
    fetch('/api/maven/kmp-variants?path=' + encodeURIComponent(reqPath))
        .then(r => r.json())
        .then(d => {
            // 快速切换详情时丢弃过期响应（DOM 中已换成新 block）
            const cur = document.getElementById('maven-kmp-variants');
            if (!cur || document.getElementById('maven-kmp-block') !== block) return;

            if (!d.success) {
                cur.textContent = '—';
                return;
            }
            if (!d.items || d.items.length === 0) {
                cur.textContent = window.t('maven_kmp_no_variants');
                return;
            }
            const downloaded = d.items.filter(v => v.downloaded).length;
            const rows = d.items.map(v => {
                const dlColor = v.downloaded ? '#2ecc71' : 'var(--text-muted)';
                const sizeTxt = v.size > 0 ? formatBytes(v.size) : '';
                return `<div style='display: flex; align-items: center; gap: 8px; padding: 2px 0;'>
                    <span style='color: ${dlColor}; font-weight: 600; width: 16px; text-align: center;'>${v.downloaded ? '✅' : '⬜'}</span>
                    <span style='font-family: monospace; font-size: 0.76rem;'>${escapeHtml(v.name)}</span>
                    <span style='margin-left: auto; color: ${dlColor}; font-size: 0.72rem;'>${sizeTxt}</span>
                </div>`;
            }).join('');
            cur.innerHTML = `<div style='margin-bottom: 4px; font-size: 0.75rem;'>${downloaded}/${d.items.length}</div>${rows}`;
        })
        .catch(() => {
            const cur = document.getElementById('maven-kmp-variants');
            if (cur) cur.textContent = '—';
        });
}

// Maven 代码 Tab 切换相关变量与函数
let mavenCurrentCodeTab = 'xml';
let mavenCodeData = {};

function initMavenCodeDisplay() {
    const dataEl = document.getElementById('maven-codes-data');
    if (dataEl) {
        try { mavenCodeData = JSON.parse(dataEl.textContent); } catch(e) { mavenCodeData = {}; }
    }
    showMavenCodeTab('xml');
}

function switchMavenCodeTab(btn, tabName) {
    // 更新 tab 按钮样式
    document.querySelectorAll('.maven-code-tab').forEach(b => {
        b.classList.remove('active');
        b.style.color = 'var(--text-muted)';
        b.style.background = 'transparent';
        b.style.borderColor = 'transparent';
    });
    btn.classList.add('active');
    btn.style.color = '#D22E2F';
    btn.style.background = 'var(--bg-color)';
    btn.style.borderColor = 'var(--border-color)';
    btn.style.borderBottomColor = 'var(--bg-color)';
    
    showMavenCodeTab(tabName);
}

function showMavenCodeTab(tabName) {
    mavenCurrentCodeTab = tabName;
    const contentEl = document.getElementById('maven-code-content');
    if (contentEl && mavenCodeData[tabName]) {
        contentEl.textContent = mavenCodeData[tabName];
    }
}

function copyMavenCode() {
    const code = mavenCodeData[mavenCurrentCodeTab] || '';
    if (!code) return;
    
    navigator.clipboard.writeText(code).then(() => {
        const btn = document.getElementById('maven-copy-btn');
        if (btn) {
            const orig = btn.innerHTML;
            btn.innerHTML = '✅ ' + (window.t?.('copy_success') || '已复制');
            btn.style.color = '#4CAF50';
            setTimeout(() => { btn.innerHTML = orig; btn.style.color = ''; }, 1500);
        }
    }).catch(() => {
        // fallback
        const textarea = document.createElement('textarea');
        textarea.value = code;
        document.body.appendChild(textarea);
        textarea.select();
        document.execCommand('copy');
        document.body.removeChild(textarea);
    });
}

function showMavenVersionsModal(event, coord) {
    if (event) { event.preventDefault(); event.stopPropagation(); }
    const group = mavenFilteredGroups.find(g => g.coord === coord);
    if (!group) return;

    const modal = document.getElementById('maven-versions-modal');
    const title = document.getElementById('maven-versions-modal-title');
    const body = document.getElementById('maven-versions-modal-body');
    if (!modal || !title || !body) return;

    title.textContent = window.t('maven_modal_versions_title', group.coord);

    let html = `<table class='file-table' style='width: 100%; border-collapse: collapse; margin-top: 4px;'>
        <thead><tr style='background: var(--bg-color); border-bottom: 1px solid var(--border-color); text-align: left;'>
            <th style='padding: 6px 10px; font-size: 0.85rem;'>${window.t('maven_th_version')}</th>
            <th style='padding: 6px 10px; font-size: 0.85rem; text-align: center;'>${window.t('maven_th_packaging')}</th>
            <th style='padding: 6px 10px; font-size: 0.85rem;'>${window.t('npm_th_size')}</th>
            <th style='padding: 6px 10px; font-size: 0.85rem; text-align: right; width: 140px;'>操作</th>
        </tr></thead><tbody>`;

    group.versions.forEach(v => {
        html += `<tr style='border-bottom: 1px solid var(--border-color);'>
            <td style='padding: 8px 10px; font-weight: bold; font-family: monospace; font-size: 0.85rem;'>v${escapeHtml(v.version)}</td>
            <td style='padding: 8px 10px; text-align: center;'><span style='padding: 1px 6px; border-radius: 3px; font-size: 0.7rem; background: ${v.packaging === 'pom' ? 'rgba(210,158,230,0.15)' : 'var(--bg-color)'};'>${escapeHtml(v.packaging)}</span></td>
            <td style='padding: 8px 10px; font-size: 0.85rem; color: var(--accent-hover);'>${formatBytes(v.size || 0)}</td>
            <td style='padding: 8px 10px; text-align: right;'>
                <div style='display: inline-flex; gap: 6px;'>
                    <button onclick='copyToClipboard(this, "mvn install:${escapeJs(group.groupId)}:${escapeJs(group.artifactId)}@${escapeJs(v.version)}")' class='config-btn-sm' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${window.t('maven_copy_mvn_cmd')}'>📋</button>
                    <button onclick='openWithHost("${escapeJs(v.localPath)}")' class='config-btn-sm' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${window.t('btn_locate')}'>📂</button>
                    <button onclick='viewMavenVersionDetail("${escapeJs(group.coord)}", "${escapeJs(v.version)}")' class='config-btn-sm' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='${window.t('maven_btn_view_detail')}'>🔍</button>
                </div>
            </td>
        </tr>`;
    });
    html += '</tbody></table>';
    body.innerHTML = html;
    modal.style.display = 'flex';
}

function closeMavenVersionsModal() {
    const m = document.getElementById('maven-versions-modal');
    if (m) m.style.display = 'none';
}

function viewMavenVersionDetail(coord, version) {
    closeMavenVersionsModal();
    const idx = mavenFilteredGroups.findIndex(g => g.coord === coord);
    if (idx < 0) return;
    showMavenDetail(idx);
    if (version) switchMavenVersion(version);
}

function triggerMavenScan() {
    fetch('/api/maven/refresh', { method: 'POST' }).then(() => {
        pollMavenData();
    });
}

function pollMavenData() {
    fetchMavenData().then(data => {
        if (!data.scanning && data.artifacts && data.artifacts.length > 0) {
            onMavenSearchChange();
        } else {
            setTimeout(pollMavenData, 1500);
        }
    });
}

function openMavenRepo() {
    if (mavenRawData && mavenRawData.localRepoPath) {
        openWithHost(mavenRawData.localRepoPath);
    }
}

function showMavenConfigModal() {
    const modal = document.getElementById('maven-config-modal');
    const body = document.getElementById('maven-config-modal-body');
    if (!modal || !body) return;

    const d = mavenRawData || {};
    body.innerHTML = `
        <div style='display: grid; grid-template-columns: 1fr 1fr; gap: 16px;'>
            <div style='grid-column: span 2; padding: 12px; background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 6px;'>
                <h4 style='margin: 0 0 8px 0; color: #D22E2F; font-size: 0.95rem;'>${window.t('maven_cfg_sec_runtime')}</h4>
                <div style='display: flex; justify-content: space-between; margin-bottom: 6px;'><span style='color: var(--text-muted);'>${window.t('maven_cfg_maven_ver')}</span><strong style='font-family: monospace;'>${d.mavenVersion || '-'}</strong></div>
                <div style='display: flex; justify-content: space-between; margin-bottom: 6px;'><span style='color: var(--text-muted);'>${window.t('maven_cfg_java_ver')}</span><strong style='font-family: monospace;'>${d.javaVersion || '-'}</strong></div>
                <div style='display: flex; justify-content: space-between;'><span style='color: var(--text-muted);'>Maven Path</span><strong style='font-family: monospace; font-size: 0.8rem; max-width: 220px; overflow: hidden; text-overflow: ellipsis;'>${d.mavenPath || '-'}</strong></div>
            </div>
            <div style='padding: 12px; background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 6px;'>
                <h4 style='margin: 0 0 8px 0; color: #D22E2F; font-size: 0.95rem;'>${window.t('maven_cfg_sec_repo')}</h4>
                <div style='margin-bottom: 6px;'><span style='color: var(--text-muted);'>${window.t('maven_cfg_repo_path')}</span><br><strong style='font-family: monospace; font-size: 0.8rem; word-break: break-all; color: var(--accent-hover);'>${d.localRepoPath || '-'}</strong></div>
                <div style='margin-bottom: 6px;'><span style='color: var(--text-muted);'>${window.t('maven_stat_artifacts')}</span> <strong>${(d.artifacts || []).length}</strong></div>
                <div><span style='color: var(--text-muted);'>${window.t('maven_stat_size')}</span> <strong>${formatBytes(d.totalSize || 0)}</strong></div>
            </div>
            <div style='grid-column: span 2; padding: 12px; background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 6px;'>
                <h4 style='margin: 0 0 8px 0; color: #D22E2F; font-size: 0.95rem;'>${window.t('maven_cfg_sec_settings')}</h4>
                <div style='margin-bottom: 6px;'><span style='color: var(--text-muted);'>${window.t('maven_cfg_settings_path')}</span><br><strong style='font-family: monospace; font-size: 0.8rem; word-break: break-all; color: var(--accent-hover);'>${d.settingsPath || '-'}</strong></div>
                ${(() => { if (d.settingsContent) { return `<pre style='background: var(--container-bg); border: 1px solid var(--border-color); border-radius: 4px; padding: 10px; overflow-x: auto; font-size: 0.72rem; max-height: 180px; white-space: pre-wrap; word-break: break-all;'>${escapeHtml(d.settingsContent)}</pre>`; } else { return '<span style="color: var(--text-muted);">-</span>'; } })()}
            </div>
        </div>
    `;
    modal.style.display = 'flex';
}

function closeMavenConfigModal() {
    const m = document.getElementById('maven-config-modal');
    if (m) m.style.display = 'none';
}

function cleanMavenInvalid() {
    openMavenCleanModal();
}

// ==================== 无效缓存预览与清理弹窗 ====================

function openMavenCleanModal() {
    const modal = document.getElementById('maven-clean-modal');
    const body = document.getElementById('maven-clean-modal-body');
    if (!modal || !body) return;

    document.getElementById('maven-clean-modal-title').textContent = window.t('maven_clean_modal_title');
    document.getElementById('maven-clean-modal-hint').textContent = window.t('maven_clean_hint');
    body.innerHTML = `<div style='text-align: center; padding: 30px; color: var(--text-muted);'>⏳ ...</div>`;
    modal.style.display = 'flex';

    fetch('/api/maven/clean-preview')
        .then(r => r.json())
        .then(data => {
            if (!data.success) {
                body.innerHTML = `<div style='text-align: center; padding: 24px; color: #e74c3c;'>⚠ ${escapeHtml(data.message || 'Error')}</div>`;
                return;
            }
            if (!data.items || data.items.length === 0) {
                body.innerHTML = `<div style='text-align: center; padding: 30px; color: var(--text-muted);'>${window.t('maven_clean_empty')}</div>`;
                return;
            }
            const rMeta = window.t('maven_clean_reason_metadata');
            const rMiss = window.t('maven_clean_reason_missing');
            body.innerHTML = data.items.map(it => `
                <div style='display: flex; align-items: center; gap: 10px; padding: 8px 10px; border: 1px solid var(--border-color); border-radius: 6px; margin-bottom: 6px; background: var(--bg-color);'>
                    <input type='checkbox' class='maven-clean-check' data-path='${escapeHtml(it.path)}' checked onchange='updateMavenCleanStats()' style='flex-shrink: 0;'>
                    <div style='flex: 1; min-width: 0;'>
                        <div style='font-family: monospace; font-size: 0.8rem; font-weight: bold;'>${escapeHtml(it.coord)}</div>
                        <div style='font-family: monospace; font-size: 0.72rem; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${escapeHtml(it.path)}'>${escapeHtml(it.path)}</div>
                    </div>
                    <span style='font-size: 0.72rem; flex-shrink: 0; color: var(--text-muted);'>${formatBytes(it.size)}</span>
                    <span style='font-size: 0.72rem; padding: 2px 6px; border-radius: 3px; flex-shrink: 0; ${it.reason === 'missing_dir' ? 'background: rgba(231,76,60,0.15); color: #e74c3c;' : 'background: rgba(230,126,34,0.15); color: #e67e22;'}'>${it.reason === 'missing_dir' ? rMiss : rMeta}</span>
                </div>
            `).join('');
            const sa = document.getElementById('maven-clean-select-all');
            if (sa) sa.checked = true;
        })
        .catch(err => {
            body.innerHTML = `<div style='text-align: center; padding: 24px; color: #e74c3c;'>⚠ ${escapeHtml(String(err))}</div>`;
        });
}

function closeMavenCleanModal() {
    const m = document.getElementById('maven-clean-modal');
    if (m) m.style.display = 'none';
}

function toggleMavenCleanSelectAll(checked) {
    document.querySelectorAll('.maven-clean-check').forEach(cb => { cb.checked = checked; });
}

function collectSelectedMavenCleanPaths() {
    return Array.from(document.querySelectorAll('.maven-clean-check:checked')).map(cb => cb.getAttribute('data-path'));
}

function execMavenCleanSelected() {
    const paths = collectSelectedMavenCleanPaths();
    if (paths.length === 0) return;
    postMavenItems('clean-invalid', paths).then(() => {
        closeMavenCleanModal();
        refreshMavenAfterOps();
    });
}

function getMavenFailedArtifacts() {
    return (mavenRawData && mavenRawData.artifacts) ? mavenRawData.artifacts.filter(a => a.parseFailed) : [];
}

function showMavenFailedModal() {
    const modal = document.getElementById('maven-failed-modal');
    const body = document.getElementById('maven-failed-modal-body');
    if (!modal || !body) return;

    const failed = getMavenFailedArtifacts();
    if (failed.length === 0) {
        body.innerHTML = `<div style='text-align: center; padding: 30px; color: var(--text-muted);'>${window.t('maven_failed_empty')}</div>`;
    } else {
        const reasonText = window.t('maven_failed_reason');
        const filesHint = window.t('maven_failed_click_files');
        body.innerHTML = failed.map((a, i) => `
            <div style='display: flex; align-items: center; gap: 10px; padding: 8px 10px; border: 1px solid var(--border-color); border-radius: 6px; margin-bottom: 6px; background: var(--bg-color);'>
                <input type='checkbox' class='maven-failed-check' data-path='${escapeHtml(a.localPath)}' onchange='updateMavenFailedStats()' style='flex-shrink: 0;'>
                <div onclick="showMavenFailedItemFiles('${escapeJs(a.localPath)}')" style='flex: 1; min-width: 0; cursor: pointer;' title='${filesHint}'>
                    <div style='font-family: monospace; font-size: 0.8rem; font-weight: bold;'>${escapeHtml(a.groupId)}:${escapeHtml(a.artifactId)}:v${escapeHtml(a.version)}</div>
                    <div style='font-family: monospace; font-size: 0.72rem; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${escapeHtml(a.localPath)}'>${escapeHtml(a.localPath)}</div>
                </div>
                <span style='font-size: 0.72rem; padding: 2px 6px; border-radius: 3px; background: rgba(230,126,34,0.15); color: #e67e22; flex-shrink: 0;' title='${escapeHtml(a.failReason || '')}'>${reasonText}: ${escapeHtml(a.failReason || '-')}</span>
                <button onclick="openWithHost('${escapeJs(a.localPath)}')" class='pagination-btn' style='padding: 3px 8px; font-size: 0.75rem; flex-shrink: 0;' title='${window.t('maven_detail_local_path')}'>📂</button>
                <button onclick="retryMavenFailedItem('${escapeJs(a.localPath)}')" class='pagination-btn' style='padding: 3px 8px; font-size: 0.75rem; flex-shrink: 0;'>🔄</button>
                <button onclick="deleteMavenFailedItem('${escapeJs(a.localPath)}')" class='pagination-btn' style='padding: 3px 8px; font-size: 0.75rem; flex-shrink: 0;'>🗑</button>
            </div>
        `).join('');
    }
    const selectAll = document.getElementById('maven-failed-select-all');
    if (selectAll) selectAll.checked = false;
    const bar = document.getElementById('maven-failed-statbar');
    if (bar) bar.style.display = failed.length > 0 ? 'block' : 'none';
    updateMavenFailedStats();
    modal.style.display = 'flex';
}

/** 失败列表统计条：共 X 项 · 已选 Y 项（勾选/全选实时联动） */
function updateMavenFailedStats() {
    const all = document.querySelectorAll('.maven-failed-check').length;
    const sel = document.querySelectorAll('.maven-failed-check:checked').length;
    const totalEl = document.getElementById('maven-failed-stat-total');
    const selEl = document.getElementById('maven-failed-stat-sel');
    if (totalEl) totalEl.textContent = window.t('maven_failed_stat_fmt').replace('{0}', all);
    if (selEl) selEl.textContent = window.t('maven_failed_stat_sel_fmt').replace('{0}', sel);
}

function closeMavenFailedModal() {
    const m = document.getElementById('maven-failed-modal');
    if (m) m.style.display = 'none';
}

function toggleMavenFailedSelectAll(checked) {
    document.querySelectorAll('.maven-failed-check').forEach(cb => { cb.checked = checked; });
    updateMavenFailedStats();
}

function collectSelectedMavenFailedPaths() {
    return Array.from(document.querySelectorAll('.maven-failed-check:checked')).map(cb => cb.getAttribute('data-path'));
}

function postMavenItems(api, paths) {
    return fetch('/api/maven/' + api, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ paths: paths })
    }).then(r => r.json());
}

// ==================== 通用确认弹窗（全局公用组件） ====================

let appConfirmCtx = null;

/**
 * 显示通用确认弹窗
 * @param {Object} opts { title, messageHtml, confirmText, cancelText, danger, onConfirm }
 */
function showAppConfirm(opts) {
    const modal = document.getElementById('app-confirm-modal');
    if (!modal || !opts || typeof opts !== 'object') return;
    appConfirmCtx = opts;

    document.getElementById('app-confirm-title').textContent = opts.title || '';
    document.getElementById('app-confirm-body').innerHTML = opts.messageHtml || escapeHtml(opts.message || '');

    const okBtn = document.getElementById('app-confirm-ok-btn');
    okBtn.textContent = opts.confirmText || 'OK';
    if (opts.danger) {
        okBtn.style.background = 'transparent';
        okBtn.style.color = '#e74c3c';
        okBtn.style.borderColor = '#e74c3c';
    } else {
        okBtn.style.background = '';
        okBtn.style.color = '';
        okBtn.style.borderColor = '';
    }

    const cancelBtn = document.getElementById('app-confirm-cancel-btn');
    cancelBtn.textContent = opts.cancelText || 'Cancel';

    modal.style.display = 'flex';
}

/** 点击确认：关闭弹窗并执行调用方回调 */
function executeAppConfirm() {
    const ctx = appConfirmCtx;
    closeAppConfirm();
    if (ctx && typeof ctx.onConfirm === 'function') ctx.onConfirm();
}

/** 取消/× 关闭弹窗并清空上下文，不产生任何副作用 */
function closeAppConfirm() {
    appConfirmCtx = null;
    const modal = document.getElementById('app-confirm-modal');
    if (modal) modal.style.display = 'none';
}

async function refreshMavenAfterOps() {
    await fetchMavenData();
    onMavenSearchChange();
    if (document.getElementById('maven-failed-modal') && document.getElementById('maven-failed-modal').style.display === 'flex') {
        showMavenFailedModal();
    }
}

function retryMavenFailedPaths(paths) {
    if (!paths || paths.length === 0) return;
    postMavenItems('retry-items', paths).then(() => refreshMavenAfterOps());
}

function deleteMavenFailedPaths(paths) {
    if (!paths || paths.length === 0) return;
    postMavenItems('delete-items', paths).then(() => refreshMavenAfterOps());
}

function retryMavenFailedItem(path) { retryMavenFailedPaths([path]); }

/** 删除前统一走自定义确认弹窗（单项/批量共用） */
function confirmMavenDelete(paths) {
    if (!paths || paths.length === 0) return;
    const isBatch = paths.length > 1;
    const preview = paths.slice(0, 3).map(p =>
        `<div style='padding: 6px 9px; margin-top: 5px; background: rgba(255,255,255,0.05); border-radius: 4px; font-family: monospace; font-size: 0.78rem;'>${escapeHtml(p)}</div>`
    ).join('');
    const more = paths.length > 3 ? `<div style='margin-top: 6px; color: var(--text-muted); font-size: 0.78rem;'>… +${paths.length - 3}</div>` : '';
    showAppConfirm({
        title: '🗑 ' + window.t('maven_delete_confirm_title'),
        messageHtml:
            '<div style="margin-bottom: 8px;">' +
            (isBatch
                ? escapeHtml(window.t('maven_delete_confirm_hint')) + ` (<b style="color:#e74c3c;">${paths.length}</b>)`
                : escapeHtml(window.t('maven_delete_confirm_hint'))) +
            '</div>' + preview + more,
        confirmText: window.t('maven_delete_confirm_ok'),
        cancelText: window.t('maven_delete_confirm_cancel'),
        danger: true,
        onConfirm: function () { deleteMavenFailedPaths(paths); }
    });
}
function deleteMavenFailedItem(path) { confirmMavenDelete([path]); }

function retrySelectedMavenFailed() {
    const paths = collectSelectedMavenFailedPaths();
    if (paths.length === 0) return;
    retryMavenFailedPaths(paths);
}

function deleteSelectedMavenFailed() {
    const paths = collectSelectedMavenFailedPaths();
    if (paths.length === 0) return;
    confirmMavenDelete(paths);
}

function showMavenFailedItemFiles(path) {
    const modal = document.getElementById('maven-itemfiles-modal');
    const titleEl = document.getElementById('maven-itemfiles-title');
    const body = document.getElementById('maven-itemfiles-body');
    if (!modal || !body) return;

    if (titleEl) titleEl.textContent = path;
    body.innerHTML = `<div style='text-align: center; padding: 24px; color: var(--text-muted);'>⏳ ...</div>`;
    modal.style.display = 'flex';

    fetch('/api/maven/item-files?path=' + encodeURIComponent(path))
        .then(r => r.json())
        .then(data => {
            if (!data.success) {
                body.innerHTML = `<div style='text-align: center; padding: 24px; color: #e74c3c;'>⚠ ${escapeHtml(data.message || 'Error')}</div>`;
                return;
            }
            if (data.files.length === 0) {
                body.innerHTML = `<div style='text-align: center; padding: 24px; color: var(--text-muted);'>📭 ${window.t('maven_item_files_empty')}</div>`;
                return;
            }
            body.innerHTML = `
                <div style='font-size: 0.78rem; color: var(--text-muted); margin-bottom: 8px;'>${window.t('maven_item_files_count', data.count)}</div>
                ${data.files.map(f => `
                    <div style='display: flex; justify-content: space-between; align-items: center; gap: 10px; padding: 6px 10px; border-bottom: 1px solid var(--border-color); font-family: monospace; font-size: 0.78rem;'>
                        <span style='overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: ${f.name.startsWith('[dir]') ? '#f0c14b' : 'inherit'};' title='${escapeHtml(f.name)}'>${escapeHtml(f.name)}</span>
                        <span style='flex-shrink: 0; color: var(--accent-hover);'>${f.size > 0 ? formatBytes(f.size) : '-'}</span>
                    </div>
                `).join('')}
            `;
        })
        .catch(err => {
            body.innerHTML = `<div style='text-align: center; padding: 24px; color: #e74c3c;'>⚠ ${escapeHtml(err.message)}</div>`;
        });
}

function closeMavenItemFilesModal() {
    const m = document.getElementById('maven-itemfiles-modal');
    if (m) m.style.display = 'none';
}

// 初始化：页面加载时启动轮询
if (typeof currentView !== 'undefined' && currentView === 'maven') {
    pollMavenData();
}

function loadPnpmPkgFiles(indexFile) {
    const listContainer = document.getElementById('pnpm-pkg-files-list');
    const loading = document.getElementById('pnpm-files-loading');
    if (!listContainer || !indexFile) return;

    if (loading) loading.style.display = 'inline';
    fetch('/api/pnpm/pkg-files?indexFile=' + encodeURIComponent(indexFile))
        .then(res => res.json())
        .then(data => {
            if (loading) loading.style.display = 'none';
            if (data.success && data.rawIndex && data.rawIndex.files) {
                const files = data.rawIndex.files;
                const fileKeys = Object.keys(files);
                if (fileKeys.length === 0) {
                    listContainer.innerHTML = `<div style='color: var(--text-muted); font-size: 0.75rem; text-align: center; padding: 10px;'>📭 空清单</div>`;
                    return;
                }
                let html = '<div style="display: flex; flex-direction: column; gap: 4px; font-family: monospace; font-size: 0.72rem;">';
                fileKeys.forEach(fk => {
                    const f = files[fk];
                    html += `
                        <div style='display: flex; justify-content: space-between; gap: 6px; padding: 2px 4px; border-bottom: 1px dashed rgba(255,255,255,0.05);'>
                            <span style='color: var(--text-color); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;' title='${escapeHtml(fk)}'>${escapeHtml(fk)}</span>
                            <span style='color: var(--text-muted); flex-shrink: 0;'>${formatBytes(f.size || 0)}</span>
                        </div>
                    `;
                });
                html += '</div>';
                listContainer.innerHTML = html;
            } else {
                listContainer.innerHTML = `<div style='color: #cb3837; font-size: 0.75rem; text-align: center; padding: 10px;'>❌ ${escapeHtml(data.message || '加载失败')}</div>`;
            }
        })
        .catch(err => {
            if (loading) loading.style.display = 'none';
            listContainer.innerHTML = `<div style='color: #cb3837; font-size: 0.75rem; text-align: center; padding: 10px;'>❌ 网络错误: ${escapeHtml(err.message)}</div>`;
        });
}

function showPnpmConfigModal() {
    const modal = document.getElementById('pnpm-config-modal');
    const body = document.getElementById('pnpm-config-modal-body');
    if (!modal || !body) return;

    modal.style.display = 'flex';
    if (!pnpmFullData) {
        body.innerHTML = `<div style='text-align: center; padding: 30px; color: var(--text-muted);'>${window.t('pnpm_loading', '🔄 加载中...')}</div>`;
        return;
    }

    const d = pnpmFullData;
    const notFound = window.t('npm_cfg_not_found', '未检测到');

    let storesRowsHtml = '';
    if (d.stores && d.stores.length > 0) {
        storesRowsHtml = d.stores.map(s => `
            <tr>
                <td class='config-key'>${window.t('pnpm_cfg_store_path', 'Drive {0} 虚拟存储', s.driveLetter || '-')}</td>
                <td class='config-val'>
                    <span>${escapeHtml(s.storePath)}</span>
                    <span style='color: var(--text-muted); font-size: 0.78rem; margin-left: 8px;'>(${s.fileCount || 0} files, ${formatBytes(s.size || 0)})</span>
                </td>
                <td class='config-actions'>
                    <div class='config-btn-group'>
                        <button class='config-btn-sm' onclick='copyToClipboard(this, "${escapeJs(s.storePath)}")' title='${escapeHtml(window.t('npm_cfg_btn_copy_path', '复制路径'))}'>📋 ${window.t('npm_cfg_btn_copy_path', '复制路径')}</button>
                        <button class='config-btn-sm' onclick='openPnpmPath("${escapeJs(s.storePath)}")' title='${escapeHtml(window.t('npm_cfg_btn_open_dir', '打开目录'))}'>📂 ${window.t('npm_cfg_btn_open_dir', '打开目录')}</button>
                        <button class='config-btn-sm' onclick='openPnpmTerminal("${escapeJs(s.storePath)}")' title='${escapeHtml(window.t('npm_cfg_btn_terminal', '打开终端'))}'>💻 ${window.t('npm_cfg_btn_terminal', '打开终端')}</button>
                    </div>
                </td>
            </tr>
        `).join('');
    } else {
        storesRowsHtml = `
            <tr>
                <td class='config-key'>${window.t('pnpm_cfg_sec_stores', '💾 PNPM 磁盘虚拟存储库 (Stores)')}</td>
                <td class='config-val' style='color: var(--text-muted);'>${window.t('pnpm_no_stores', '未检测到磁盘上的 PNPM 存储库')}</td>
                <td class='config-actions'></td>
            </tr>
        `;
    }

    let configsHtml = '';
    if (d.npmrcConfigs && Object.keys(d.npmrcConfigs).length > 0) {
        configsHtml = `
            <table class='config-table' style='margin-bottom: 12px;'>
                ${Object.keys(d.npmrcConfigs).map(k => `
                    <tr>
                        <td class='config-key' style='width: 180px;'>${escapeHtml(k)}</td>
                        <td class='config-val' style='color: var(--accent-hover);'>${escapeHtml(d.npmrcConfigs[k])}</td>
                        <td class='config-actions'></td>
                    </tr>
                `).join('')}
            </table>
        `;
    } else {
        configsHtml = `<div style='color: var(--text-muted); font-size: 0.84rem; margin-bottom: 10px;'>${window.t('npm_cfg_npmrc_none', '未检测到 .npmrc 配置文件')}</div>`;
    }

    let rawNpmrcHtml = '';
    if (d.npmrcContent) {
        rawNpmrcHtml = `
            <div style='font-size: 0.82rem; font-weight: 500; margin-bottom: 4px; color: var(--text-muted);'>${window.t('npm_cfg_view_raw_npmrc', '查看原始 .npmrc 文件内容')}:</div>
            <div class='config-code-block'>${escapeHtml(d.npmrcContent)}</div>
        `;
    }

    body.innerHTML = `
        <div class='config-section'>
            <div class='config-section-title'>${window.t('pnpm_cfg_sec_runtime', '⚡ PNPM & Node.js 运行环境')}</div>
            <table class='config-table'>
                <tr>
                    <td class='config-key'>${window.t('pnpm_cfg_pnpm_ver', 'PNPM CLI 版本')}</td>
                    <td class='config-val'><strong style='color: #f69220;'>v${escapeHtml(d.pnpmVersion || notFound)}</strong></td>
                    <td class='config-actions'></td>
                </tr>
                ${renderConfigPathRow(window.t('pnpm_cfg_pnpm_path', 'PNPM CLI 执行文件'), d.pnpmPath, 'Pnpm')}
                <tr>
                    <td class='config-key'>${window.t('pnpm_cfg_node_ver', 'Node.js 运行时版本')}</td>
                    <td class='config-val'><strong style='color: var(--accent-color);'>${escapeHtml(d.nodeVersion || notFound)}</strong></td>
                    <td class='config-actions'></td>
                </tr>
                ${renderConfigPathRow(window.t('pnpm_cfg_node_path', 'Node.js 执行文件路径'), d.nodePath, 'Pnpm')}
            </table>
        </div>

        <div class='config-section'>
            <div class='config-section-title'>${window.t('pnpm_cfg_sec_paths', '📂 PNPM 全局目录与缓存路径')}</div>
            <table class='config-table'>
                ${renderConfigPathRow(window.t('pnpm_cfg_global_dir', '全局根目录 (global-dir)'), d.globalModulesDir, 'Pnpm')}
                ${renderConfigPathRow(window.t('pnpm_cfg_global_bin', '全局可执行文件目录 (global-bin-dir)'), d.globalBinDir, 'Pnpm')}
                ${renderConfigPathRow(window.t('pnpm_cfg_cache_dir', '临时下载缓存 (cache-dir)'), d.cacheDir, 'Pnpm')}
                ${renderConfigPathRow(window.t('pnpm_cfg_state_dir', '运行时状态目录 (state-dir)'), d.stateDir, 'Pnpm')}
            </table>
        </div>

        <div class='config-section'>
            <div class='config-section-title'>${window.t('pnpm_cfg_sec_stores', '💾 PNPM 磁盘虚拟存储库 (Stores)')}</div>
            <table class='config-table'>
                ${storesRowsHtml}
            </table>
        </div>

        <div class='config-section'>
            <div class='config-section-title'>${window.t('pnpm_cfg_sec_npmrc', '⚙️ .npmrc 配置文件与指令参数')}</div>
            <table class='config-table' style='margin-bottom: 10px;'>
                ${renderConfigPathRow(window.t('npm_cfg_npmrc_path', '.npmrc 文件路径'), d.npmrcPath, 'Pnpm')}
            </table>
            ${configsHtml}
            ${rawNpmrcHtml}
        </div>
    `;
}

function closePnpmConfigModal() {
    const modal = document.getElementById('pnpm-config-modal');
    if (modal) modal.style.display = 'none';
}

function openPnpmPath(pathStr) {
    if (!pathStr) return;
    fetch('/api/pnpm/open-path?path=' + encodeURIComponent(pathStr))
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open directory');
        })
        .catch(err => alert('Network error: ' + err.message));
}

function openPnpmTerminal(pathStr) {
    if (!pathStr) return;
    fetch('/api/pnpm/terminal?path=' + encodeURIComponent(pathStr))
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert(data.message || 'Failed to open terminal');
        })
        .catch(err => alert('Network error: ' + err.message));
}

// ====== 通用表格列宽拖拽调整（符合 column-resizer 规范）======
// 规范版本：v2.0 - 指针事件 + 独立覆盖层 + 双列联动 + 文字安全边界

(function() {
    'use strict';

    // 配置常量
    var HANDLE_WIDTH = 12;           // 手柄命中区宽度
    var MIN_COL_PCT = 0.08;          // 列最小宽度占比（兜底保护）
    var ABSOLUTE_MIN_WIDTH = 40;     // 绝对最小宽度 px
    var STORAGE_KEY_PREFIX = 'col_widths_';

    /**
     * 初始化表格列宽拖拽
     * @param {string} tableSelector - 表格选择器
     * @param {Object} [options] - 可选配置
     * @param {string} [options.storageKey] - 持久化存储 key
     */
    function initColumnResize(tableSelector, options) {
        options = options || {};
        var table = document.querySelector(tableSelector);
        if (!table) return;

        var thead = table.querySelector('thead');
        if (!thead) return;

        var ths = thead.querySelectorAll('th');
        if (ths.length < 2) return; // 至少需要 2 列

        // 创建独立覆盖层
        var overlay = document.createElement('div');
        overlay.className = 'col-resize-overlay';
        
        // 定位函数：使用 getBoundingClientRect 动态定位（含滚动偏移）
        function positionOverlay() {
            var headerRect = thead.getBoundingClientRect();
            // 使用 fixed 定位 + 视口坐标，避免滚动时位置偏移
            overlay.style.position = 'fixed';
            overlay.style.left = headerRect.left + 'px';
            overlay.style.top = headerRect.top + 'px';
            overlay.style.width = headerRect.width + 'px';
            overlay.style.height = headerRect.height + 'px';
        }
        
        document.body.appendChild(overlay);
        
        // 存储手柄引用和状态
        var handles = [];
        var isDragging = false;
        var dragState = null;

        // 创建手柄（数量 = 列数 - 1）
        for (var i = 0; i < ths.length - 1; i++) {
            createHandle(i, ths[i], ths[i + 1]);
        }

        // 响应窗口变化和滚动（确保位置始终正确）
        function onLayoutChange() {
            positionOverlay();
            repositionAllHandles();
        }
        
        window.addEventListener('resize', debounce(onLayoutChange, 100));
        window.addEventListener('scroll', debounce(onLayoutChange, 50), true); // 捕获阶段
        
        // ⚠️ 延迟初始定位：等待布局完全稳定（字体加载、异步内容等）
        setTimeout(onLayoutChange, 0);       // 当前宏任务结束后
        setTimeout(onLayoutChange, 50);      // 50ms 后微调
        setTimeout(onLayoutChange, 200);     // 200ms 后最终校正

        // 从持久化恢复列宽
        restoreWidths();

        /**
         * 创建拖拽手柄
         */
        function createHandle(index, leftTh, rightTh) {
            var handle = document.createElement('div');
            handle.className = 'col-resize-handle';
            handle.dataset.colIndex = index;
            
            overlay.appendChild(handle);
            handles.push({ el: handle, leftTh: leftTh, rightTh: rightTh });
            
            // 初始定位
            positionHandle(handle, leftTh);

            // === 核心事件：指针事件模型 ===
            handle.addEventListener('pointerdown', onPointerDown);
        }

        /**
         * 指针按下：启动拖拽
         */
        function onPointerDown(e) {
            // 仅响应主键/主指针
            if (e.button !== 0 && e.pointerType === 'mouse') return;
            
            var handle = e.currentTarget;
            var index = parseInt(handle.dataset.colIndex, 10);
            var handleData = handles[index];
            if (!handleData) return;

            e.preventDefault();  // 阻止默认行为
            e.stopPropagation(); // 防止冒泡到排序等交互

            // 启用指针捕获（硬件级锁定事件流）
            try {
                handle.setPointerCapture(e.pointerId);
            } catch (ex) {
                // 某些浏览器可能不支持，降级处理
                console.warn('setPointerCapture not supported:', ex);
            }

            // 固化全表宽度（防止自动伸缩补偿导致反向跳动）
            lockTableWidths();

            // 计算初始状态
            var leftRect = handleData.leftTh.getBoundingClientRect();
            var rightRect = handleData.rightTh.getBoundingClientRect();
            
            dragState = {
                handle: handle,
                index: index,
                startX: e.clientX,
                startLeftWidth: handleData.leftTh.offsetWidth,
                startRightWidth: handleData.rightTh.offsetWidth,
                totalWidth: leftRect.width + rightRect.width, // 恒等式分母
                leftMin: calcMinSafeWidth(handleData.leftTh),
                rightMin: calcMinSafeWidth(handleData.rightTh),
                leftTh: handleData.leftTh,
                rightTh: handleData.rightTh
            };

            // 兜底保护：防止最小宽度之和 >= 总宽度导致死锁
            if (dragState.leftMin + dragState.rightMin >= dragState.totalWidth) {
                var halfTotal = Math.floor(dragState.totalWidth * 0.45); // 各取 45%，留 10% 余量
                dragState.leftMin = Math.min(dragState.leftMin, halfTotal);
                dragState.rightMin = Math.min(dragState.rightMin, halfTotal);
            }

            isDragging = true;
            handle.classList.add('active');
            document.body.classList.add('col-resizing');

            // 绑定后续事件到手柄（非全局）
            handle.addEventListener('pointermove', onPointerMove);
            handle.addEventListener('pointerup', onPointerUp);
            handle.addEventListener('pointercancel', onPointerCancel);
        }

        /**
         * 指标移动：执行双列联动调整
         */
        function onPointerMove(e) {
            if (!isDragging || !dragState) return;

            e.preventDefault();

            var diff = e.clientX - dragState.startX;
            
            // 双列联动公式（核心算法）
            var newLeftWidth = dragState.startLeftWidth + diff;
            var maxLeftWidth = dragState.totalWidth - dragState.rightMin;
            
            // clamp 到合法区间
            newLeftWidth = Math.max(dragState.leftMin, Math.min(newLeftWidth, maxLeftWidth));
            var newRightWidth = dragState.totalWidth - newLeftWidth; // 守恒

            // 写入宽度（仅操作配对双列，零干扰）
            dragState.leftTh.style.width = newLeftWidth + 'px';
            dragState.rightTh.style.width = newRightWidth + 'px';

            // 重定位当前手柄及之后的所有手柄
            repositionHandlesFrom(dragState.index);
        }

        /**
         * 指针释放/取消：结束拖拽
         */
        function onPointerUp(e) {
            endDrag(e);
        }

        function onPointerCancel(e) {
            endDrag(e);
        }

        function endDrag(e) {
            if (!isDragging || !dragState) return;

            var handle = dragState.handle;
            var pointerId = e.pointerId;

            // 解除指针捕获
            try {
                handle.releasePointerCapture(pointerId);
            } catch (ex) {}

            // 清理状态
            handle.classList.remove('active');
            document.body.classList.remove('col-resizing');
            
            handle.removeEventListener('pointermove', onPointerMove);
            handle.removeEventListener('pointerup', onPointerUp);
            handle.removeEventListener('pointercancel', onPointerCancel);

            isDragging = false;

            // 持久化存储（仅在结束时写入）
            saveWidths();

            dragState = null;
        }

        /**
         * 固化全表所有列的显式宽度
         * ⚠️ 防止固定布局算法的自动伸缩补偿
         * 必须将所有列（含百分比）转为像素值，否则浏览器会重算
         */
        function lockTableWidths() {
            for (var i = 0; i < ths.length; i++) {
                var th = ths[i];
                // 强制转换为像素值（无论当前是 auto、px 还是 %）
                th.style.width = th.offsetWidth + 'px';
            }
        }

        /**
         * 计算列的最小安全宽度（基于文字内容测量）
         * ⚠️ 测量内联文字节点固有宽度，而非容器宽度
         */
        function calcMinSafeWidth(th) {
            // 方法1：测量文字内容实际宽度
            var textNode = getTextContent(th);
            if (textNode) {
                var span = document.createElement('span');
                span.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;font:inherit;padding:0;margin:0;border:0;';
                span.textContent = textNode;
                document.body.appendChild(span);
                var textWidth = span.offsetWidth;
                document.body.removeChild(span);

                // 加上 padding 和可能的图标/排序指示器
                var computedStyle = window.getComputedStyle(th);
                var paddingLeft = parseFloat(computedStyle.paddingLeft) || 0;
                var paddingRight = parseFloat(computedStyle.paddingRight) || 0;
                
                // 检查是否有排序指示器或额外元素
                var extraWidth = 20; // 预留空间给图标等
                
                return Math.max(ABSOLUTE_MIN_WIDTH, textWidth + paddingLeft + paddingRight + extraWidth);
            }

            // 降级：使用百分比计算
            var tableWidth = table.offsetWidth || 600;
            return Math.max(ABSOLUTE_MIN_WIDTH, tableWidth * MIN_COL_PCT);
        }

        /**
         * 获取表头文字内容（排除子元素）
         */
        function getTextContent(th) {
            // 优先获取 .th-label 或直接文字
            var labelEl = th.querySelector('.th-label');
            if (labelEl) return labelEl.textContent.trim();
            
            // 过滤掉手柄等子元素，只取文字节点
            var texts = [];
            for (var i = 0; i < th.childNodes.length; i++) {
                var node = th.childNodes[i];
                if (node.nodeType === Node.TEXT_NODE && node.textContent.trim()) {
                    texts.push(node.textContent.trim());
                }
            }
            return texts.join(' ') || th.textContent.trim() || '';
        }

        /**
         * 定位单个手柄到列边界
         */
        function positionHandle(handle, leftTh) {
            var rect = leftTh.getBoundingClientRect();
            var overlayRect = overlay.getBoundingClientRect();
            handle.style.left = (rect.right - overlayRect.left - HANDLE_WIDTH / 2) + 'px';
        }

        /**
         * 重定位从指定索引开始的所有手柄
         */
        function repositionHandlesFrom(fromIndex) {
            for (var i = fromIndex; i < handles.length; i++) {
                positionHandle(handles[i].el, handles[i].leftTh);
            }
        }

        /**
         * 重定位所有手柄
         */
        function repositionAllHandles() {
            for (var i = 0; i < handles.length; i++) {
                positionHandle(handles[i].el, handles[i].leftTh);
            }
        }

        /**
         * 持久化列宽
         */
        function saveWidths() {
            try {
                var widths = [];
                for (var i = 0; i < ths.length; i++) {
                    widths.push(ths[i].offsetWidth);
                }
                var key = options.storageKey || (STORAGE_KEY_PREFIX + tableSelector.replace(/[^a-zA-Z0-9]/g, '_'));
                localStorage.setItem(key, JSON.stringify(widths));
            } catch (ex) {}
        }

        /**
         * 从持久化恢复列宽（含自愈校验）
         */
        function restoreWidths() {
            try {
                var key = options.storageKey || (STORAGE_KEY_PREFIX + tableSelector.replace(/[^a-zA-Z0-9]/g, '_'));
                var stored = localStorage.getItem(key);
                if (!stored) return;

                var widths = JSON.parse(stored);
                // 自愈校验：长度匹配、每项为正数
                if (!Array.isArray(widths) || widths.length !== ths.length) {
                    localStorage.removeItem(key); // 脏数据丢弃
                    return;
                }

                var allValid = true;
                for (var i = 0; i < widths.length; i++) {
                    if (typeof widths[i] !== 'number' || widths[i] <= 0 || !isFinite(widths[i])) {
                        allValid = false;
                        break;
                    }
                }

                if (allValid) {
                    for (var j = 0; j < ths.length; j++) {
                        ths[j].style.width = widths[j] + 'px';
                    }
                    // 延迟重定位手柄（等待布局完成）
                    setTimeout(repositionAllHandles, 50);
                } else {
                    localStorage.removeItem(key);
                }
            } catch (ex) {
                // 解析失败则忽略
            }
        }

        // 防抖工具
        function debounce(fn, delay) {
            var timer = null;
            return function() {
                clearTimeout(timer);
                var args = arguments;
                var ctx = this;
                timer = setTimeout(function() {
                    fn.apply(ctx, args);
                }, delay);
            };
        }
    }

    // 暴露到全局
    window.initColumnResize = initColumnResize;

})();

// 页面加载后初始化所有数据表的列拖拽
document.addEventListener('DOMContentLoaded', function() {
    initColumnResize('#file-table', { storageKey: 'col_widths_file_table' });
    initColumnResize('#gradle-deps-table', { storageKey: 'col_widths_gradle_deps' });
    initColumnResize('#pnpm-pkgs-table', { storageKey: 'col_widths_pnpm_pkgs' });
    
    // Maven 表格
    var mavenTable = document.getElementById('maven-pkgs-tbody')?.closest('table');
    if (mavenTable && mavenTable.id) {
        initColumnResize('#' + mavenTable.id, { storageKey: 'col_widths_maven_pkgs' });
    }
});

