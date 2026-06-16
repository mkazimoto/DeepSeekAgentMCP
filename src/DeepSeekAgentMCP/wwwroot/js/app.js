// ============================================
// DeepSeek Agent MCP - Web Interface
// ============================================

class ChatApp {
    constructor() {
        this.isLoading = false;
        // Generate a unique session ID per tab/instance
        // This ensures each browser tab has its own isolated conversation
        this.sessionId = crypto.randomUUID ? crypto.randomUUID() : 
            'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
                const r = Math.random() * 16 | 0;
                return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
            });
        this.initElements();
        this.restoreWelcomeMessage();
        this.initEventListeners();
        this.initTheme();
        this.loadMcpStatus();
        this.autoResizeTextarea();
        this.initMermaid();
    }

    // --- DOM Elements ---
    initElements() {
        this.elements = {
            messagesContainer: document.getElementById('messages-container'),
            messageInput: document.getElementById('message-input'),
            sendBtn: document.getElementById('send-btn'),
            cancelBtn: document.getElementById('cancel-btn'),
            typingIndicator: document.getElementById('typing-indicator'),
            mcpStatus: document.getElementById('mcp-status'),
            modelName: document.getElementById('model-name'),
            welcomeMessage: document.getElementById('welcome-message'),
            sidebar: document.getElementById('sidebar'),
            menuToggle: document.getElementById('menu-toggle'),
            themeToggle: document.getElementById('theme-toggle'),
            statusIndicator: document.getElementById('status-indicator'),
        };
    }

    // --- Event Listeners ---
    initEventListeners() {
        // Send message
        this.elements.sendBtn.addEventListener('click', () => this.sendMessage());
        this.elements.cancelBtn.addEventListener('click', () => this.cancelRequest());
        this.elements.messageInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                this.sendMessage();
            }
        });
        this.elements.messageInput.addEventListener('input', () => {
            this.autoResizeTextarea();
            this.updateSendButton();
        });

        // Sidebar toggle
        this.elements.menuToggle.addEventListener('click', () => {
            this.elements.sidebar.classList.toggle('open');
        });

        // Close sidebar on outside click (mobile)
        document.addEventListener('click', (e) => {
            if (window.innerWidth <= 768 &&
                !this.elements.sidebar.contains(e.target) &&
                !this.elements.menuToggle.contains(e.target)) {
                this.elements.sidebar.classList.remove('open');
            }
        });

        // Command buttons
        document.querySelectorAll('.command-btn').forEach(btn => {
            btn.addEventListener('click', () => this.handleCommand(btn.dataset.command));
        });

        // Theme toggle
        this.elements.themeToggle.addEventListener('click', () => this.toggleTheme());

        // Copy code button delegation
        this.elements.messagesContainer.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-copy-code]');
            if (btn) {
                this.copyCodeContent(btn);
            }
        });

        // Mermaid diagram toolbar buttons
        this.elements.messagesContainer.addEventListener('click', (e) => {
            const fullscreenBtn = e.target.closest('[data-fullscreen-diagram]');
            if (fullscreenBtn) {
                const diagramId = fullscreenBtn.dataset.fullscreenDiagram;
                this.openDiagramFullscreen(diagramId);
                return;
            }

            const exportBtn = e.target.closest('[data-export-diagram]');
            if (exportBtn) {
                const diagramId = exportBtn.dataset.exportDiagram;
                this.exportDiagramAsSvg(diagramId);
                return;
            }
        });

        // Fullscreen modal close
        document.getElementById('mermaid-modal-close').addEventListener('click', () => this.closeDiagramFullscreen());
        document.getElementById('mermaid-modal-export').addEventListener('click', () => this.exportFullscreenDiagram());
        document.getElementById('mermaid-modal').addEventListener('click', (e) => {
            if (e.target.classList.contains('mermaid-modal-backdrop')) {
                this.closeDiagramFullscreen();
            }
        });
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                this.closeDiagramFullscreen();
            }
        });
    }

    // --- Theme ---
    initTheme() {
        const saved = localStorage.getItem('deepseek-theme');
        if (saved === 'dark' || saved === null) {
            document.documentElement.setAttribute('data-theme', 'dark');
            this.updateThemeIcons(true);
            if (saved === null) {
                localStorage.setItem('deepseek-theme', 'dark');
            }
        }
    }

    toggleTheme() {
        const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
        if (isDark) {
            document.documentElement.removeAttribute('data-theme');
            localStorage.setItem('deepseek-theme', 'light');
            this.updateThemeIcons(false);
        } else {
            document.documentElement.setAttribute('data-theme', 'dark');
            localStorage.setItem('deepseek-theme', 'dark');
            this.updateThemeIcons(true);
        }
        // Re-initialize Mermaid with correct theme
        this.initMermaid();
    }

    updateThemeIcons(isDark) {
        const sun = this.elements.themeToggle.querySelector('.theme-icon-sun');
        const moon = this.elements.themeToggle.querySelector('.theme-icon-moon');
        if (isDark) {
            sun.style.display = 'none';
            moon.style.display = 'block';
        } else {
            sun.style.display = 'block';
            moon.style.display = 'none';
        }
    }

    // --- MCP Status ---
    async loadMcpStatus() {
        try {
            const response = await fetch('/api/status');
            const data = await response.json();
            this.renderMcpStatus(data);
            if (data.model) {
                this.elements.modelName.textContent = data.model;
            }
        } catch (err) {
            this.elements.mcpStatus.innerHTML = `
                <div class="mcp-loading" style="color: #ef4444">
                    <span>Erro ao carregar status MCP</span>
                </div>
            `;
        }
    }

    renderMcpStatus(data) {
        if (!data.mcpServers || data.mcpServers.length === 0) {
            this.elements.mcpStatus.innerHTML = `
                <div style="font-size:13px;color:var(--text-tertiary);padding:8px 0;">
                    Nenhum servidor MCP conectado
                </div>
            `;
            return;
        }

        const html = data.mcpServers.map(server => `
            <div class="mcp-server-item">
                <span class="mcp-server-dot ${server.connected ? 'connected' : 'disconnected'}"></span>
                <span class="mcp-server-name">${this.escapeHtml(server.name)}</span>
                <span class="mcp-server-tools">${server.toolCount} ferramenta${server.toolCount !== 1 ? 's' : ''}</span>
            </div>
        `).join('');

        this.elements.mcpStatus.innerHTML = html;
    }

    // --- Send Message ---
    async sendMessage() {
        const text = this.elements.messageInput.value.trim();
        if (!text || this.isLoading) return;

        this.elements.messageInput.value = '';
        this.autoResizeTextarea();
        this.updateSendButton();

        // Remove welcome message
        if (this.elements.welcomeMessage) {
            this.elements.welcomeMessage.remove();
            this.elements.welcomeMessage = null;
        }

        // Add user message
        this.addMessage(text, 'user');

        // Show loading
        this.setLoading(true);

        try {
            const response = await fetch('/api/chat', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: text, sessionId: this.sessionId })
            });

            // If loading was already turned off (via cancel), don't process response
            if (!this.isLoading) return;

            if (!response.ok) {
                const errorData = await response.json().catch(() => null);
                throw new Error(errorData?.error || `Erro ${response.status}: ${response.statusText}`);
            }

            const data = await response.json();

            // If loading was already turned off (via cancel), don't process response
            if (!this.isLoading) return;

            // Hide loading
            this.setLoading(false);

            // Add agent response
            if (data.response) {
                this.addMessage(data.response, 'agent');
            }

            // Add tool calls if present
            if (data.toolCalls && data.toolCalls.length > 0) {
                data.toolCalls.forEach(tc => {
                    this.addToolCall(tc);
                });
            }

        } catch (err) {
            if (!this.isLoading) return;
            this.setLoading(false);
            this.addMessage(`Erro: ${err.message}`, 'error');
        }

        this.scrollToBottom();
    }

    // --- Add Message ---
    addMessage(text, type) {
        const container = this.elements.messagesContainer;

        const div = document.createElement('div');
        div.className = `message ${type === 'user' ? 'user-message' : type === 'error' ? 'error-message' : ''}`;

        const avatar = document.createElement('div');
        avatar.className = `message-avatar ${type === 'user' ? 'user-avatar' : 'agent-avatar'}`;

        if (type === 'user') {
            avatar.innerHTML = `
                <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
                    <circle cx="9" cy="6" r="3" stroke="currentColor" stroke-width="1.5"/>
                    <path d="M3 17C3 13.6863 5.68629 11 9 11C12.3137 11 15 13.6863 15 17" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
                </svg>
            `;
        } else {
            avatar.innerHTML = `
                <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
                    <rect width="18" height="18" rx="4" fill="currentColor" fill-opacity="0.2"/>
                    <path d="M4.5 9L7.5 12L13.5 6" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>
            `;
        }

        div.appendChild(avatar);

        const content = document.createElement('div');
        content.className = 'message-content';

        const sender = document.createElement('div');
        sender.className = 'message-sender';
        sender.textContent = type === 'user' ? 'Você' : type === 'error' ? 'Erro' : 'Assistente gerador de consulta SQL';
        content.appendChild(sender);

        const textDiv = document.createElement('div');
        textDiv.className = 'message-text';

        // Format markdown-like content
        textDiv.innerHTML = this.formatMessage(text);

        content.appendChild(textDiv);
        div.appendChild(content);

        container.appendChild(div);

        // Render Mermaid diagrams after element is in the DOM
        this.renderMermaidDiagrams(textDiv);

        this.scrollToBottom();
    }

    // --- Add Tool Call ---
    addToolCall(toolCall) {
        const container = this.elements.messagesContainer;

        const div = document.createElement('div');
        div.className = 'message tool-message';

        const content = document.createElement('div');
        content.className = 'message-content';

        const sender = document.createElement('div');
        sender.className = 'message-sender';
        sender.innerHTML = `🔧 ${this.escapeHtml(toolCall.name)}`;
        content.appendChild(sender);

        const textDiv = document.createElement('div');
        textDiv.className = 'message-text';
        try {
            const args = JSON.parse(toolCall.arguments);
            textDiv.textContent = `Argumentos: ${JSON.stringify(args, null, 2)}`;
        } catch {
            textDiv.textContent = this.escapeHtml(toolCall.arguments || 'Sem argumentos');
        }

        content.appendChild(textDiv);
        div.appendChild(content);

        container.appendChild(div);
        this.scrollToBottom();
    }

    // --- Check if text is Mermaid diagram content ---
    isMermaidContent(text) {
        if (!text) return false;
        const firstLine = text.split('\n')[0].trim();
        const mermaidKeywords = [
            'erDiagram',
            'graph ', 'graph\t',
            'flowchart ', 'flowchart\t',
            'sequenceDiagram',
            'classDiagram',
            'stateDiagram', 'stateDiagram-v2',
            'gantt',
            'pie',
            'journey',
            'gitgraph',
            'mindmap',
            'timeline',
            'zenuml',
            'sankey-beta',
            'xyChart', 'xychart',
            'block',
            'packet',
            'kanban',
            'architecture-beta',
        ];
        return mermaidKeywords.some(keyword => firstLine.startsWith(keyword));
    }

    // --- Format Message (Markdown renderer via marked) ---
    formatMessage(text) {
        if (!text) return '';

        // Configure marked for safe rendering
        if (typeof marked !== 'undefined') {
            marked.setOptions({
                breaks: true,
                gfm: true,
                headerIds: false,
                mangle: false,
            });

            let html = marked.parse(text);

            // Open external links in new tab
            html = html.replace(/<a href=/g, '<a target="_blank" rel="noopener" href=');

            // Step 1: Convert ALL code blocks that contain Mermaid content to mermaid divs
            // This handles both explicit ```mermaid and generic ``` blocks with diagram content
            html = html.replace(
                /<pre><code(?: class="([^"]*)")?>([\s\S]*?)<\/code><\/pre>/g,
                (match, lang, content) => {
                    const trimmed = content.trim();
                    const isExplicitMermaid = lang && lang.includes('language-mermaid');
                    const isDetectedMermaid = !isExplicitMermaid && this.isMermaidContent(trimmed);

                    if (isExplicitMermaid || isDetectedMermaid) {
                        const diagramId = 'd-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);
                        const fullscreenIcon = '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3H5a2 2 0 0 0-2 2v3"/><path d="M21 8V5a2 2 0 0 0-2-2h-3"/><path d="M16 21h3a2 2 0 0 0 2-2v-3"/><path d="M3 16v3a2 2 0 0 0 2 2h3"/></svg>';
                        const exportIcon = '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>';
                        return `<div class="mermaid-wrapper" data-diagram-id="${diagramId}">
                            <div class="mermaid-toolbar">
                                <button class="mermaid-btn mermaid-fullscreen-btn" data-fullscreen-diagram="${diagramId}" title="Visualizar em tela cheia">${fullscreenIcon} Tela cheia</button>
                                <button class="mermaid-btn mermaid-export-btn" data-export-diagram="${diagramId}" title="Exportar diagrama como SVG">${exportIcon} Exportar</button>
                            </div>
                            <div class="mermaid">${trimmed}</div>
                        </div>`;
                    }
                    // Non-mermaid: return unchanged for code block wrapping below
                    return match;
                }
            );

            // Step 2: Add copy button to remaining (non-mermaid) code blocks
            html = html.replace(
                /<pre><code(?: class="([^"]*)")?>/g,
                (match, lang) => {
                    const langLabel = lang ? lang.replace(/^language-/, '') : 'code';
                    const classAttr = lang ? ` class="${lang}"` : '';
                    return `<div class="code-block-wrapper"><div class="code-block-header"><span class="code-lang">${langLabel}</span><button class="copy-btn" data-copy-code title="Copiar código"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg><span>Copiar</span></button></div><pre><code${classAttr}>`;
                }
            );
            html = html.replace(/<\/code><\/pre>(?!\s*<\/div>)/g, `</code></pre></div>`);

            return html;
        }

        // Fallback: simple text-only rendering
        return `<p>${this.escapeHtml(text)}</p>`;
    }

    // --- Commands ---
    async handleCommand(command) {
        switch (command) {
            case '/clear':
                await this.clearConversation();
                break;
        }
    }

    async clearConversation() {
        try {
            await fetch('/api/clear', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: '', sessionId: this.sessionId })
            });
            this.elements.messagesContainer.innerHTML = '';
            this.restoreWelcomeMessage();
            this.showToast('🧹 Conversa limpa');
        } catch (err) {
            this.showToast('❌ Erro ao limpar conversa');
        }
    }

    // --- Initialize Mermaid ---
    initMermaid() {
        if (typeof mermaid === 'undefined') return;
        const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
        mermaid.initialize({
            startOnLoad: false,
            theme: isDark ? 'dark' : 'base',
            themeVariables: isDark ? {
                primaryColor: '#1e293b',
                primaryTextColor: '#f1f5f9',
                primaryBorderColor: '#334155',
                lineColor: '#818cf8',
                secondaryColor: '#0f172a',
                tertiaryColor: '#334155',
                background: '#000000',
                fontSize: '14px',
            } : {
                primaryColor: '#eef2ff',
                primaryTextColor: '#111827',
                primaryBorderColor: '#e5e7eb',
                lineColor: '#4F46E5',
                secondaryColor: '#ffffff',
                tertiaryColor: '#f3f4f6',
                background: '#000000',
                fontSize: '14px',
            },
        });
    }

    // --- Render Mermaid Diagrams ---
    async renderMermaidDiagrams(container) {
        if (typeof mermaid === 'undefined') return;
        const mermaidElements = container.querySelectorAll('.mermaid:not(.rendered)');
        if (mermaidElements.length === 0) return;

        for (const el of mermaidElements) {
            try {
                const diagramText = el.textContent || '';
                // Validate syntax first
                const valid = await mermaid.parse(diagramText, { suppressErrors: true });
                if (!valid) {
                    console.warn('Mermaid parse error for diagram, showing raw source');
                    el.classList.add('rendered', 'mermaid-error');
                    continue;
                }
                // Render using unique id
                const id = 'mermaid-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);
                const { svg } = await mermaid.render(id, diagramText);
                el.innerHTML = svg;
                el.classList.add('rendered');
            } catch (e) {
                console.warn('Mermaid render error:', e);
                el.classList.add('rendered', 'mermaid-error');
            }
        }
    }

    restoreWelcomeMessage() {
        const container = this.elements.messagesContainer;

        const div = document.createElement('div');
        div.className = 'message welcome-message';
        div.id = 'welcome-message';

        div.innerHTML = `
            <div class="message-avatar agent-avatar">
                <svg width="20" height="20" viewBox="0 0 20 20" fill="none">
                    <rect width="20" height="20" rx="5" fill="url(#avatar-gradient)"/>
                    <path d="M5 10L8 13L15 6" stroke="white" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
                    <defs>
                        <linearGradient id="avatar-gradient" x1="0" y1="0" x2="20" y2="20">
                            <stop offset="0%" stop-color="#4F46E5"/>
                            <stop offset="100%" stop-color="#7C3AED"/>
                        </linearGradient>
                    </defs>
                </svg>
            </div>
            <div class="message-content">
                <div class="message-sender">Assistente gerador de consulta SQL - ERP TOTVS RM</div>
                <div class="message-text">
                    <p>👋 Bem vindo! Sou o <strong>Assistente gerador de consulta SQL</strong>, especializado em gerar consultas SQL a partir das suas perguntas.</p>
                    <p>Exemplo:</p>
                    <p>* Listar todos os lançamentos financeiros a receber inadimplentes.</p>
                    <p>* Listar os funcionários ativos agrupados por faixa etária de 10 em 10 anos.</p>
                    <p>* Listar toda a hierarquia da tarefa codigo = '004' do projeto codigo = '0000' e coligada 1.</p>
                    <p>* Gere o diagrama de relacionamento das tabelas MTAREFA, MISM, MCMP, MRECCMP.</p>
                    <p>* Listar os top 10 clientes inadimplentes de vendas de imóveis.</p>
                    <p>* Listar os top 10 alunos do ensino superior inadimplentes.</p>
                </div>
            </div>
        `;

        container.appendChild(div);
        this.elements.welcomeMessage = div;
        this.scrollToBottom();
    }



    // --- Cancel Request ---
    async cancelRequest() {
        try {
            await fetch('/api/cancel', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: '', sessionId: this.sessionId })
            });
        } catch {
            // Ignore errors on cancel
        }
        this.setLoading(false);
        this.showToast('⏹️ Pedido cancelado');
    }

    // --- UI Helpers ---
    setLoading(loading) {
        this.isLoading = loading;
        this.elements.sendBtn.disabled = loading;
        this.elements.messageInput.disabled = loading;
        this.elements.typingIndicator.style.display = loading ? 'flex' : 'none';

        // Toggle between send and cancel buttons
        this.elements.sendBtn.style.display = loading ? 'none' : 'flex';
        this.elements.cancelBtn.style.display = loading ? 'flex' : 'none';

        if (loading) {
            this.elements.sendBtn.classList.add('loading');
        } else {
            this.elements.sendBtn.classList.remove('loading');
        }
    }

    updateSendButton() {
        this.elements.sendBtn.disabled = !this.elements.messageInput.value.trim() || this.isLoading;
    }

    autoResizeTextarea() {
        const textarea = this.elements.messageInput;
        textarea.style.height = 'auto';
        textarea.style.height = Math.min(textarea.scrollHeight, 200) + 'px';
    }

    scrollToBottom() {
        const container = this.elements.messagesContainer;
        requestAnimationFrame(() => {
            container.scrollTop = container.scrollHeight;
        });
    }

    showToast(message) {
        let toast = document.querySelector('.toast');
        if (!toast) {
            toast = document.createElement('div');
            toast.className = 'toast';
            document.body.appendChild(toast);
        }
        toast.textContent = message;
        toast.classList.add('show');
        clearTimeout(this._toastTimeout);
        this._toastTimeout = setTimeout(() => {
            toast.classList.remove('show');
        }, 3000);
    }

    // --- Copy Code ---
    async copyCodeContent(btn) {
        const wrapper = btn.closest('.code-block-wrapper');
        if (!wrapper) return;

        const code = wrapper.querySelector('code');
        if (!code) return;

        try {
            const text = code.textContent;
            await navigator.clipboard.writeText(text);

            const span = btn.querySelector('span');
            const original = span.textContent;
            span.textContent = 'Copiado!';
            btn.classList.add('copied');

            setTimeout(() => {
                span.textContent = original;
                btn.classList.remove('copied');
            }, 2000);
        } catch {
            // Fallback for older browsers
            try {
                const range = document.createRange();
                range.selectNodeContents(code);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                document.execCommand('copy');
                selection.removeAllRanges();

                const span = btn.querySelector('span');
                span.textContent = 'Copiado!';
                btn.classList.add('copied');
                setTimeout(() => {
                    span.textContent = 'Copiar';
                    btn.classList.remove('copied');
                }, 2000);
            } catch {
                this.showToast('❌ Erro ao copiar');
            }
        }
    }

    // --- Diagram Fullscreen ---
    openDiagramFullscreen(diagramId) {
        const wrapper = document.querySelector(`.mermaid-wrapper[data-diagram-id="${diagramId}"]`);
        if (!wrapper) return;

        const mermaidEl = wrapper.querySelector('.mermaid');
        if (!mermaidEl) return;

        const modal = document.getElementById('mermaid-modal');
        const modalBody = document.getElementById('mermaid-modal-body');

        // Reset zoom state
        this._zoomLevel = 1;
        this._zoomMax = 5;
        this._zoomMin = 0.1;

        // Clone the rendered SVG or the raw mermaid source
        const svg = mermaidEl.querySelector('svg');
        if (svg) {
            const clone = svg.cloneNode(true);
            // Ensure the SVG has proper attributes for standalone display
            clone.removeAttribute('width');
            clone.removeAttribute('height');
            clone.setAttribute('viewBox', svg.getAttribute('viewBox') || `0 0 ${svg.getAttribute('width') || 800} ${svg.getAttribute('height') || 600}`);
            clone.style.maxWidth = 'none';
            clone.style.maxHeight = 'none';
            clone.style.width = 'auto';
            clone.style.height = 'auto';

            // Create container for zoom/pan
            const container = document.createElement('div');
            container.className = 'mermaid-diagram-container';
            container.appendChild(clone);
            modalBody.innerHTML = '';
            modalBody.appendChild(container);
        } else {
            // Fallback: show raw mermaid source
            modalBody.innerHTML = `<pre style="white-space:pre-wrap;font-family:monospace;font-size:13px;color:var(--text-primary);background:var(--bg-secondary);padding:16px;border-radius:8px;max-width:100%;overflow:auto;">${this.escapeHtml(mermaidEl.textContent || '')}</pre>`;
        }

        modal.style.display = 'flex';
        document.body.style.overflow = 'hidden';
        this._fullscreenDiagramId = diagramId;

        // Re-flow to trigger animation
        requestAnimationFrame(() => {
            modal.classList.add('open');
            this.updateZoomLevelDisplay();
            this.setupZoomEvents(modal);
        });
    }

    closeDiagramFullscreen() {
        const modal = document.getElementById('mermaid-modal');
        modal.classList.remove('open');
        modal.style.display = 'none';
        document.body.style.overflow = '';
        this._fullscreenDiagramId = null;
        this._zoomLevel = 1;
        this.cleanupZoomEvents(modal);
    }

    // --- Zoom Controls ---
    setupZoomEvents(modal) {
        const modalBody = document.getElementById('mermaid-modal-body');
        const zoomIn = document.getElementById('zoom-in');
        const zoomOut = document.getElementById('zoom-out');
        const zoomReset = document.getElementById('zoom-reset');
        const container = modalBody.querySelector('.mermaid-diagram-container');

        if (!container) return;

        this._zoomInHandler = () => this.zoomDiagram(0.25);
        this._zoomOutHandler = () => this.zoomDiagram(-0.25);
        this._zoomResetHandler = () => this.resetZoom();
        this._wheelHandler = (e) => this.handleZoomWheel(e);
        this._dragStartHandler = (e) => this.handleDragStart(e);
        this._dragMoveHandler = (e) => this.handleDragMove(e);
        this._dragEndHandler = () => this.handleDragEnd();

        zoomIn.addEventListener('click', this._zoomInHandler);
        zoomOut.addEventListener('click', this._zoomOutHandler);
        zoomReset.addEventListener('click', this._zoomResetHandler);
        modalBody.addEventListener('wheel', this._wheelHandler, { passive: false });
        modalBody.addEventListener('mousedown', this._dragStartHandler);
        document.addEventListener('mousemove', this._dragMoveHandler);
        document.addEventListener('mouseup', this._dragEndHandler);
    }

    cleanupZoomEvents(modal) {
        const modalBody = document.getElementById('mermaid-modal-body');
        const zoomIn = document.getElementById('zoom-in');
        const zoomOut = document.getElementById('zoom-out');
        const zoomReset = document.getElementById('zoom-reset');

        if (this._zoomInHandler) zoomIn.removeEventListener('click', this._zoomInHandler);
        if (this._zoomOutHandler) zoomOut.removeEventListener('click', this._zoomOutHandler);
        if (this._zoomResetHandler) zoomReset.removeEventListener('click', this._zoomResetHandler);
        if (this._wheelHandler) modalBody.removeEventListener('wheel', this._wheelHandler);
        if (this._dragStartHandler) modalBody.removeEventListener('mousedown', this._dragStartHandler);
        if (this._dragMoveHandler) document.removeEventListener('mousemove', this._dragMoveHandler);
        if (this._dragEndHandler) document.removeEventListener('mouseup', this._dragEndHandler);

        this._zoomInHandler = null;
        this._zoomOutHandler = null;
        this._zoomResetHandler = null;
        this._wheelHandler = null;
        this._dragStartHandler = null;
        this._dragMoveHandler = null;
        this._dragEndHandler = null;
        this._isDragging = false;
    }

    zoomDiagram(delta) {
        const newZoom = Math.max(this._zoomMin, Math.min(this._zoomMax, this._zoomLevel + delta));
        if (newZoom === this._zoomLevel) return;
        this._zoomLevel = newZoom;
        this.applyZoom();
    }

    applyZoom() {
        const modalBody = document.getElementById('mermaid-modal-body');
        const container = modalBody.querySelector('.mermaid-diagram-container');
        if (!container) return;

        const pct = Math.round(this._zoomLevel * 100);
        container.style.transform = `scale(${this._zoomLevel})`;
        this.updateZoomLevelDisplay();

        // Show zoom indicator briefly
        const existing = modalBody.querySelector('.zoom-info');
        if (existing) existing.remove();

        const info = document.createElement('div');
        info.className = 'zoom-info show';
        info.textContent = `${pct}%`;
        modalBody.appendChild(info);
        clearTimeout(this._zoomInfoTimeout);
        this._zoomInfoTimeout = setTimeout(() => {
            info.classList.remove('show');
            setTimeout(() => info.remove(), 200);
        }, 1000);
    }

    resetZoom() {
        this._zoomLevel = 1;
        this.applyZoom();
        // Also reset scroll position
        const modalBody = document.getElementById('mermaid-modal-body');
        modalBody.scrollLeft = 0;
        modalBody.scrollTop = 0;
    }

    updateZoomLevelDisplay() {
        const zoomLevelEl = document.getElementById('zoom-level');
        if (zoomLevelEl) {
            zoomLevelEl.textContent = `${Math.round(this._zoomLevel * 100)}%`;
        }
    }

    handleZoomWheel(e) {
        const modalBody = document.getElementById('mermaid-modal-body');
        const container = modalBody.querySelector('.mermaid-diagram-container');
        if (!container) return;

        // Zoom with Ctrl+Scroll
        if (e.ctrlKey || e.metaKey) {
            e.preventDefault();
            const delta = e.deltaY > 0 ? -0.1 : 0.1;
            this.zoomDiagram(delta);
        }
    }

    handleDragStart(e) {
        const modalBody = document.getElementById('mermaid-modal-body');
        const container = modalBody.querySelector('.mermaid-diagram-container');
        if (!container) return;

        // Only drag with left mouse button, and only when zoomed
        if (e.button !== 0) return;

        this._isDragging = true;
        this._dragStartX = e.clientX;
        this._dragStartY = e.clientY;
        this._scrollStartX = modalBody.scrollLeft;
        this._scrollStartY = modalBody.scrollTop;
        modalBody.classList.add('dragging');
    }

    handleDragMove(e) {
        if (!this._isDragging) return;

        const modalBody = document.getElementById('mermaid-modal-body');
        const dx = e.clientX - this._dragStartX;
        const dy = e.clientY - this._dragStartY;
        modalBody.scrollLeft = this._scrollStartX - dx;
        modalBody.scrollTop = this._scrollStartY - dy;
    }

    handleDragEnd() {
        if (!this._isDragging) return;
        this._isDragging = false;
        const modalBody = document.getElementById('mermaid-modal-body');
        modalBody.classList.remove('dragging');
    }

    // --- Diagram Export ---
    exportDiagramAsSvg(diagramId) {
        const wrapper = document.querySelector(`.mermaid-wrapper[data-diagram-id="${diagramId}"]`);
        if (!wrapper) return;

        const mermaidEl = wrapper.querySelector('.mermaid');
        if (!mermaidEl) return;

        const svg = mermaidEl.querySelector('svg');
        if (!svg) {
            this.showToast('❌ Diagrama ainda não renderizado');
            return;
        }

        this.downloadSvg(svg, `diagrama-${diagramId}.svg`);
    }

    exportFullscreenDiagram() {
        const modalBody = document.getElementById('mermaid-modal-body');
        const svg = modalBody.querySelector('svg');
        if (!svg) {
            this.showToast('❸ Nenhum diagrama para exportar');
            return;
        }
        this.downloadSvg(svg, `diagrama-${this._fullscreenDiagramId || 'exportado'}.svg`);
    }

    downloadSvg(svgElement, filename) {
        // Clone the SVG to avoid modifying the original
        const clone = svgElement.cloneNode(true);

        // Ensure inline CSS from style tags is included
        const styles = svgElement.querySelectorAll('style');
        styles.forEach(s => {
            clone.appendChild(s.cloneNode(true));
        });

        // Add black background rectangle as the first child of the SVG
        const viewBox = clone.getAttribute('viewBox') || '';
        let width = clone.getAttribute('width') || '800';
        let height = clone.getAttribute('height') || '600';
        // Use viewBox dimensions if available
        const vbParts = viewBox.split(/\s+/);
        if (vbParts.length === 4) {
            width = vbParts[2];
            height = vbParts[3];
        }
        const bgRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        bgRect.setAttribute('width', width);
        bgRect.setAttribute('height', height);
        bgRect.setAttribute('fill', '#000000');
        bgRect.setAttribute('x', '0');
        bgRect.setAttribute('y', '0');
        clone.insertBefore(bgRect, clone.firstChild);

        // Serialize to string
        const serializer = new XMLSerializer();
        const svgString = serializer.serializeToString(clone);

        // Create proper SVG document with XML declaration
        const svgBlob = new Blob([
            '<?xml version="1.0" encoding="UTF-8"?>\n' +
            '<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">\n' +
            svgString
        ], { type: 'image/svg+xml;charset=utf-8' });

        const url = URL.createObjectURL(svgBlob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);

        this.showToast('✅ Diagrama exportado como SVG');
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
}

// --- Initialize ---
document.addEventListener('DOMContentLoaded', () => {
    new ChatApp();
});
