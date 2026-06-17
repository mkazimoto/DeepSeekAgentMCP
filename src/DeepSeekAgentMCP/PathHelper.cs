namespace DeepSeekAgentMCP;

/// <summary>
/// Centraliza a lógica de descoberta de caminhos (config, skills, wwwroot)
/// usada em todo o projeto, evitando duplicação entre Program.cs,
/// DeepSeekAgentService.cs e SkillLoader.cs.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Localiza o arquivo appsettings.json, primeiro no diretório de saída (published),
    /// depois no diretório do projeto (desenvolvimento).
    /// </summary>
    public static string FindConfigPath()
    {
        var searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "appsettings.json"))
        };

        return searchPaths.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    /// <summary>
    /// Localiza o arquivo de configuração dos servidores MCP.
    /// </summary>
    public static string FindMcpConfigPath(string? configPath = null)
    {
        // Try next to config file first
        if (!string.IsNullOrEmpty(configPath))
        {
            var configDir = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
            var mcpPath = Path.Combine(configDir, "mcp-servers.json");
            if (File.Exists(mcpPath))
                return mcpPath;
        }

        // Try relative to project root
        var projectRoot = !string.IsNullOrEmpty(configPath)
            ? Path.GetDirectoryName(Path.GetDirectoryName(configPath)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        var relPath = "config/mcp-servers.json";
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            try
            {
                var configJson = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(configJson);
                relPath = doc.RootElement.GetProperty("McpServerConfigPath").GetString() ?? "config/mcp-servers.json";
            }
            catch { }
        }

        return Path.GetFullPath(Path.Combine(projectRoot, relPath));
    }

    /// <summary>
    /// Localiza o diretório wwwroot (content root) para o servidor web.
    /// </summary>
    public static string FindContentRoot()
    {
        var contentRoot = Path.GetFullPath(AppContext.BaseDirectory);
        if (!Directory.Exists(Path.Combine(contentRoot, "wwwroot")))
        {
            var devPath = Path.GetFullPath(Path.Combine(contentRoot, "..", "..", ".."));
            if (Directory.Exists(Path.Combine(devPath, "wwwroot")))
                contentRoot = devPath;
        }
        return contentRoot;
    }

    /// <summary>
    /// Localiza o diretório Skills, primeiro no diretório de saída,
    /// depois no diretório do projeto.
    /// </summary>
    public static string? FindSkillsDirectory()
    {
        var searchPaths = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Skills")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Skills"))
        };

        return searchPaths.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Localiza o arquivo instructions.md.
    /// </summary>
    public static string? FindInstructionsFile()
    {
        var searchPaths = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "instructions.md")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "instructions.md"))
        };

        return searchPaths.FirstOrDefault(File.Exists);
    }
}
