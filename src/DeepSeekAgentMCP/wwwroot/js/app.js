// ============================================
// DeepSeek Agent MCP - Web Interface
// ============================================

// Global callback for Google Identity Services (GIS).
// GIS calls this function by name when the user completes sign-in.
let _chatAppInstance = null;

function handleGisCredential(response) {
    if (_chatAppInstance) {
        _chatAppInstance.handleGisCredentialResponse(response);
    }
}

class ChatApp {
    constructor() {
        _chatAppInstance = this;
        this.isLoading = false;
        this.isAuthenticated = false;
        this.userInfo = null;
        this.gisClientId = null;
        this._gisInitialized = false;
        // Generate a unique session ID per tab/instance
        // This ensures each browser tab has its own isolated conversation
        this.sessionId = crypto.randomUUID ? crypto.randomUUID() : 
            'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
                const r = Math.random() * 16 | 0;
                return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
            });
        this._statusPollInterval = null;
        this._healthPollInterval = null;
        this.initElements();
        this.initEventListeners();
        this.initTheme();
        this.initMermaid();
        // Check auth status first, then initialize UI
        this.checkAuthStatus();
    }

    // --- Authentication (Google Identity Services) ---
    async checkAuthStatus() {
        try {
            const response = await fetch('/api/auth/status');
            const data = await response.json();

            if (data.authenticated) {
                this.isAuthenticated = true;
                this.userInfo = data;
                this.gisClientId = data.clientId || null;
                this.hideLogin();
                await this.restoreWelcomeMessage();
                this.loadMcpStatus();
                this.startStatusPolling();
                this.startHealthPolling();
                this.autoResizeTextarea();
                this.showUserInfo(data);
            } else if (data.authDisabled) {
                // Auth is not configured — show the app directly
                this.isAuthenticated = true;
                this.hideLogin();
                await this.restoreWelcomeMessage();
                this.loadMcpStatus();
                this.startStatusPolling();
                this.startHealthPolling();
                this.autoResizeTextarea();
            } else {
                // Not authenticated — show login screen
                this.isAuthenticated = false;
                this.gisClientId = data.clientId || null;
                this.showLogin();
            }
        } catch (err) {
            // If endpoint fails, assume auth is disabled and show app
            console.warn('Auth status check failed, showing app directly:', err);
            this.isAuthenticated = true;
            this.hideLogin();
            await this.restoreWelcomeMessage();
            this.loadMcpStatus();
            this.startStatusPolling();
            this.startHealthPolling();
            this.autoResizeTextarea();
        }
    }

    showLogin() {
        const overlay = document.getElementById('login-overlay');
        if (!overlay) return;
        overlay.style.display = 'flex';

        // Initialize GIS when the overlay is shown
        this.initGis();
    }

    /**
     * Initializes Google Identity Services (GIS) on the login overlay.
     * Sets up the One Tap and the sign-in button programmatically.
     */
    initGis() {
        if (this._gisInitialized) {
            return;
        }

        if (!this.gisClientId) {
            console.warn(
                'GIS: Google Client ID not available from server. ' +
                'Check that GoogleAuth.ClientId is configured in appsettings.json ' +
                'or the GOOGLE_CLIENT_ID environment variable is set.'
            );
            return;
        }

        if (typeof google === 'undefined' || !google.accounts) {
            console.warn('GIS: Google Identity Services script not loaded yet. Retrying...');
            // Retry once after a short delay (GIS script may still be loading)
            setTimeout(() => this.initGis(), 1000);
            return;
        }

        console.log('GIS: Initializing with client ID:', this.gisClientId);

        try {
            // Initialize GIS with the client ID from the server
            google.accounts.id.initialize({
                client_id: this.gisClientId,
                callback: handleGisCredential,
                auto_prompt: false
            });

            // Render the Google Sign-In button inside our custom button container
            google.accounts.id.renderButton(
                document.getElementById('google-signin-btn'),
                {
                    type: 'standard',
                    theme: 'outline',
                    size: 'large',
                    text: 'signin_with',
                    shape: 'pill',
                    width: 280
                }
            );

            this._gisInitialized = true;
        } catch (err) {
            console.error('GIS: Failed to initialize:', err);
        }
    }

    /**
     * Called by GIS after successful authentication.
     * Sends the credential (ID token) to the server for validation.
     */
    async handleGisCredentialResponse(response) {
        if (!response || !response.credential) {
            console.error('GIS: No credential received from Google');
            this.showLoginError('Falha na autenticação: nenhum credential recebido do Google.');
            return;
        }

        console.log('GIS: Credential received, sending to server for validation...');

        try {
            const res = await fetch('/api/auth/google/callback', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ credential: response.credential })
            });

            if (!res.ok) {
                const err = await res.json().catch(() => null);
                const errorMsg = err?.error || `Erro ${res.status}: ${res.statusText}`;
                console.error('GIS: Server rejected credential:', errorMsg);
                this.showLoginError(
                    'Falha na autenticação: o servidor rejeitou o credential. ' +
                    'Verifique se o GoogleAuth.ClientId está configurado corretamente no servidor.'
                );
                return;
            }

            // Reload the page to reflect authenticated state
            window.location.reload();
        } catch (err) {
            console.error('GIS: Network error sending credential to server:', err);
            this.showLoginError(
                'Falha de rede ao autenticar. Verifique se o servidor está rodando e tente novamente.'
            );
        }
    }

    /**
     * Shows an error message in the login overlay.
     */
    showLoginError(message) {
        const card = document.querySelector('.login-card');
        if (!card) return;

        let errorEl = card.querySelector('.login-error');
        if (!errorEl) {
            errorEl = document.createElement('p');
            errorEl.className = 'login-error';
            card.appendChild(errorEl);
        }
        errorEl.textContent = message;
        errorEl.style.display = 'block';
    }

    hideLogin() {
        const overlay = document.getElementById('login-overlay');
        if (overlay) {
            overlay.style.display = 'none';
        }
    }

    showUserInfo(data) {
        // Add user info to sidebar footer
        const sidebarFooter = document.querySelector('.sidebar-footer');
        if (sidebarFooter && data.name) {
            const userInfoEl = document.createElement('div');
            userInfoEl.className = 'user-info';

            // Avatar container
            const avatar = document.createElement('div');
            avatar.className = 'user-info-avatar';

            const personIcon = '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg>';

            if (data.picture) {
                const img = document.createElement('img');
                img.src = data.picture;
                img.alt = this.escapeHtml(data.name);
                img.onerror = function () {
                    this.outerHTML = personIcon;
                };
                avatar.appendChild(img);
            } else {
                avatar.innerHTML = personIcon;
            }

            userInfoEl.appendChild(avatar);

            // Details
            const details = document.createElement('div');
            details.className = 'user-info-details';
            details.innerHTML = `
                <span class="user-info-name">${this.escapeHtml(data.name)}</span>
                <button class="user-info-logout" id="logout-btn">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                        <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/>
                        <polyline points="16 17 21 12 16 7"/>
                        <line x1="21" y1="12" x2="9" y2="12"/>
                    </svg>
                    Sair
                </button>
            `;

            userInfoEl.appendChild(details);

            // Remove existing user-info if any
            const existing = sidebarFooter.querySelector('.user-info');
            if (existing) existing.remove();
            sidebarFooter.prepend(userInfoEl);

            document.getElementById('logout-btn').addEventListener('click', () => this.logout());
        }
    }

    async logout() {
        try {
            await fetch(`/api/auth/logout?sessionId=${encodeURIComponent(this.sessionId)}`, { method: 'POST' });
            window.location.reload();
        } catch {
            window.location.reload();
        }
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
        // Re-render all stored diagrams with the new theme
        this.reRenderAllDiagrams();
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

            // Se o servidor retornar código de não autorizado, faz logout automático
            if (data?.code === 'AUTH_REQUIRED') {
                this.logout();
                return;
            }

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

        const html = data.mcpServers.map(server => {
            const toolList = server.toolNames && server.toolNames.length > 0
                ? server.toolNames.map(t => `  • ${t}`).join('\n')
                : 'Nenhuma ferramenta';
            return `
            <div class="mcp-server-item">
                <span class="mcp-server-dot ${server.connected ? 'connected' : 'disconnected'}"></span>
                <span class="mcp-server-name" title="${this.escapeHtml(server.name)}">${this.escapeHtml(server.name)}</span>
                <span class="mcp-server-tools" title="Ferramentas:\n${this.escapeHtml(toolList)}">${server.toolCount} ferramenta${server.toolCount !== 1 ? 's' : ''}</span>
            </div>`;
        }).join('');

        this.elements.mcpStatus.innerHTML = html;
    }

    /** Inicia polling periódico do status dos servidores MCP a cada 10 segundos */
    startStatusPolling() {
        if (this._statusPollInterval) clearInterval(this._statusPollInterval);
        this._statusPollInterval = setInterval(() => this.loadMcpStatus(), 10000);
    }

    // --- Health Metrics ---
    async loadHealthMetrics() {
        try {
            const response = await fetch('/api/health');
            const data = await response.json();

            const sessionsEl = document.getElementById('health-active-sessions');
            const usersEl = document.getElementById('health-active-users');

            if (sessionsEl) {
                sessionsEl.textContent = data.activeSessions ?? '—';
            }
            if (usersEl) {
                usersEl.textContent = data.connectedUsers ?? '—';
            }
        } catch {
            // Silently ignore — health check failures are not critical for UX
        }
    }

    /** Inicia polling periódico das métricas de health a cada 10 segundos */
    startHealthPolling() {
        this.loadHealthMetrics();
        if (this._healthPollInterval) clearInterval(this._healthPollInterval);
        this._healthPollInterval = setInterval(() => this.loadHealthMetrics(), 10_000);
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

        } catch (err) {
            if (!this.isLoading) return;
            this.setLoading(false);
            this.addMessage(`Erro: ${err.message}`, 'error');
        }

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
            await this.restoreWelcomeMessage();
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
                background: '#ffffff',
                fontSize: '14px',
            },
        });
    }

    // --- Render Mermaid Diagrams ---
    /** Armazena source dos diagramas para re-renderização ao trocar tema */
    async renderMermaidDiagrams(container) {
        if (typeof mermaid === 'undefined') return;
        const mermaidElements = container.querySelectorAll('.mermaid:not(.rendered)');
        if (mermaidElements.length === 0) return;

        // Inicializa o mapa de fontes se não existir
        if (!this._diagramSources) this._diagramSources = new Map();

        for (const el of mermaidElements) {
            try {
                const diagramText = el.textContent || '';
                // Store source for theme re-render
                const wrapper = el.closest('.mermaid-wrapper');
                if (wrapper) {
                    const diagramId = wrapper.dataset.diagramId;
                    if (diagramId) {
                        this._diagramSources.set(diagramId, diagramText);
                    }
                }

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

    /** Re-renderiza todos os diagramas armazenados (usado ao trocar tema) */
    async reRenderAllDiagrams() {
        if (!this._diagramSources || this._diagramSources.size === 0) return;
        if (typeof mermaid === 'undefined') return;

        for (const [diagramId, source] of this._diagramSources) {
            const wrapper = document.querySelector(`.mermaid-wrapper[data-diagram-id="${diagramId}"]`);
            if (!wrapper) continue;

            const mermaidEl = wrapper.querySelector('.mermaid');
            if (!mermaidEl) continue;

            // Remove rendered state so it gets re-rendered
            mermaidEl.classList.remove('rendered');

            try {
                const valid = await mermaid.parse(source, { suppressErrors: true });
                if (!valid) {
                    mermaidEl.classList.add('rendered', 'mermaid-error');
                    continue;
                }
                const id = 'mermaid-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);
                const { svg } = await mermaid.render(id, source);
                mermaidEl.innerHTML = svg;
                mermaidEl.classList.add('rendered');
            } catch (e) {
                console.warn('Mermaid re-render error:', e);
                mermaidEl.classList.add('rendered', 'mermaid-error');
            }
        }
    }

    async restoreWelcomeMessage() {
        const container = this.elements.messagesContainer;

        const div = document.createElement('div');
        div.className = 'message welcome-message';
        div.id = 'welcome-message';

        // Load welcome text from markdown file
        let welcomeHtml = '';
        try {
            const response = await fetch('welcome-message.md');
            const markdown = await response.text();
            welcomeHtml = this.formatMessage(markdown);
        } catch {
            welcomeHtml = '<p>👋 Bem vindo! Sou o <strong>Assistente gerador de consulta SQL</strong>.</p>';
        }

        div.innerHTML = `
            <div class="message-avatar agent-avatar">
                <img src="assets/robo.png" width="40" height="40" alt="Robô Assistente" style="border-radius: 8px" />
            </div>
            <div class="message-content">
                <div class="message-sender">Assistente gerador de consulta SQL - ERP TOTVS RM</div>
                <div class="message-text">
                    ${welcomeHtml}
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

    // --- Diagram Fullscreen (Pan infinito via transform) ---
    openDiagramFullscreen(diagramId) {
        const wrapper = document.querySelector(`.mermaid-wrapper[data-diagram-id="${diagramId}"]`);
        if (!wrapper) return;

        const mermaidEl = wrapper.querySelector('.mermaid');
        if (!mermaidEl) return;

        const modal = document.getElementById('mermaid-modal');
        const modalBody = document.getElementById('mermaid-modal-body');

        // Reset pan/zoom state
        this._zoomLevel = 1;
        this._zoomMax = 5;
        this._zoomMin = 0.1;
        this._panX = 0;
        this._panY = 0;

        // Clone the rendered SVG or the raw mermaid source
        const svg = mermaidEl.querySelector('svg');
        if (svg) {
            const clone = svg.cloneNode(true);
            clone.removeAttribute('width');
            clone.removeAttribute('height');
            clone.setAttribute('viewBox', svg.getAttribute('viewBox') || `0 0 ${svg.getAttribute('width') || 800} ${svg.getAttribute('height') || 600}`);
            clone.style.maxWidth = 'none';
            clone.style.maxHeight = 'none';
            clone.style.width = 'auto';
            clone.style.height = 'auto';

            // Store SVG dimensions for minimap
            const vb = clone.getAttribute('viewBox').split(/\s+/);
            this._svgNaturalWidth = parseInt(vb[2]) || 800;
            this._svgNaturalHeight = parseInt(vb[3]) || 600;

            // Create container for zoom/pan (transform-based, no scroll)
            const container = document.createElement('div');
            container.className = 'mermaid-diagram-container';
            container.appendChild(clone);
            modalBody.innerHTML = '';
            modalBody.appendChild(container);

            // Criar minimap
            this.createMinimap(modalBody, clone, this._svgNaturalWidth, this._svgNaturalHeight);
        } else {
            // Fallback: show raw mermaid source
            modalBody.innerHTML = `<pre style="white-space:pre-wrap;font-family:monospace;font-size:13px;color:var(--text-primary);background:var(--bg-secondary);padding:16px;border-radius:8px;max-width:100%;overflow:auto;">${this.escapeHtml(mermaidEl.textContent || '')}</pre>`;
        }

        modal.style.display = 'flex';
        document.body.style.overflow = 'hidden';
        this._fullscreenDiagramId = diagramId;

        requestAnimationFrame(() => {
            modal.classList.add('open');
            // Calculate fit-to-screen zoom so the entire diagram is visible
            if (svg && this._svgNaturalWidth > 0 && this._svgNaturalHeight > 0) {
                const availW = Math.max(modalBody.clientWidth - 80, 100);
                const availH = Math.max(modalBody.clientHeight - 80, 100);
                const fitZoom = Math.min(availW / this._svgNaturalWidth, availH / this._svgNaturalHeight);
                this._zoomLevel = Math.min(Math.max(fitZoom, this._zoomMin), 1);
            }
            this.centerDiagram();
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
        this._panX = 0;
        this._panY = 0;
        this.cleanupZoomEvents(modal);
        this.removeMinimap();
    }

    /** Centraliza o diagrama no modal (considerando o zoom atual) */
    centerDiagram() {
        const modalBody = document.getElementById('mermaid-modal-body');
        const container = modalBody.querySelector('.mermaid-diagram-container');
        if (!container) return;

        const svg = container.querySelector('svg');
        if (!svg) return;

        const rect = modalBody.getBoundingClientRect();
        const svgWidth = this._svgNaturalWidth || 800;
        const svgHeight = this._svgNaturalHeight || 600;

        // Centraliza considerando o zoom: o centro do SVG escalado deve ficar no centro da viewport
        this._panX = (rect.width - svgWidth * this._zoomLevel) / 2;
        this._panY = (rect.height - svgHeight * this._zoomLevel) / 2;
        this.applyTransform();
    }

    // --- Zoom + Pan Controls (transform-based, pan infinito) ---
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
        this._wheelHandler = (e) => this.handleDiagramWheel(e);
        this._dragStartHandler = (e) => this.handleDiagramDragStart(e);
        this._dragMoveHandler = (e) => this.handleDiagramDragMove(e);
        this._dragEndHandler = () => this.handleDiagramDragEnd();

        // Minimap events
        this._minimapClickHandler = (e) => this.handleMinimapClick(e);
        this._minimapDragHandler = (e) => this.handleMinimapDrag(e);

        zoomIn.addEventListener('click', this._zoomInHandler);
        zoomOut.addEventListener('click', this._zoomOutHandler);
        zoomReset.addEventListener('click', this._zoomResetHandler);
        modalBody.addEventListener('wheel', this._wheelHandler, { passive: false });
        modalBody.addEventListener('mousedown', this._dragStartHandler);
        document.addEventListener('mousemove', this._dragMoveHandler);
        document.addEventListener('mouseup', this._dragEndHandler);

        const minimap = modalBody.querySelector('.minimap');
        if (minimap) {
            minimap.addEventListener('mousedown', this._minimapClickHandler);
        }
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

        const minimap = modalBody.querySelector('.minimap');
        if (minimap && this._minimapClickHandler) {
            minimap.removeEventListener('mousedown', this._minimapClickHandler);
        }

        this._zoomInHandler = null;
        this._zoomOutHandler = null;
        this._zoomResetHandler = null;
        this._wheelHandler = null;
        this._dragStartHandler = null;
        this._dragMoveHandler = null;
        this._dragEndHandler = null;
        this._minimapClickHandler = null;
        this._minimapDragHandler = null;
        this._isDragging = false;
    }

    zoomDiagram(delta) {
        const newZoom = Math.max(this._zoomMin, Math.min(this._zoomMax, this._zoomLevel + delta));
        if (newZoom === this._zoomLevel) return;
        this._zoomLevel = newZoom;
        this.applyTransform();
    }

    /** Aplica transform: translate(X, Y) scale(Z) — pan infinito sem scroll */
    applyTransform() {
        const modalBody = document.getElementById('mermaid-modal-body');
        const container = modalBody.querySelector('.mermaid-diagram-container');
        if (!container) return;

        const pct = Math.round(this._zoomLevel * 100);
        container.style.transform = `translate(${this._panX}px, ${this._panY}px) scale(${this._zoomLevel})`;
        this.updateZoomLevelDisplay();
        this.updateMinimapViewport();

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
        this.centerDiagram();
    }

    updateZoomLevelDisplay() {
        const zoomLevelEl = document.getElementById('zoom-level');
        if (zoomLevelEl) {
            zoomLevelEl.textContent = `${Math.round(this._zoomLevel * 100)}%`;
        }
    }

    /** Wheel = zoom (com Ctrl) ou pan vertical (sem Ctrl) */
    handleDiagramWheel(e) {
        const modalBody = document.getElementById('mermaid-modal-body');
        const container = modalBody.querySelector('.mermaid-diagram-container');
        if (!container) return;

        if (e.ctrlKey || e.metaKey) {
            e.preventDefault();
            const delta = e.deltaY > 0 ? -0.1 : 0.1;
            this.zoomDiagram(delta);
        } else {
            // Pan vertical com scroll suave
            this._panY -= e.deltaY;
            this.applyTransform();
        }
    }

    handleDiagramDragStart(e) {
        const modalBody = document.getElementById('mermaid-modal-body');
        const container = modalBody.querySelector('.mermaid-diagram-container');
        if (!container) return;

        if (e.button !== 0) return;

        this._isDragging = true;
        this._dragStartX = e.clientX;
        this._dragStartY = e.clientY;
        this._panStartX = this._panX;
        this._panStartY = this._panY;
        modalBody.classList.add('dragging');
    }

    handleDiagramDragMove(e) {
        if (!this._isDragging) return;

        const dx = e.clientX - this._dragStartX;
        const dy = e.clientY - this._dragStartY;
        this._panX = this._panStartX + dx;
        this._panY = this._panStartY + dy;
        this.applyTransform();
    }

    handleDiagramDragEnd() {
        if (!this._isDragging) return;
        this._isDragging = false;
        const modalBody = document.getElementById('mermaid-modal-body');
        modalBody.classList.remove('dragging');
    }

    // --- Minimap ---
    createMinimap(modalBody, svgElement, naturalWidth, naturalHeight) {
        this.removeMinimap();

        this._minimapSvgUrl = null;
        this._minimapImg = null;

        const minimap = document.createElement('canvas');
        minimap.className = 'minimap';
        minimap.width = 160;
        minimap.height = 120;
        this._minimapEl = minimap;

        const ctx = minimap.getContext('2d');
        if (!ctx) return;

        // Serializa SVG e cria Image para desenhar no canvas
        const svgString = new XMLSerializer().serializeToString(svgElement);
        const blob = new Blob([svgString], { type: 'image/svg+xml;charset=utf-8' });
        this._minimapSvgUrl = URL.createObjectURL(blob);

        const img = new Image();
        this._minimapImg = img;

        img.onload = () => {
            const scale = Math.min(minimap.width / naturalWidth, minimap.height / naturalHeight);
            const drawW = naturalWidth * scale;
            const drawH = naturalHeight * scale;
            const offsetX = (minimap.width - drawW) / 2;
            const offsetY = (minimap.height - drawH) / 2;

            this._minimapParams = { scale, offsetX, offsetY, drawW, drawH };

            // Desenho inicial com viewport
            this.redrawMinimap();
        };

        img.src = this._minimapSvgUrl;
        modalBody.appendChild(minimap);
    }

    removeMinimap() {
        if (this._minimapSvgUrl) {
            URL.revokeObjectURL(this._minimapSvgUrl);
            this._minimapSvgUrl = null;
        }
        if (this._minimapEl) {
            this._minimapEl.remove();
            this._minimapEl = null;
        }
        this._minimapParams = null;
        this._minimapImg = null;
    }

    /** Redesenha o canvas do minimap por completo (fundo + viewport) */
    redrawMinimap() {
        if (!this._minimapEl || !this._minimapParams || !this._minimapImg) return;
        const canvas = this._minimapEl;
        const ctx = canvas.getContext('2d');
        if (!ctx) return;

        const { offsetX, offsetY, drawW, drawH } = this._minimapParams;

        // Limpa e redesenha o SVG
        ctx.fillStyle = '#000000';
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.drawImage(this._minimapImg, offsetX, offsetY, drawW, drawH);

        // Desenha o viewport
        this.drawMinimapViewport(ctx);
    }

    /** Desenha o retângulo do viewport sobre o minimap */
    drawMinimapViewport(ctx) {
        if (!this._minimapParams) return;
        const canvas = this._minimapEl;
        const { offsetX, offsetY, drawW, drawH } = this._minimapParams;
        const modalBody = document.getElementById('mermaid-modal-body');

        const viewW = modalBody.clientWidth;
        const viewH = modalBody.clientHeight;
        const z = this._zoomLevel;

        // Coordenadas do viewport no SVG natural
        const vpLeft = -this._panX / z;
        const vpTop = -this._panY / z;
        const vpRight = vpLeft + viewW / z;
        const vpBottom = vpTop + viewH / z;

        // Mapeia para coordenadas do minimap
        const ratioX = drawW / this._svgNaturalWidth;
        const ratioY = drawH / this._svgNaturalHeight;
        const mX = offsetX + vpLeft * ratioX;
        const mY = offsetY + vpTop * ratioY;
        const mW = (vpRight - vpLeft) * ratioX;
        const mH = (vpBottom - vpTop) * ratioY;

        // Sombra fora do viewport
        ctx.fillStyle = 'rgba(0, 0, 0, 0.4)';
        ctx.fillRect(0, 0, canvas.width, mY);
        ctx.fillRect(0, mY + mH, canvas.width, canvas.height - mY - mH);
        ctx.fillRect(0, mY, mX, mH);
        ctx.fillRect(mX + mW, mY, canvas.width - mX - mW, mH);

        // Borda do viewport
        ctx.strokeStyle = '#818cf8';
        ctx.lineWidth = 2;
        ctx.strokeRect(mX, mY, mW, mH);
    }

    updateMinimapViewport() {
        if (!this._minimapEl || !this._minimapParams) return;
        const ctx = this._minimapEl.getContext('2d');
        if (!ctx) return;
        this.redrawMinimap();
    }

    handleMinimapClick(e) {
        if (!this._minimapEl || !this._minimapParams) return;

        const rect = this._minimapEl.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const y = e.clientY - rect.top;

        const { offsetX, offsetY, drawW, drawH } = this._minimapParams;

        // Converte clique no minimap para coordenada SVG natural
        const svgX = (x - offsetX) / (drawW / this._svgNaturalWidth);
        const svgY = (y - offsetY) / (drawH / this._svgNaturalHeight);

        // Centraliza o viewport nessa posição
        const modalBody = document.getElementById('mermaid-modal-body');
        const viewW = modalBody.clientWidth;
        const viewH = modalBody.clientHeight;
        const z = this._zoomLevel;

        this._panX = -(svgX * z - viewW / 2);
        this._panY = -(svgY * z - viewH / 2);

        this.applyTransform();

        // Inicia drag no minimap para follow do mouse
        this._minimapDragging = true;
        this._minimapDragStartX = e.clientX;
        this._minimapDragStartY = e.clientY;

        const onMove = (ev) => this.handleMinimapDrag(ev);
        const onUp = () => {
            this._minimapDragging = false;
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
        };
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    }

    handleMinimapDrag(e) {
        if (!this._minimapDragging || !this._minimapEl || !this._minimapParams) return;

        const rect = this._minimapEl.getBoundingClientRect();
        const x = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
        const y = Math.max(0, Math.min(rect.height, e.clientY - rect.top));

        const { offsetX, offsetY, drawW, drawH } = this._minimapParams;

        const svgX = (x - offsetX) / (drawW / this._svgNaturalWidth);
        const svgY = (y - offsetY) / (drawH / this._svgNaturalHeight);

        const modalBody = document.getElementById('mermaid-modal-body');
        const viewW = modalBody.clientWidth;
        const viewH = modalBody.clientHeight;
        const z = this._zoomLevel;

        this._panX = -(svgX * z - viewW / 2);
        this._panY = -(svgY * z - viewH / 2);

        this.applyTransform();
    }

    // --- Diagram Export (SVG) ---
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
            this.showToast('❌ Nenhum diagrama para exportar');
            return;
        }
        this.downloadSvg(svg, `diagrama-${this._fullscreenDiagramId || 'exportado'}.svg`);
    }

    downloadSvg(svgElement, filename) {
        const clone = svgElement.cloneNode(true);
        const styles = svgElement.querySelectorAll('style');
        styles.forEach(s => clone.appendChild(s.cloneNode(true)));

        const viewBox = clone.getAttribute('viewBox') || '';
        let width = clone.getAttribute('width') || '800';
        let height = clone.getAttribute('height') || '600';
        const vbParts = viewBox.split(/\s+/);
        if (vbParts.length === 4) {
            width = vbParts[2];
            height = vbParts[3];
        }

        const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
        const bgRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        bgRect.setAttribute('width', width);
        bgRect.setAttribute('height', height);
        bgRect.setAttribute('fill', isDark ? '#000000' : '#ffffff');
        bgRect.setAttribute('x', '0');
        bgRect.setAttribute('y', '0');
        clone.insertBefore(bgRect, clone.firstChild);

        const serializer = new XMLSerializer();
        const svgString = serializer.serializeToString(clone);

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

    // --- HTML Sanitization (XSS Protection) ---
    sanitizeHtml(html) {
        // Remove event handlers (onclick, onload, onerror, etc.)
        html = html.replace(/\s+on\w+\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)/gi, '');

        // Remove javascript: and data: URIs in links
        html = html.replace(/(href|src|action|formaction)\s*=\s*(?:"javascript:[^"]*"|'javascript:[^']*'|javascript:[^\s>]+)/gi, (match, attr) => {
            return `${attr}="about:blank"`;
        });
        html = html.replace(/(href|src|action|formaction)\s*=\s*(?:"data:[^"]*"|'data:[^']*'|data:[^\s>]+)/gi, (match, attr) => {
            return `${attr}="about:blank"`;
        });

        // Remove <script> tags and any content between them
        html = html.replace(/<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>/gi, '');

        // Remove <iframe>, <embed>, <object> tags
        html = html.replace(/<(iframe|embed|object|frame|frameset|applet)\b[^>]*>.*?<\/\1\s*>/gis, '');
        html = html.replace(/<(iframe|embed|object|frame|frameset|applet)\b[^>]*\/?>/gi, '');

        // Remove <style> tags (prevent CSS injection)
        html = html.replace(/<style\b[^>]*>.*?<\/style\s*>/gis, '');

        // Remove <link> tags (prevent external resource loading)
        html = html.replace(/<link\b[^>]*\/?>/gi, '');

        // Remove <meta> tags
        html = html.replace(/<meta\b[^>]*\/?>/gi, '');

        // Remove <base> tags
        html = html.replace(/<base\b[^>]*\/?>/gi, '');

        return html;
    }

    // Override addMessage to sanitize agent responses
    addMessage(text, type) {
        const container = this.elements.messagesContainer;

        const div = document.createElement('div');
        div.className = `message ${type === 'user' ? 'user-message' : type === 'error' ? 'error-message' : ''}`;

        const avatar = document.createElement('div');
        avatar.className = `message-avatar ${type === 'user' ? 'user-avatar' : 'agent-avatar'}`;

        if (type === 'user') {
            const picture = this.userInfo?.picture;
            if (picture) {
                const img = document.createElement('img');
                img.src = picture;
                img.width = 40;
                img.height = 40;
                img.alt = 'Você';
                img.style.borderRadius = '8px';
                img.onerror = function() {
                    this.outerHTML = `<svg width="18" height="18" viewBox="0 0 18 18" fill="none">
                        <circle cx="9" cy="6" r="3" stroke="currentColor" stroke-width="1.5"/>
                        <path d="M3 17C3 13.6863 5.68629 11 9 11C12.3137 11 15 13.6863 15 17" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
                    </svg>`;
                };
                avatar.appendChild(img);
            } else {
                avatar.innerHTML = `<svg width="18" height="18" viewBox="0 0 18 18" fill="none">
                    <circle cx="9" cy="6" r="3" stroke="currentColor" stroke-width="1.5"/>
                    <path d="M3 17C3 13.6863 5.68629 11 9 11C12.3137 11 15 13.6863 15 17" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
                </svg>`;
            }
        } else {
            avatar.innerHTML = `
                <img src="assets/robo.png" width="40" height="40" alt="Robô Assistente" style="border-radius: 8px" />
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

        // Format markdown-like content and sanitize against XSS
        let formattedHtml = this.formatMessage(text);
        // Only sanitize agent/error messages (user messages are plain text)
        if (type !== 'user') {
            formattedHtml = this.sanitizeHtml(formattedHtml);
        }
        textDiv.innerHTML = formattedHtml;

        content.appendChild(textDiv);
        div.appendChild(content);

        container.appendChild(div);

        // Render Mermaid diagrams after element is in the DOM
        this.renderMermaidDiagrams(textDiv);

        this.scrollToBottom();
    }
}

// --- Initialize ---
document.addEventListener('DOMContentLoaded', () => {
    new ChatApp();
});
