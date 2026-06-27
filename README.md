# DeepSeek Agent MCP

🤖 **DeepSeek Agent** com suporte a **MCP (Model Context Protocol)** — um agente inteligente em C# que conecta o modelo DeepSeek a ferramentas externas através do protocolo MCP.

## ✨ Funcionalidades

- 🤖 **Integração com DeepSeek API** — Chat completions com suporte a streaming e function calling
- 🔌 **Múltiplos servidores MCP** — Conecte-se a vários servidores MCP simultaneamente
- 🧰 **Descoberta automática de ferramentas** — Ferramentas MCP expostas automaticamente ao DeepSeek
- 💬 **Loop interativo** — Terminal interativo para conversar com o agente
- ⚡ **Streaming** — Respostas em tempo real com streaming
- 📜 **Histórico de conversação** — Mantém contexto entre mensagens
- 🌐 **Interface Web** — Chat interativo via navegador com API REST
- 🔐 **Autenticação Google (GIS)** — Login com conta Google via Google Identity Services na interface web (desabilitado por padrão)
- 🖥️ **Windows Service** — Execução como serviço do Windows em segundo plano
- 🛡️ **Health check com auto-reconnect** — Reconexão automática a servidores MCP com falha
- ⚙️ **HttpClient pooling** — HttpClient reutilizável via construtor para evitar socket exhaustion
- 🔑 **Resolução de variáveis via ambiente/registro** — Tokens e chaves lidos de env vars ou Registro Windows

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

> O programa busca a chave na seguinte ordem: variável de ambiente (Process) → variável de ambiente (User) → **Registro Windows** (`HKLM\SOFTWARE\DeepSeekAgentMCP\DEEPSEEK_API_KEY`) → `appsettings.json`. Se nenhuma for encontrada, solicita a digitação no terminal.

#### Windows Service

```powershell
.\scripts\install-service.ps1 -Action install -DeepSeekApiKey "sua-chave-aqui"
```

Ou configure manualmente no Registro Windows:
```powershell
$regPath = "HKLM:\SOFTWARE\DeepSeekAgentMCP"
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name "DEEPSEEK_API_KEY" -Value "sua-chave-aqui"
```

### 1.1. Configurar Google Auth via GIS (opcional)

A interface web suporta login com Google via **Google Identity Services (GIS)** — autenticação client-side com popup, sem necessidade de `ClientSecret` no servidor.

O Google Auth está **desabilitado por padrão** (`"Enabled": false` no `appsettings.json`).

Para habilitar, edite `config/appsettings.json`:
```json
"GoogleAuth": {
  "Enabled": true,
  "Scopes": ["openid", "profile", "email"]
}
```

#### Client ID obrigatório

O `ClientId` é **obrigatório** e deve ser configurado em uma das seguintes fontes (ordem de precedência):

| Prioridade | Fonte | Local |
|---|---|---|
| **1ª** | **Registro Windows** | `HKLM\SOFTWARE\DeepSeekAgentMCP\GOOGLE_CLIENT_ID` |
| **2ª** | Variável de ambiente | `GOOGLE_CLIENT_ID` |
| **3ª** | `appsettings.json` | `GoogleAuth.ClientId` |

Configure via registro Windows (recomendado para Windows Service):
```powershell
$regPath = "HKLM:\SOFTWARE\DeepSeekAgentMCP"
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name "GOOGLE_CLIENT_ID" -Value "seu-client-id.apps.googleusercontent.com"
```

Ou via variável de ambiente (modo console):
```powershell
$env:GOOGLE_CLIENT_ID = "seu-client-id.apps.googleusercontent.com"
```

> ⚠️ **O `ClientSecret` não é mais necessário.** O GIS usa autenticação client-side e o servidor valida o JWT com as chaves públicas do Google (JWKS).

#### Configurar no Google Cloud Console

Após configurar o `ClientId`, é preciso liberar a origem no Google Cloud Console:

