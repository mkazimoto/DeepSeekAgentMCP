# DeepSeek Agent MCP

🤖 **DeepSeek Agent** com suporte a **MCP (Model Context Protocol)** — um agente inteligente em C# que conecta o modelo DeepSeek a ferramentas externas através do protocolo MCP.

## ✨ Funcionalidades

- **Integração com DeepSeek API** — Chat completions com suporte a streaming e function calling
- **Múltiplos servidores MCP** — Conecte-se a vários servidores MCP simultaneamente
- **Descoberta automática de ferramentas** — Ferramentas MCP são expostas automaticamente ao DeepSeek
- **Loop interativo** — Terminal interativo para conversar com o agente
- **Streaming** — Respostas em tempo real com streaming
- **Histórico de conversação** — Mantém contexto entre mensagens
- **Interface Web** — Chat interativo via navegador com API REST
- **Autenticação Google OAuth** — Login com conta Google na interface web
- **Windows Service** — Execução como serviço do Windows em segundo plano

## 🚀 Como usar

### 1. Configurar API Key

A chave da API **não deve** ser colocada diretamente no `config/appsettings.json` (o arquivo versionado contém apenas um placeholder vazio). Configure por variável de ambiente:

#### Windows (permanente — recomendado)

```powershell
[Environment]::SetEnvironmentVariable("DEEPSEEK_API_KEY", "sua-chave-aqui", "User")
```

Depois reinicie o terminal para aplicar.

#### Windows (sessão atual)

```powershell
$env:DEEPSEEK_API_KEY = "sua-chave-aqui"
```

#### Linux / macOS

```bash
export DEEPSEEK_API_KEY="sua-chave-aqui"
```

> O programa busca a chave na seguinte ordem: `appsettings.json` → variável de ambiente (Process) → variável de ambiente (User). Se nenhuma for encontrada, solicita a digitação no terminal.

### 1.1. Configurar Google OAuth (opcional)

A interface web suporta login com Google. As credenciais **devem** ser configuradas via variáveis de ambiente (fonte primária):

```powershell
$env:GOOGLE_CLIENT_ID = "seu-client-id.apps.googleusercontent.com"
$env:GOOGLE_CLIENT_SECRET = "seu-client-secret"
```

> O sistema busca as credenciais primeiro nas variáveis de ambiente `GOOGLE_CLIENT_ID` e `GOOGLE_CLIENT_SECRET`. Se não estiverem definidas, faz fallback para os valores em `config/appsettings.json`. Para habilitar o Google Auth, mantenha `"Enabled": true` na seção `GoogleAuth` do `config/appsettings.json`.

#### Configurar no Windows Service

Ao instalar como serviço Windows, passe as credenciais como parâmetros:

```powershell
.\scripts\install-service.ps1 -Action install `
    -GoogleClientId "seu-client-id.apps.googleusercontent.com" `
    -GoogleClientSecret "seu-client-secret"
```

Ou configure manualmente no registro:

```powershell
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\DeepSeekAgentMCP"
Set-ItemProperty -Path $regPath -Name "Environment" -Value @(
    "GOOGLE_CLIENT_ID=seu-client-id.apps.googleusercontent.com",
    "GOOGLE_CLIENT_SECRET=seu-client-secret"
) -Type MultiString
Restart-Service -Name DeepSeekAgentMCP
```

### 2. Configurar Servidores MCP

Edite o arquivo `config/mcp-servers.json` para adicionar os servidores MCP desejados.

> Servidores MCP são configurados em `config/mcp-servers.json`. Consulte a documentação de cada servidor para específica.

### 3. Executar

```bash
cd src/DeepSeekAgentMCP
dotnet run
```

### 4. Comandos

| Comando | Descrição |
|---------|-----------|
| `/exit` | Sair do agente |
| `/clear` | Limpar histórico da conversa |
| `/history` | Mostrar histórico da conversa |
| `/mcp` | Mostrar status dos servidores MCP |
| `/help` | Mostrar ajuda |

## � Testes

```bash
# Executar todos os testes
dotnet test

# Executar testes com verbose
dotnet test --logger "console;verbosity=detailed"
```

O projeto inclui testes unitários para:
- **DeepSeekAgent** — Orquestração de chamadas e tratamento de respostas
- **DeepSeekClient** — Chamadas HTTP à API DeepSeek (com fake client)
- **InputSanitizer** — Sanitização de entrada, prevenção de prompt injection e XSS
- **McpToolManager** — Gerenciamento de servidores MCP (com fake manager)
- **PathHelper** — Descoberta de caminhos em cenários de desenvolvimento e publicação
- **RateLimiter** — Sliding window rate limiting, limites por chave, reset
- **SessionManager** — Gerenciamento de sessões de conversa

## �🧩 Adicionar Novos Servidores MCP

Adicione novas entradas em `config/mcp-servers.json`:

