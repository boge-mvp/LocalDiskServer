// availableShells is injected globally in footer.html
function filterList() {
    var input = document.getElementById('search');
    var filter = input.value.toLowerCase();
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

let lastSelected = null;
let selectedRows = new Set();
let contextMenu = null;

document.addEventListener('DOMContentLoaded', () => {
    initCollapsibleSidebars();
    initProtocolSwitcher();
    
    if (typeof currentView !== 'undefined' && currentView === 'gradle') {
        initGradleDashboard();
        return;
    }

    initSelection();
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
});

function getSelectableItems() {
    return Array.from(document.querySelectorAll('.item-row, .drive-card, .fav-card'));
}

function initSelection() {
    const selectables = getSelectableItems();
    selectables.forEach(item => {
        item.addEventListener('dragstart', (e) => e.preventDefault());
        
        const isCard = item.classList.contains('drive-card') || item.classList.contains('fav-card') || item.classList.contains('card');
        
        item.addEventListener('click', (e) => {
            if (e.target.tagName === 'INPUT' || e.target.tagName === 'BUTTON') return;
            if (e.target.closest('.fav-star-btn')) return;
            
            if (isCard) {
                // 💡 For lobby cards, single-click directly opens/navigates them!
                return;
            }
            
            e.preventDefault();
            e.stopPropagation();
            
            handleItemSelection(item, e.ctrlKey, e.shiftKey);
        });

        item.addEventListener('dblclick', (e) => {
            if (isCard) return;
            const link = item.querySelector('a');
            if (link) {
                window.location.href = link.href;
            }
        });
    });

    document.addEventListener('click', (e) => {
        if (!e.target.closest('.item-row, .drive-card, .fav-card, .context-menu')) {
            clearAllSelections();
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
}

function deselectRow(item) {
    item.classList.remove('selected');
    selectedRows.delete(item);
    if (typeof updateLivePreview === 'function') updateLivePreview();
}

function clearAllSelections() {
    const selectables = getSelectableItems();
    selectables.forEach(item => item.classList.remove('selected'));
    selectedRows.clear();
    lastSelected = null;
    if (typeof updateLivePreview === 'function') updateLivePreview();
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
            items.push({ label: '📂 打开 (Enter)', action: 'open' });
            items.push({ label: '🎛️ 属性', action: 'properties' });
        } else if (isLobbyCard) {
            items.push({ label: '📂 打开 (Enter)', action: 'open' });
            items.push({ label: '⭐ 收藏/取消收藏', action: 'favorite' });
            items.push({ label: '🎛️ 属性', action: 'properties' });
        } else {
            items.push({ label: '📂 打开 (Enter)', action: 'open' });
            
            const submenuItems = [];
            if (!isDir) {
                submenuItems.push({ label: '🌐 浏览器文本直显', action: 'openWith_text' });
            }
            submenuItems.push({ label: '💻 宿主电脑默认程序', action: 'openWith_host' });
            submenuItems.push({ label: isDir ? '📂 网页进入目录' : '📥 标准物理下载', action: 'openWith_standard' });

            items.push({ 
                label: '⚡ 打开方式', 
                action: 'openWith', 
                submenu: submenuItems 
            });

            items.push({ label: '⭐ 收藏/取消收藏', action: 'favorite' });
            items.push({ label: '📝 重命名 (F2)', action: 'rename' });
            items.push({ label: '📋 复制 (Ctrl+C)', action: 'copy' });
            items.push({ label: '✂️ 剪切 (Ctrl+X)', action: 'cut' });
            items.push({ label: '🗑️ 删除 (Delete)', action: 'delete', danger: true });
            items.push({ label: '🎛️ 属性', action: 'properties' });
        }
    }
    const isLobby = typeof isLobbyPage !== 'undefined' && isLobbyPage;
    if (!isLobby) {
        items.push({ label: '📥 粘贴 (Ctrl+V)', action: 'paste' });
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
            const labelSuffix = index === 0 ? " (系统推荐)" : "";
            submenuShells.push({ 
                label: "🖥️ " + shell.name + labelSuffix, 
                action: "openTerminal_path",
                param: shell.exePath
            });
        });
    }
    items.push({ 
        label: '🖥️ 在终端中打开', 
        action: 'openTerminal',
        submenu: submenuShells 
    });

    items.push({ label: '🔄 刷新 (F5)', action: 'refresh' });

    const menuWidth = 155;

    items.forEach(cfg => {
        const el = document.createElement('div');
        el.className = 'context-menu-item' + (cfg.danger ? ' danger' : '');
        
        if (cfg.submenu) {
            el.className += ' has-submenu';
            el.innerHTML = `<span>${cfg.label}</span><span style="font-size: 0.65rem; color: var(--text-muted); margin-left: 8px;">▶</span>`;
            
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
            
            if (clientX + menuWidth + 175 > window.innerWidth) {
                subEl.classList.add('edge-left');
            }
        } else {
            el.innerText = cfg.label;
            el.addEventListener('click', () => {
                contextMenu.style.display = 'none';
                triggerAction(cfg.action);
            });
        }
        contextMenu.appendChild(el);
    });

    contextMenu.style.display = 'block';

    const actualWidth = contextMenu.offsetWidth || menuWidth;
    const actualHeight = contextMenu.offsetHeight || 220;

    let x = clientX;
    let y = clientY;

    if (x + actualWidth > window.innerWidth) {
        x = window.innerWidth - actualWidth - 5;
    }
    if (y + actualHeight > window.innerHeight) {
        y = window.innerHeight - actualHeight - 5;
    }

    contextMenu.style.left = x + 'px';
    contextMenu.style.top = y + 'px';
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
            const newName = prompt('输入新的名称:', oldName);
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
            if (confirm('确定要删除选中的项目吗？此操作不可逆！')) {
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
            alert('请先进入磁盘分区或文件夹内再进行粘贴。');
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
            .catch(err => alert("网络错误: " + err.message));
    }
}