1. Acesse [console.cloud.google.com/apis/credentials](https://console.cloud.google.com/apis/credentials)
2. Clique no **OAuth 2.0 Client ID** que você está usando
3. Em **Authorized JavaScript Origins**, adicione a URL base do app (ex: `http://localhost:5000`)
4. Salve

> Sem essa configuração, o Google retorna o erro `"no registered origin"` ao tentar fazer login.

#### Windows Service

O registro é a melhor opção para Windows Service:

```powershell
.\scripts\install-service.ps1 -Action install `
    -DeepSeekApiKey "sk-sua-chave" `
    -GoogleClientId "seu-client-id.apps.googleusercontent.com" `
    -McpServerToken "seu-token-mcp"
```

Ou configure manualmente:

```powershell
$regPath = "HKLM:\SOFTWARE\DeepSeekAgentMCP"
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name "GOOGLE_CLIENT_ID" -Value "seu-client-id.apps.googleusercontent.com"
Set-ItemProperty -Path $regPath -Name "MCP_SERVER_TOKEN" -Value "seu-token-aqui"
Set-ItemProperty -Path $regPath -Name "DEEPSEEK_API_KEY" -Value "sua-chave-deepseek"

Restart-Service -Name DeepSeekAgentMCP
```

### 1.2. Configurar Token do Servidor MCP

Se o servidor MCP utilizar autenticação via token (`Authorization: Bearer`), configure-o **fora do versionamento**:

#### Modo Console
```powershell
$env:MCP_SERVER_TOKEN = "seu-token-aqui"
dotnet run --project src/DeepSeekAgentMCP
```

#### Windows Service
```powershell
.\scripts\install-service.ps1 -Action install -McpServerToken "seu-token-aqui"
```

#### Registro Windows (fallback)
O valor também pode ser registrado diretamente em:
```powershell
$regPath = "HKLM:\SOFTWARE\DeepSeekAgentMCP"
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name "MCP_SERVER_TOKEN" -Value "seu-token-aqui"
```

> O `config/mcp-servers.json` versionado contém o placeholder `${MCP_SERVER_TOKEN}`, que é resolvido automaticamente em tempo de execução: **variável de ambiente → Registro Windows → vazio**.

### 2. Configurar Servidores MCP

Edite o arquivo `config/mcp-servers.json` para adicionar os servidores MCP desejados.

> Consulte o template em `config/mcp-servers.template.json` para a estrutura esperada. Tokens e credenciais sensíveis devem usar `${VARIAVEL}` — o `McpToolManager` os resolve via ambiente/registro.

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

O projeto inclui **100 testes unitários** para:
- **DeepSeekAgent** — Orquestração de chamadas e tratamento de respostas
- **DeepSeekClient** — Chamadas HTTP à API DeepSeek (com fake client)
- **InputSanitizer** — Sanitização de entrada, prevenção de prompt injection e XSS (9 testes)
- **McpToolManager** — Gerenciamento de servidores MCP, cache e serialização
- **PathHelper** — Descoberta de caminhos em cenários de desenvolvimento e publicação
- **RateLimiter** — Sliding window rate limiting, limites por chave, reset
- **SessionManager** — Gerenciamento de sessões de conversa (12 testes)

## 🧩 Adicionar Novos Servidores MCP

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

> **Tokens e credenciais:** Use o padrão `${VARIAVEL_DE_AMBIENTE}` em valores sensíveis (como headers `Authorization`). O `McpToolManager` resolve automaticamente na ordem: **variável de ambiente → Registro Windows → string literal**.

## 🏗️ Estrutura do Projeto

```
DeepSeekAgentMCP/
├── config/
│   ├── appsettings.json            # Configuração principal
│   ├── mcp-servers.json            # Configuração dos servidores MCP
│   └── mcp-servers.template.json   # Template de configuração MCP
├── publish/                        # Publicação do Windows Service (self-contained)
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
├── .gitignore
└── README.md
```

## �️ Windows Service

O agente pode ser executado como um **Serviço Windows**, rodando em segundo plano com a interface web habilitada.

### Instalação

Execute o PowerShell como **Administrador** e use o script de instalação:

```powershell
# Instalar o serviço (sem Google Auth)
.\scripts\install-service.ps1 -Action install

# Instalar o serviço com todas as credenciais
.\scripts\install-service.ps1 -Action install `
    -DeepSeekApiKey "sk-sua-chave" `
    -GoogleClientId "seu-client-id.apps.googleusercontent.com" `
    -McpServerToken "seu-token-mcp"

# Verificar status
.\scripts\install-service.ps1 -Action status

# Desinstalar o serviço
.\scripts\install-service.ps1 -Action uninstall
```

O script:
1. Compila o projeto em modo **Release** com `dotnet publish --self-contained`
2. Cria o serviço Windows com nome `DeepSeekAgentMCP`
3. Configura as variáveis de ambiente no registro do serviço (`Environment`): `GOOGLE_CLIENT_ID`, `MCP_SERVER_TOKEN`
4. Registra todas as variáveis em `HKLM\\SOFTWARE\\DeepSeekAgentMCP` como fallback:
   - `DEEPSEEK_API_KEY` — Chave da API DeepSeek
   - `MCP_SERVER_TOKEN` — Token de autenticação do servidor MCP
   - `GOOGLE_CLIENT_ID` — Client ID do Google Identity Services (GIS)
5. Configura inicialização automática
6. Inicia o serviço automaticamente

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
