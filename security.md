# Melhorias de Segurança - DeepSeek Agent MCP

## 1. Sanitização de Entrada (`InputSanitizer.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 1.1 | **Remoção de caracteres de controle** | Remove caracteres de controle perigosos (`\x00-\x08`, `\x0B`, `\x0C`, `\x0E-\x1F`, `\x7F`) preservando `\n`, `\r`, `\t` |
| 1.2 | **Prevenção de injeção de system prompt** | Substitui delimitadores comuns usados para sobrescrever instruções do sistema: `` ``` `` → `'''`, `<<SYS>>` → `«SYS»`, `<|system|>` → `«system»`, `<|user|>` → `«user»`, `<|assistant|>` → `«assistant»` |
| 1.3 | **Remoção de tags HTML/XML perigosas** | Remove blocos completos de tags `<script>`, `<iframe>`, `<embed>`, `<object>`, `<frame>`, `<frameset>`, `<applet>`, `<form>`, `<input>`, `<textarea>`, `<select>`, `<option>`, `<style>`, `<link>`, `<meta>` com seus conteúdos |
| 1.4 | **Limite de tamanho** | Trunca mensagens para no máximo 10.000 caracteres |
| 1.5 | **Sanitização para exibição (XSS)** | Remove event handlers (`onclick=`, `onerror=`, etc.) e URIs `javascript:` de atributos (`href`, `src`, `action`, `data`) antes de exibir no frontend |

## 2. Rate Limiting (`RateLimiter.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 2.1 | **Sliding window rate limiter** | Implementação thread-safe com `ConcurrentQueue<DateTime>` para controle preciso de requisições por IP/session |
| 2.2 | **Limite configurável** | 30 requisições por minuto por IP (configurável via `appsettings.json`) |
| 2.3 | **Cleanup automático** | Timer de limpeza remove entradas expiradas a cada 5 minutos para evitar vazamento de memória |
| 2.4 | **Header Retry-After** | Resposta 429 (Too Many Requests) inclui `Retry-After` header informando tempo de espera |
| 2.5 | **Chaves independentes** | Cada IP/session tem seu próprio contador, impossibilitando que um cliente consuma o limite de outro |

## 3. Autenticação e Autorização

| # | Melhoria | Descrição | Arquivo |
|---|---------|-----------|---------|
| 3.1 | **Bearer Token Auth** | Autenticação via header `Authorization: Bearer <token>` ou `X-API-Key` | `WebAppExtensions.cs` |
| 3.2 | **Google OAuth** | Login com Google via ASP.NET Core Authentication + Cookie | `AgentHostBuilder.cs`, `WebAppExtensions.cs` |
| 3.3 | **Cookie HttpOnly** | Cookie de autenticação marcado como `HttpOnly=true` (inacessível via JavaScript) | `AgentHostBuilder.cs` |
| 3.4 | **Cookie SameSite=Lax** | Cookie configurado com `SameSiteMode.Lax` para proteção CSRF | `AgentHostBuilder.cs` |
| 3.5 | **Cookie SecurePolicy** | `SecurePolicy = SameAsRequest` — cookie só enviado em HTTPS se a conexão for HTTPS | `AgentHostBuilder.cs` |
| 3.6 | **Sliding Expiration** | Cookie com expiração deslizante de 8 horas | `AgentHostBuilder.cs` |
| 3.7 | **API Key via env var** | Fallback para `DEEPSEEK_API_KEY` e `DEEPSEEK_AGENT_AUTH_TOKEN` em variáveis de ambiente, nunca armazenadas em texto puro no repositório | `AgentHostBuilder.cs` |

## 4. Segurança na Configuração (`AgentHostBuilder.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 4.1 | **HTTPS password em env var** | A senha do certificado HTTPS é lida **exclusivamente** da variável de ambiente `DEEPSEEK_AGENT_HTTPS_PASSWORD`, nunca do arquivo de configuração |
| 4.2 | **Google secrets: env var > Registry > config** | Prioridade na obtenção de `ClientId`/`ClientSecret`: (1) variáveis de ambiente, (2) Windows Registry (`HKLM\SOFTWARE\DeepSeekAgentMCP`), (3) arquivo de configuração |
| 4.3 | **CORS restrito** | Origens permitidas configuradas explicitamente em `AllowedOrigins`, com métodos (`GET`, `POST`) e headers (`Content-Type`, `Authorization`, `X-API-Key`) restritos |
| 4.4 | **Validação de configuração** | `ValidateConfig()` verifica: API Key presente, modelo definido, MaxTokens > 0, Temperature entre 0 e 2, RateLimitPerMinute > 0 |
| 4.5 | **Timeout configurável** | HttpClient timeout configurável (padrão 300s) para evitar conexões pendentes indefinidamente |

## 5. Segurança de Sessão (`SessionManager.cs`, `WebAppExtensions.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 5.1 | **Limite de sessões por IP** | Máximo de 5 sessões simultâneas por IP (`MaxSessionsPerIp`) |
| 5.2 | **Timeout de sessão** | Sessões inativas por 30 minutos são automaticamente removidas |
| 5.3 | **Cleanup periódico** | Timer executa limpeza de sessões expiradas a cada 15 minutos |
| 5.4 | **Thread-safe com SemaphoreSlim** | Cada sessão possui um semáforo exclusivo para prevenir race conditions |
| 5.5 | **CancellationToken seguro** | Padrão TryRemove para evitar race conditions no cancelamento de requisições |
| 5.6 | **Validação de SessionId** | SessionId limitado a 100 caracteres para prevenir abuso |

## 6. Segurança na API DeepSeek (`DeepSeekClient.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 6.1 | **Bearer token** | API Key enviada via header `Authorization: Bearer` |
| 6.2 | **Retry com exponential backoff + jitter** | Até 3 retentativas em caso de 429 (rate limit) ou 5xx (erro servidor) com backoff exponencial e randomização de ±25% para evitar thundering herd |
| 6.3 | **Timeout de requisição** | HttpClient com timeout configurável (padrão 300s) |
| 6.4 | **Tratamento de cancellation** | Suporte a `CancellationToken` em todas as chamadas |

## 7. Segurança de Conexões MCP (`McpToolManager.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 7.1 | **Bearer token nas conexões HTTP** | Servidores MCP conectados via HTTP usam header `Authorization: Bearer ${MCP_SERVER_TOKEN}` |
| 7.2 | **Health check com timeout** | Verificação periódica de saúde com timeout de 10 segundos |
| 7.3 | **Reconexão automática** | Reconexão com limite de falhas consecutivas (3) |
| 7.4 | **Filtro de ferramentas (AllowedTools)** | Lista opcional de ferramentas permitidas por servidor, suportando wildcards |
| 7.5 | **Shutdown via CancellationToken** | Desligamento gracioso dos servidores MCP |
| 7.6 | **Cache de tool definitions** | Cache com TTL (30s) para evitar chamadas repetidas de descoberta |

## 8. Segurança no Frontend (`wwwroot/js/app.js`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 8.1 | **Session ID único por tab** | Cada aba do navegador gera seu próprio `sessionId` via `crypto.randomUUID()` |
| 8.2 | **Escape HTML** | Função `escapeHtml()` usada em todas as renderizações para prevenir XSS |
| 8.3 | **Links externos seguros** | Todos os links `<a>` recebem `target="_blank" rel="noopener"` |
| 8.4 | **Login OAuth com overlay** | Tela de login exibida antes de liberar o acesso ao chat |
| 8.5 | **Fallback de avatar** | Se a foto do perfil falhar ao carregar, exibe ícone SVG como fallback |
| 8.6 | **Tratamento de erros** | Todas as chamadas fetch possuem try/catch com mensagens seguras (sem expor detalhes internos) |

## 9. Profile Picture Proxy (`WebAppExtensions.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 9.1 | **Proxy de imagem** | A foto do perfil do Google é baixada pelo servidor e servida localmente, jamais expondo a URL original do Google ao cliente |
| 9.2 | **Cache no servidor** | Imagens cacheadas em memória com `Cache-Control: private, max-age=3600` |
| 9.3 | **Fallback em caso de erro** | Se falhar ao baixar a foto, tenta servir versão anterior do cache |
| 9.4 | **Timeout na busca** | Timeout de 10 segundos na requisição ao Google |

## 10. Identificação de IP Real (`WebAppExtensions.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 10.1 | **X-Forwarded-For** | Respeita header `X-Forwarded-For` para identificar IP real do cliente atrás de proxies reversos / load balancers |
| 10.2 | **Fallback** | Usa `RemoteIpAddress` da conexão direta como fallback |

## 11. Segurança do Cache de Skills (`SkillLoader.cs`)

| # | Melhoria | Descrição |
|---|---------|-----------|
| 11.1 | **ReaderWriterLockSlim** | Cache de skills thread-safe para leitura concorrente com escrita exclusiva |
| 11.2 | **Debounce no FileSystemWatcher** | Atualização de cache com debounce de 500ms para evitar múltiplas recargas |

## 12. Testes de Segurança

| # | Teste | Arquivo |
|---|-------|---------|
| 12.1 | `SanitizeMessage_NullInput_ReturnsEmpty` | `InputSanitizerTests.cs` |
| 12.2 | `SanitizeMessage_EmptyInput_ReturnsEmpty` | `InputSanitizerTests.cs` |
| 12.3 | `SanitizeMessage_RemovesControlCharacters` | `InputSanitizerTests.cs` |
| 12.4 | `SanitizeMessage_PreservesNewlinesAndTabs` | `InputSanitizerTests.cs` |
| 12.5 | `SanitizeMessage_ReplacesSystemPromptDelimiters` | `InputSanitizerTests.cs` |
| 12.6 | `SanitizeMessage_RemovesDangerousHtmlTags` | `InputSanitizerTests.cs` |
| 12.7 | `SanitizeMessage_TruncatesLongInput` | `InputSanitizerTests.cs` |
| 12.8 | `SanitizeForDisplay_RemovesEventHandlers` | `InputSanitizerTests.cs` |
| 12.9 | `SanitizeForDisplay_NullInput_ReturnsEmpty` | `InputSanitizerTests.cs` |
| 12.10 | `TryConsume_ReturnsTrue_WhenUnderLimit` | `RateLimiterTests.cs` |
| 12.11 | `TryConsume_ReturnsFalse_WhenOverLimit` | `RateLimiterTests.cs` |
| 12.12 | `TryConsume_DifferentKeys_AreIndependent` | `RateLimiterTests.cs` |
| 12.13 | `GetRemainingRequests_ReturnsCorrectCount` | `RateLimiterTests.cs` |
| 12.14 | `GetRemainingRequests_UnknownKey_ReturnsMax` | `RateLimiterTests.cs` |
| 12.15 | `Reset_ClearsKey` | `RateLimiterTests.cs` |

## 13. Resumo por Categoria

| Categoria | Total de Melhorias |
|-----------|:------------------:|
| Sanitização de entrada (XSS, prompt injection) | 5 |
| Rate limiting (DoS protection) | 5 |
| Autenticação e autorização | 7 |
| Segurança de configuração (secrets, CORS, HTTPS) | 5 |
| Segurança de sessão | 6 |
| Segurança na API externa (retry, timeout) | 4 |
| Segurança de conexões MCP | 6 |
| Segurança no frontend | 6 |
| Profile picture proxy | 4 |
| Identificação de IP real | 2 |
| Cache thread-safe | 2 |
| Testes de segurança | 15 |
| **Total** | **67** |