function openWithHost(path) {
    fetch(`/api/file/open-host?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                alert("打开失败: " + data.message);
            }
        })
        .catch(err => alert("网络错误: " + err.message));
}

function closeProperties() {
    document.getElementById('properties-modal').style.display = 'none';
}

function showProperties() {
    const selectables = Array.from(selectedRows);
    if (selectables.length === 0) return;

    const paths = selectables.map(item => item.getAttribute('data-path')).join('|');
    const body = document.getElementById('properties-body');
    body.innerHTML = '<div style="text-align: center; padding: 20px;">🔄 正在计算属性...</div>';
    document.getElementById('properties-modal').style.display = 'flex';

    fetch(`/api/file/properties?paths=${encodeURIComponent(paths)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                body.innerHTML = `<div style="color: #e74c3c;">❌ 获取属性失败: ${data.message}</div>`;
                return;
            }

            let html = '<table class="properties-table">';
            if (!data.multi) {
                html += `<tr><td class="label">名称:</td><td class="val" style="font-weight: bold;">${data.name}</td></tr>`;
                html += `<tr><td class="label">类型:</td><td class="val">${data.isDir ? '文件夹' : (data.ext || '未知文件') + ' 文件'}</td></tr>`;
                html += `<tr><td class="label">位置:</td><td class="val">${data.folder || '根目录'}</td></tr>`;
                html += `<tr><td class="label">大小:</td><td class="val">${data.size} (${data.sizeBytes.toLocaleString()} 字节)</td></tr>`;
                if (data.isDir) {
                    html += `<tr><td class="label">包含:</td><td class="val">${data.files} 个文件, ${data.folders} 个文件夹</td></tr>`;
                }
                html += `<tr><td colspan="2"><div class="properties-divider"></div></td></tr>`;
                html += `<tr><td class="label">物理路径:</td><td class="val">${data.path}</td></tr>`;
                html += `<tr><td class="label">创建时间:</td><td class="val">${data.created}</td></tr>`;
                html += `<tr><td class="label">修改时间:</td><td class="val">${data.modified}</td></tr>`;
                if (data.attrs) {
                    html += `<tr><td class="label">属性:</td><td class="val">${data.attrs}</td></tr>`;
                }
            } else {
                html += `<tr><td class="label">对象数量:</td><td class="val" style="font-weight: bold;">选中了 ${data.count} 个项目</td></tr>`;
                html += `<tr><td class="label">包含:</td><td class="val">${data.files} 个文件, ${data.folders} 个文件夹</td></tr>`;
                html += `<tr><td class="label">位置:</td><td class="val">${data.folder}</td></tr>`;
                html += `<tr><td class="label">总大小:</td><td class="val">${data.size} (${data.sizeBytes.toLocaleString()} 字节)</td></tr>`;
            }
            html += '</table>';
            body.innerHTML = html;
        })
        .catch(err => {
            body.innerHTML = `<div style="color: #e74c3c;">❌ 获取属性失败: ${err.message}</div>`;
        });
}

function closeLogs() {
    document.getElementById('log-modal').style.display = 'none';
}