```json
{
  "Name": "MeuServidor",
  "Enabled": true,
  "TransportType": "stdio",
  "Command": "npx",
  "Arguments": ["-y", "@modelcontextprotocol/server-exemplo"],
  "EnvironmentVariables": {
    "API_KEY": "minha-chave"
  }
}
```

## 🏗️ Estrutura do Projeto

```
DeepSeekAgentMCP/
├── config/
│   ├── appsettings.json            # Configuração principal
│   └── mcp-servers.json            # Configuração dos servidores MCP
├── scripts/
│   └── install-service.ps1        # Instalação do Windows Service
├── src/DeepSeekAgentMCP/
│   ├── Models/
│   │   ├── AgentConfig.cs         # Modelo de configuração do agente
│   │   ├── ChatMessage.cs         # Modelos de mensagem
│   │   ├── DeepSeekResponses.cs   # Modelos de requisição/resposta
│   │   └── GoogleAuthConfig.cs    # Configuração do Google OAuth
│   ├── Skills/                    # Skills internas (templates para o modelo)
│   │   ├── cte-recursivo-auto-relacionamento.md
│   │   └── ...
│   ├── wwwroot/                   # Interface web
│   │   ├── index.html
│   │   ├── css/styles.css
│   │   └── js/app.js
│   ├── AgentHostBuilder.cs        # Factory centralizada de componentes
│   ├── DeepSeekAgent.cs           # Orquestrador do agente
│   ├── DeepSeekAgentService.cs    # Windows Service Host
│   ├── DeepSeekClient.cs          # Cliente HTTP para DeepSeek API
│   ├── InputSanitizer.cs          # Sanitização anti-prompt injection
│   ├── instructions.md            # System prompt base
│   ├── McpToolManager.cs          # Gerenciamento de servidores MCP
│   ├── PathHelper.cs              # Descoberta de caminhos
│   ├── Program.cs                 # Ponto de entrada
│   ├── RateLimiter.cs             # Rate limiter sliding window
│   ├── SessionManager.cs          # Gerenciamento de sessões
│   ├── SkillLoader.cs             # Carregamento de skills
│   └── WebAppExtensions.cs        # Endpoints da API REST e config. do Google OAuth
├── tests/
│   └── DeepSeekAgentMCP.Tests/    # Testes unitários (xUnit)
│       ├── DeepSeekAgentTests.cs
│       ├── DeepSeekClientTests.cs
│       ├── FakeDeepSeekClient.cs
│       ├── FakeMcpToolManager.cs
│       ├── InputSanitizerTests.cs
│       ├── McpToolManagerTests.cs
│       ├── PathHelperTests.cs
│       ├── RateLimiterTests.cs
│       └── SessionManagerTests.cs
├── DeepSeekAgentMCP.slnx
└── README.md
```

## �️ Windows Service

O agente pode ser executado como um **Serviço Windows**, rodando em segundo plano com a interface web habilitada.

### Instalação

Execute o PowerShell como **Administrador** e use o script de instalação:

```powershell
# Instalar o serviço (sem Google OAuth)
.\scripts\install-service.ps1 -Action install

# Instalar o serviço com Google OAuth
.\scripts\install-service.ps1 -Action install `
    -GoogleClientId "seu-client-id.apps.googleusercontent.com" `
    -GoogleClientSecret "seu-client-secret"

# Verificar status
.\scripts\install-service.ps1 -Action status

# Desinstalar o serviço
.\scripts\install-service.ps1 -Action uninstall
```

O script:
1. Compila o projeto em modo **Release** com `dotnet publish --self-contained`
2. Cria o serviço Windows com nome `DeepSeekAgentMCP`
3. Configura as variáveis de ambiente do Google OAuth no registro do serviço (se fornecidas)
4. Configura inicialização automática
5. Inicia o serviço automaticamente

### Gerenciamento manual

```powershell
# Via PowerShell (como Administrador)
Stop-Service -Name DeepSeekAgentMCP
Start-Service -Name DeepSeekAgentMCP
Restart-Service -Name DeepSeekAgentMCP

# Via sc.exe
sc.exe stop DeepSeekAgentMCP
sc.exe start DeepSeekAgentMCP
```

### Logs

O serviço registra eventos no **Visualizador de Eventos do Windows**:

```powershell
Get-EventLog -LogName Application -Source DeepSeekAgentMCP -Newest 20
```

### Acesso

Com o serviço rodando, acesse a interface web em:

```
http://localhost:5000
```

### Execução manual (modo serviço)

Para testar o comportamento do serviço sem instalar:

```bash
cd src/DeepSeekAgentMCP
dotnet run -- --service
```

### Estrutura de arquivos adicionada

```
DeepSeekAgentMCP/
├── scripts/
│   └── install-service.ps1    # Script de instalação/desinstalação
├── src/DeepSeekAgentMCP/
│   ├── DeepSeekAgentService.cs # Implementação do Windows Service
│   └── ...
```

## �📋 Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (para servidores MCP baseados em npx)
- Uma chave de API do [DeepSeek](https://platform.deepseek.com/)