function clearLogs() {
    if (confirm('确定要清空所有运行日志吗？')) {
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
    container.innerHTML = '<div style="color: var(--text-muted); text-align: center; padding: 20px;">🔄 正在加载日志...</div>';
    document.getElementById('log-modal').style.display = 'flex';

    fetch('/api/logs')
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                container.innerHTML = `<div style="color: #f48771;">❌ 加载日志失败: ${data.message}</div>`;
                return;
            }
            if (data.logs.length === 0) {
                container.innerHTML = '<div style="color: #777; text-align: center; padding: 20px;">📭 暂无运行日志</div>';
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
            container.innerHTML = `<div style="color: #f48771;">❌ 加载日志发生错误: ${err.message}</div>`;
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

    container.innerHTML = `<div style='padding: 2px 10px; color: var(--text-muted); font-size: 0.8rem;'>🔄 正在载入...</div>`;

    fetch(`/api/explorer/tree?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success || data.folders.length === 0) {
                container.innerHTML = `<div style='padding: 2px 10px; color: var(--text-muted); font-size: 0.8rem;'>📭 空文件夹</div>`;
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
            container.innerHTML = `<div style='padding: 2px 10px; color: #e74c3c; font-size: 0.8rem;'>❌ 加载失败</div>`;
        });
}

// Live Preview Update Logic
function updateLivePreview() {
    const preview = document.getElementById('preview-pane');
    if (!preview) return;

    const content = document.getElementById('preview-content');
    const selectables = Array.from(selectedRows);

    if (selectables.length === 0) {
        content.innerHTML = `<div style='color: var(--text-muted); font-size: 0.9rem; padding-top: 40px;'>🔍 未选择任何项目</div>`;
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
            <div style='font-weight: bold; font-size: 1rem;'>已选择多个对象</div>
            <div class='preview-meta' style='margin-top: 15px;'>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>对象总数:</span>
                    <span class='preview-meta-value'>${selectables.length} 个项目</span>
                </div>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>文件数:</span>
                    <span class='preview-meta-value'>${filesCount} 个文件</span>
                </div>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>文件夹数:</span>
                    <span class='preview-meta-value'>${dirsCount} 个文件夹</span>
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

    content.innerHTML = `<div style='text-align: center; padding: 20px;'>🔄 正在载入预览...</div>`;

    if (type === 'dir') {
        content.innerHTML = `
            <div style='font-size: 3.5rem; margin-bottom: 5px;'>📁</div>
            <div style='font-weight: bold; font-size: 0.95rem; word-break: break-all;'>${escapeHtml(displayName)}</div>
            <div class='preview-meta' style='margin-top: 15px;'>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>类型:</span>
                    <span class='preview-meta-value'>文件夹</span>
                </div>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>修改时间:</span>
                    <span class='preview-meta-value'>${modifiedTime}</span>
                </div>
                <div class='preview-meta-row'>
                    <span class='preview-meta-label'>收藏状态:</span>
                    <span class='preview-meta-value'>${isFav ? '★ 已收藏' : '未收藏'}</span>
                </div>
            </div>
            <div style='margin-top: 15px; font-size: 0.75rem; color: var(--text-muted); word-break: break-all; text-align: left; width: 100%; border-top: 1px solid var(--border-color); padding-top: 10px;'>
                <strong>物理路径:</strong><br>${escapeHtml(path)}
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
                <img class='preview-thumbnail' src='${webLink}' alt='预览' onerror="this.src='/favicon.ico';">
                <div style='font-weight: bold; font-size: 0.9rem; word-break: break-all; margin-top: 10px;'>${escapeHtml(displayName)}</div>
                <div class='preview-meta' style='margin-top: 10px;'>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>类型:</span>
                        <span class='preview-meta-value'>${ext.toUpperCase()} 图片</span>
                    </div>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>大小:</span>
                        <span class='preview-meta-value'>${sizeText}</span>
                    </div>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>修改时间:</span>
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
                        <span class='preview-meta-label'>类型:</span>
                        <span class='preview-meta-value'>音频文件</span>
                    </div>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>大小:</span>
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
                        <span class='preview-meta-label'>类型:</span>
                        <span class='preview-meta-value'>视频文件</span>
                    </div>
                    <div class='preview-meta-row'>
                        <span class='preview-meta-label'>大小:</span>
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
                                    <span class='preview-meta-label'>类型:</span>
                                    <span class='preview-meta-value'>${ext.toUpperCase()} 文本</span>
                                </div>
                                <div class='preview-meta-row'>
                                    <span class='preview-meta-label'>大小:</span>
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
                <span class='preview-meta-label'>类型:</span>
                <span class='preview-meta-value'>${ext.toUpperCase() || '未知'} 文件</span>
            </div>
            <div class='preview-meta-row'>
                <span class='preview-meta-label'>大小:</span>
                <span class='preview-meta-value'>${sizeText}</span>
            </div>
            <div class='preview-meta-row'>
                <span class='preview-meta-label'>修改时间:</span>
                <span class='preview-meta-value'>${modifiedTime}</span>
            </div>
        </div>
        <div style='margin-top: 15px; font-size: 0.75rem; color: var(--text-muted); word-break: break-all; text-align: left; width: 100%; border-top: 1px solid var(--border-color); padding-top: 10px;'>
            <strong>物理路径:</strong><br>${escapeHtml(path)}
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
                    alert('路径不存在或无权访问，请检查拼写并重试！');
                }
            })
            .catch(() => {
                alert('校验路径时发生 network 错误！');
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
                document.getElementById('gradle-stat-home').innerHTML = `<span style='color: var(--text-muted); font-size: 0.8rem;'>未找到有效根路径</span>`;
                document.getElementById('gradle-wrappers-grid').innerHTML = `<div style='padding: 15px; color: #e74c3c; text-align: center; grid-column: 1/-1;'>❌ ${data.message}</div>`;
                document.getElementById('gradle-deps-tbody').innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: #e74c3c;'>❌ ${data.message}</td></tr>`;
                return;
            }
            
            const scanBtn = document.getElementById('gradle-refresh-btn');
            if (data.isScanning) {
                if (scanBtn) {
                    scanBtn.innerText = '🔄 扫描中...';
                    scanBtn.disabled = true;
                    scanBtn.style.opacity = '0.6';
                    scanBtn.style.cursor = 'not-allowed';
                }
                document.getElementById('gradle-stat-count').innerHTML = `<span style='color: var(--text-muted); font-size: 0.85rem;'>⏳ 扫描中...</span>`;
                document.getElementById('gradle-stat-size').innerHTML = `<span style='color: var(--text-muted); font-size: 0.85rem;'>⏳ 扫描中...</span>`;
                document.getElementById('gradle-stat-kmp').innerHTML = `<span style='color: var(--text-muted); font-size: 0.85rem;'>⏳ 扫描中...</span>`;
                
                if (!window.gradlePollTimer) {
                    window.gradlePollTimer = setInterval(loadGradleInfo, 2000);
                }
            } else {
                if (scanBtn) {
                    scanBtn.innerText = '🔄 重新扫描';
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
                grid.innerHTML = `<div style='padding: 15px; color: var(--text-muted); text-align: center; grid-column: 1/-1;'>📭 未检测到本地已解压的 Gradle Wrapper 分发包</div>`;
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
            console.error('获取 Gradle 摘要失败:', err);
        });
}

function triggerGradleScan() {
    const btn = document.getElementById('gradle-refresh-btn');
    if (btn && btn.disabled) return;
    
    fetch('/api/gradle/refresh')
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert('🔄 已在后台启动新的依赖库与 wrappers 扫描线程！');
                loadGradleInfo();
            } else {
                alert('❌ 触发扫描失败: ' + data.message);
            }
        })
        .catch(err => {
            alert('❌ 触发扫描失败: ' + err.message);
        });
}

function openInExplorer(path) {
    fetch(`/api/file/open-host?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) alert('❌ 打开目录失败: ' + data.message);
        })
        .catch(err => {
            alert('❌ 打开目录失败: ' + err.message);
        });
}

function deleteWrapper(path, version) {
    if (!confirm(`⚠️ 确定要物理清理已解压的 Gradle Wrapper 分发包：Gradle ${version} 吗？\n\n路径：${path}\n\n此操作将彻底删除此版本的物理文件夹。您确定要执行吗？`)) {
        return;
    }
    fetch(`/api/gradle/delete-wrapper?path=${encodeURIComponent(path)}`)
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert('✅ 该 Gradle Wrapper 分发包已成功物理清理！');
                loadGradleInfo();
            } else {
                alert('❌ 清理失败: ' + data.message);
            }
        })
        .catch(err => {
            alert('❌ 清理失败: ' + err.message);
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
    
    content.innerHTML = '<div style="text-align: center; padding: 20px; color: var(--text-muted);">🔄 正在获取 Wrapper 详情...</div>';

    fetch(`/api/gradle/wrapper-detail?version=${encodeURIComponent(version)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                content.innerHTML = `<div style="color: #e74c3c; padding: 15px;">❌ 错误: ${data.message}</div>`;
                return;
            }

            let subfoldersHtml = '';
            if (data.subfolders.length === 0) {
                subfoldersHtml = '<span style="color: var(--text-muted); font-size: 0.8rem;">空</span>';
            } else {
                subfoldersHtml = data.subfolders.map(f => `<span style="background: rgba(41, 128, 185, 0.1); border: 1px solid rgba(41, 128, 185, 0.3); color: var(--accent-hover); font-weight: 500; font-size: 0.75rem; padding: 2px 6px; border-radius: 4px; display: inline-block;">${escapeHtml(f)}</span>`).join(' ');
            }

            content.innerHTML = `
                <div style="display: flex; align-items: center; gap: 10px; margin-bottom: 12px;">
                    <span style="font-size: 2.2rem;">☕</span>
                    <div>
                        <div style="font-weight: bold; font-size: 1.15rem; color: var(--accent-hover);">Gradle ${escapeHtml(data.version)}</div>
                        <div style="font-size: 0.75rem; color: var(--text-muted); margin-top: 2px;">Wrappers 本地分包</div>
                    </div>
                </div>
                
                <div style="background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 6px; padding: 10px; margin-bottom: 15px; font-size: 0.85rem; display: flex; flex-direction: column; gap: 6px;">
                    <div style="display: flex; justify-content: space-between;"><span style="color:var(--text-muted);">分包体积:</span><strong>${data.size}</strong></div>
                    <div style="display: flex; justify-content: space-between;"><span style="color:var(--text-muted);">文件总数:</span><span>${data.fileCount.toLocaleString()} 个</span></div>
                    <div style="display: flex; justify-content: space-between;"><span style="color:var(--text-muted);">压缩文件:</span><span>${escapeHtml(data.zipFile)} (${data.zipExists ? '✅ 已下载' : '❌ 缺失'})</span></div>
                </div>

                <div style="margin-bottom: 15px;">
                    <h4 style="margin-top: 0; margin-bottom: 4px; font-size: 0.85rem; color: var(--text-muted);">📂 解压物理路径 (点击复制):</h4>
                    <div style="font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 8px; border-radius: 4px; overflow-x: auto; white-space: nowrap; cursor: pointer; text-decoration: underline; width: 100%; max-width: 100%; box-sizing: border-box;" onclick="copyToClipboard(this, '${escapeJs(data.path)}' )" title="点击复制路径">${escapeHtml(data.path)}</div>
                </div>

                <div style="margin-bottom: 15px;">
                    <h4 style="margin-top: 0; margin-bottom: 4px; font-size: 0.85rem; color: var(--text-muted);">💾 缓存哈希目录:</h4>
                    <div style="font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 8px; border-radius: 4px; color: var(--text-muted); word-break: break-all;">${escapeHtml(data.hashFolder)}</div>
                </div>

                <div style="margin-bottom: 15px; display: flex; flex-direction: column; min-height: 100px;">
                    <h4 style="margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--text-muted);">🌱 解压后直接下级目录:</h4>
                    <div style="overflow-y: auto; background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 4px; padding: 8px; max-height: 140px; display: flex; flex-wrap: wrap; gap: 6px; align-content: flex-start;">
                        ${subfoldersHtml}
                    </div>
                </div>

                <div style="margin-top: 15px; padding-top: 10px; border-top: 1px solid var(--border-color); display: flex; flex-direction: column; gap: 8px;">
                    <button onclick="openInExplorer('${escapeJs(data.path)}' )" style="width: 100%; padding: 8px; border-radius: 4px; background: var(--border-color); border: 1px solid var(--border-color); color: var(--text-color); font-weight: bold; cursor: pointer;">📂 宿主资源管理器中定位</button>
                    <button onclick="deleteWrapper('${escapeJs(data.path)}' , '${escapeJs(data.version)}' )" style="width: 100%; padding: 8px; border-radius: 4px; background: #e74c3c; border: none; color: white; font-weight: bold; cursor: pointer;">🗑️ 物理安全清理 (不可逆)</button>
                </div>
            `;
        })
        .catch(err => {
            content.innerHTML = `<div style="color: #e74c3c; padding: 15px;">❌ 无法加载详情: ${err.message}</div>`;
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
        btn.innerText = '✓ 已复制';
        setTimeout(() => { btn.innerText = oldText; }, 2000);
    } else if (btn) {
        const oldTitle = btn.title;
        btn.title = '✓ 路径已复制！';
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
    tbody.innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: var(--text-muted);'>🔄 正在检索已缓存依赖...</td></tr>`;

    fetch(`/api/gradle/search?q=${encodeURIComponent(query)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                tbody.innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: #e74c3c;'>❌ ${data.message || '获取依赖失败'}</td></tr>`;
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
            if (title) title.innerText = `📦 已缓存的依赖库列表 (${gradleAllDeps.length} 个依赖库)`;

            renderGradleDepsPage();
        })
        .catch(err => {
            tbody.innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: #e74c3c;'>❌ 获取依赖失败: ${err.message}</td></tr>`;
        });
}

function renderGradleDepsPage() {
    const tbody = document.getElementById('gradle-deps-tbody');
    if (!tbody) return;
    tbody.innerHTML = '';

    const totalItems = gradleAllDeps.length;
    if (totalItems === 0) {
        tbody.innerHTML = `<tr><td colspan='4' style='padding: 20px; text-align: center; color: var(--text-muted);'>📭 没有匹配的已缓存依赖库</td></tr>`;
        const info = document.getElementById('pagination-info');
        if (info) info.innerText = '第 1 / 1 页 (共 0 条)';
        return;
    }

    const totalPages = Math.ceil(totalItems / gradlePageSize) || 1;
    if (gradleCurrentPage > totalPages) gradleCurrentPage = totalPages;
    if (gradleCurrentPage < 1) gradleCurrentPage = 1;

    const startIndex = (gradleCurrentPage - 1) * gradlePageSize;
    const endIndex = Math.min(startIndex + gradlePageSize, totalItems);

    const pageItems = gradleAllDeps.slice(startIndex, endIndex);

    const info = document.getElementById('pagination-info');
    if (info) info.innerText = `第 ${gradleCurrentPage} / ${totalPages} 页 (共 ${totalItems} 条，当前显示 ${startIndex + 1} - ${endIndex})`;

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

        const versionTextHtml = `<span class="version-link" onclick="showVersionsModal(event, '${escapeJs(item.group)}', '${escapeJs(item.artifact)}')" style="color: var(--accent-hover); text-decoration: underline; cursor: pointer;" title="点击查看并管理所有版本">${escapeHtml(item.versionText)}</span>`;

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
    pane.innerHTML = '<div style="text-align: center; padding: 40px; color: var(--text-muted);">🔄 正在分析并载入 POM 级联依赖...</div>';

    fetch(`/api/gradle/detail?group=${encodeURIComponent(group)}&name=${encodeURIComponent(artifact)}&version=${encodeURIComponent(version)}`)
        .then(res => res.json())
        .then(data => {
            if (!data.success) {
                pane.innerHTML = `<div style="color: #e74c3c; padding: 20px;">❌ 载入依赖失败: ${data.message}</div>`;
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
                    <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>坐标版本:</span><strong>${escapeHtml(data.version)}</strong></div>
                    <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>缓存大小:</span><span>${data.size}</span></div>
                    <div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>开源许可证:</span><span>${escapeHtml(data.license)}</span></div>
                    ${data.organization ? `<div style='display: flex; justify-content: space-between;'><span style='color:var(--text-muted);'>发布组织:</span><span>${escapeHtml(data.organization)}</span></div>` : ''}
                </div>
            `;

            if (data.isKmp) {
                html += `
                    <div style='margin-bottom: 15px;'>
                        <h4 style='margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--text-muted);'>🌐 Kotlin Multiplatform (KMP) 适配平台:</h4>
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
                        <h4 style='margin-top: 0; margin-bottom: 4px; font-size: 0.85rem; color: var(--text-muted);'>📝 库文件描述:</h4>
                        <div style='font-size: 0.8rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 8px; border-radius: 4px; max-height: 100px; overflow-y: auto; color: var(--text-muted); line-height: 1.4;'>${escapeHtml(data.description)}</div>
                    </div>
                `;
            }

            // Store dependencies and artifact name for full modal popup
            currentPreviewDeps = data.dependencies || [];
            currentPreviewArtifactName = data.artifact;

            html += `
                <div style='margin-bottom: 15px; flex: 1; display: flex; flex-direction: column; min-height: 120px;'>
                    <h4 onclick='showDepsListModal(event)' style='margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--accent-hover); text-decoration: underline; cursor: pointer;' title='点击查看完整依赖树列表'>🔗 POM 级联依赖集 (${data.dependencies.length} 个):</h4>
                    <div style='flex: 1; overflow-y: auto; background: var(--bg-color); border: 1px solid var(--border-color); border-radius: 4px; padding: 8px; max-height: 200px;'>
            `;
            if (data.dependencies.length === 0) {
                html += `<div style='font-size: 0.8rem; color: var(--text-muted); text-align: center; padding: 10px;'>无级联依赖依赖项</div>`;
            } else {
                data.dependencies.forEach(dep => {
                    html += `
                        <div onclick='showDepsListModal(event)' style='display: flex; align-items: center; justify-content: space-between; border-bottom: 1px solid var(--border-color); padding: 4px 0; font-size: 0.8rem; cursor: pointer; text-decoration: underline;' title='点击查看完整依赖树列表'>
                            <div style='min-width: 0; margin-right: 8px;' title='${escapeHtml(dep.group)}:${escapeHtml(dep.artifact)}:${escapeHtml(dep.version)}'>
                                <div style='font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;'>${escapeHtml(dep.artifact)}</div>
                                <div style='font-size: 0.7rem; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;'>${escapeHtml(dep.group)}:${escapeHtml(dep.version)}</div>
                            </div>
                            <div style='flex-shrink: 0;'>
                                ${dep.isDownloaded ? '<span style="color: #2ecc71; font-weight: bold;" title="已在本地缓存">✓ 已缓存</span>' : '<span style="color: #e67e22; font-weight: bold;" title="未下载到本地缓存">⚠ 未缓存</span>'}
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
                    <h4 style='margin-top: 0; margin-bottom: 6px; font-size: 0.85rem; color: var(--text-muted);'>📋 快速引入 Gradle 配置代码:</h4>
                    <div style='display: flex; flex-direction: column; gap: 8px;'>
                        <div>
                            <div style='display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;'><span style='font-size: 0.75rem; color: var(--text-muted);'>常规 JVM / Android 项目:</span><button onclick='copyToClipboard(this, "${escapeJs(data.implementationCode)}")' style='background: var(--border-color); border: none; font-size: 0.75rem; padding: 2px 8px; border-radius: 3px; cursor: pointer; color: var(--text-color);'>复制</button></div>
                            <div style='font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 6px; border-radius: 4px; overflow-x: auto; white-space: nowrap; width: 100%; max-width: 100%; box-sizing: border-box;'>${escapeHtml(data.implementationCode)}</div>
                        </div>
                        <div>
                            <div style='display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;'><span style='font-size: 0.75rem; color: var(--text-muted);'>Kotlin KMP (Multiplatform) 项目:</span><button onclick='copyToClipboard(this, "${escapeJs(data.kmpCode)}")' style='background: var(--border-color); border: none; font-size: 0.75rem; padding: 2px 8px; border-radius: 3px; cursor: pointer; color: var(--text-color);'>复制</button></div>
                            <div style='font-family: monospace; font-size: 0.75rem; background: var(--bg-color); border: 1px solid var(--border-color); padding: 6px; border-radius: 4px; overflow-x: auto; white-space: nowrap; width: 100%; max-width: 100%; box-sizing: border-box;'>${escapeHtml(data.kmpCode)}</div>
                        </div>
                    </div>
                </div>
                
                <div style='margin-top: 15px; padding-top: 10px; border-top: 1px solid var(--border-color);'>
                    <button onclick='deleteGradleDep("${escapeJs(data.group)}", "${escapeJs(data.artifact)}", "${escapeJs(data.version)}")' style='width: 100%; padding: 8px; border-radius: 4px; background: #e74c3c; border: none; color: white; font-weight: bold; cursor: pointer; transition: background 0.15s;'>🗑️ 安全清空当前依赖库版本</button>
                </div>
            `;

            pane.innerHTML = html;
        })
        .catch(err => {
            pane.innerHTML = `<div style="color: #e74c3c; padding: 20px;">❌ 载入依赖失败: ${err.message}</div>`;
        });
}

function deleteGradleDep(group, name, version) {
    if (!confirm(`⚠️ 警告：您确定要物理清空已缓存的依赖库：\n\n${group}:${name}:${version}\n\n此操作将彻底删除此版本的物理文件夹。\n【安全设计】它只会安全清空该特定版本的文件夹，绝不递归删除任何级联的其它关联依赖库，防止破坏全局依赖。您确认要执行吗？`)) {
        return;
    }

    fetch(`/api/gradle/delete?group=${encodeURIComponent(group)}&name=${encodeURIComponent(name)}&version=${encodeURIComponent(version)}`)
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert('✅ 该依赖库版本已被安全物理清理！');
                document.getElementById('gradle-preview-body').innerHTML = `
                    <div style='text-align: center; color: var(--text-muted); margin-top: 40px; padding: 10px;'>
                        <span style='font-size: 2.5rem; display: block; margin-bottom: 12px;'>✓</span>
                        依赖库已删除成功。重新检索列表中。
                    </div>
                `;
                loadGradleInfo();
                onGradleSearchChange();
            } else {
                alert('❌ 删除依赖库失败: ' + data.message);
            }
        })
        .catch(err => {
            alert('❌ 删除依赖库失败: ' + err.message);
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
        title.innerHTML = `📦 <strong>${escapeHtml(artifact)}</strong> 的本地缓存版本列表`;
        
        let html = `
            <div style='margin-bottom: 12px; font-size: 0.85rem; color: var(--text-muted);'>
                分组 Group: <span style='font-family: monospace;'>${escapeHtml(group)}</span>
            </div>
            <table class='file-table' style='width: 100%; border-collapse: collapse; margin-top: 10px;'>
                <thead>
                    <tr style='background: var(--bg-color); border-bottom: 1px solid var(--border-color); text-align: left;'>
                        <th style='padding: 6px 10px; font-size: 0.85rem;'>版本号</th>
                        <th style='padding: 6px 10px; font-size: 0.85rem;'>大小</th>
                        <th style='padding: 6px 10px; text-align: right; font-size: 0.85rem;'>操作</th>
                    </tr>
                </thead>
                <tbody>
        `;
        
        const sorted = [...item.versions].sort((a, b) => compareVersions(b.version, a.version));
        
        sorted.forEach(v => {
            html += `
                <tr style='border-bottom: 1px solid var(--border-color);'>
                    <td style='padding: 8px 10px; font-weight: bold; font-family: monospace; font-size: 0.85rem;'>
                        <span onclick="showVersionFilesModal(event, '${escapeJs(group)}', '${escapeJs(artifact)}', '${escapeJs(v.version)}')" style='color: var(--accent-hover); text-decoration: underline; cursor: pointer;' title='点击查看版本缓存的所有文件详情'>${escapeHtml(v.version)}</span>
                    </td>
                    <td style='padding: 8px 10px; font-size: 0.85rem; color: var(--text-muted);'>${v.size}</td>
                    <td style='padding: 8px 10px; text-align: right; display: flex; justify-content: flex-end; gap: 6px;'>
                        <button onclick='copyToClipboard(this, "${escapeJs(v.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='复制绝对缓存路径'>📋 复制路径</button>
                        <button onclick='openInExplorer("${escapeJs(v.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='在系统文件夹中定位'>📂 定位</button>
                        <button onclick='deleteGradleDepFromModal("${escapeJs(group)}", "${escapeJs(artifact)}", "${escapeJs(v.version)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer; background: rgba(231, 76, 60, 0.1); border: 1px solid rgba(231, 76, 60, 0.3); color: #e74c3c;' title='安全物理清理此特定版本缓存'>🗑️ 删除</button>
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
        title.innerHTML = `🔗 <strong>${escapeHtml(currentPreviewArtifactName)}</strong> 的完整级联依赖集`;
        
        let html = `
            <div style='margin-bottom: 12px; font-size: 0.85rem; color: var(--text-muted);'>
                共包含 ${currentPreviewDeps.length} 个直接和级联依赖项：
            </div>
            <table class='file-table' style='width: 100%; border-collapse: collapse;'>
                <thead>
                    <tr style='background: var(--bg-color); border-bottom: 1px solid var(--border-color); text-align: left;'>
                        <th style='padding: 6px 10px; font-size: 0.85rem;'>依赖库坐标 (GroupId : ArtifactId : Version)</th>
                        <th style='padding: 6px 10px; font-size: 0.85rem; text-align: right; width: 100px;'>缓存状态</th>
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
                        ${dep.isDownloaded ? '<span style="color: #2ecc71; font-weight: bold;">✓ 已缓存</span>' : '<span style="color: #e67e22; font-weight: bold;">⚠ 未缓存</span>'}
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
        btn.innerHTML = '🌐 明文';
        btn.title = '一键切换到 HTTP 极速通道 (端口: ' + httpPort + ')';
    } else {
        btn.innerHTML = '🔒 密文';
        btn.title = '一键切换到 HTTPS 安全沙箱 (端口: ' + httpsPort + ')';
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
    if (!confirm(`⚠️ 警告：您确定要物理清空已缓存的依赖库：\n\n${group}:${name}:${version}\n\n此操作将彻底删除此版本的物理文件夹。\n【安全设计】它只会安全清空该特定版本的文件夹，绝不递归删除任何级联的其它关联依赖。您确认要执行吗？`)) {
        return;
    }

    fetch(`/api/gradle/delete?group=${encodeURIComponent(group)}&name=${encodeURIComponent(name)}&version=${encodeURIComponent(version)}`)
        .then(res => res.json())
        .then(data => {
            if (data.success) {
                alert('✅ 该依赖库版本已被安全物理清理！');
                loadGradleInfo();
                onGradleSearchChange();
                closeVersionsModal();
            } else {
                alert('❌ 删除依赖库失败: ' + data.message);
            }
        })
        .catch(err => {
            alert('❌ 删除依赖库失败: ' + err.message);
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
        body.innerHTML = '<div style="text-align: center; padding: 30px; color: var(--text-muted);">🔄 正在扫描并载入已下载文件列表...</div>';
        modal.style.display = 'flex';

        title.innerHTML = `📄 <strong>${escapeHtml(artifact)}:${escapeHtml(version)}</strong> 已下载文件列表`;

        fetch(`/api/gradle/version-files?group=${encodeURIComponent(group)}&name=${encodeURIComponent(artifact)}&version=${encodeURIComponent(version)}`)
            .then(res => res.json())
            .then(data => {
                if (!data.success) {
                    body.innerHTML = `<div style="color: #e74c3c; padding: 20px;">❌ 载入文件列表失败: ${data.message}</div>`;
                    return;
                }

                if (!data.files || data.files.length === 0) {
                    body.innerHTML = '<div style="color: var(--text-muted); text-align: center; padding: 20px;">📭 该版本目录内暂无缓存文件</div>';
                    return;
                }

                let html = `
                    <table class='file-table' style='width: 100%; border-collapse: collapse; margin-top: 5px;'>
                        <thead>
                            <tr style='background: var(--bg-color); border-bottom: 1px solid var(--border-color); text-align: left;'>
                                <th style='padding: 6px 10px; font-size: 0.85rem;'>文件名</th>
                                <th style='padding: 6px 10px; font-size: 0.85rem;'>大小</th>
                                <th style='padding: 6px 10px; text-align: right; font-size: 0.85rem;'>动作</th>
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
                                <button onclick='copyToClipboard(this, "${escapeJs(f.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='复制该文件的物理绝对路径'>📋 复制路径</button>
                                <button onclick='openInExplorer("${escapeJs(f.path)}")' class='btn' style='padding: 2px 6px; font-size: 0.75rem; cursor: pointer;' title='在系统文件夹中定位该文件'>📂 定位</button>
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
                body.innerHTML = `<div style="color: #e74c3c; padding: 20px;">❌ 载入文件列表失败: ${err.message}</div>`;
            });
    }
}

function closeFilesModal() {
    const modal = document.getElementById('files-modal');
    if (modal) modal.style.display = 'none';
}
