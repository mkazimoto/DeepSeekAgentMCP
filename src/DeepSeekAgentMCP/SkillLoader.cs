using System.Text;

namespace DeepSeekAgentMCP;

/// <summary>
/// Carrega skills internas do diretório Skills/ e as transforma em
/// conteúdo para o System Prompt do agente.
/// </summary>
public static class SkillLoader
{
    private static readonly string? _baseDirectory;

    static SkillLoader()
    {
        _baseDirectory = PathHelper.FindSkillsDirectory();
    }

    /// <summary>
    /// Carrega o arquivo instructions.md que contém as instruções base do sistema.
    /// </summary>
    public static string LoadInstructions()
    {
        var path = PathHelper.FindInstructionsFile();
        if (path != null)
        {
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SkillLoader] Error loading {path}: {ex.Message}");
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Retorna o conteúdo de todas as skills encontradas como um único bloco de texto,
    /// pronto para ser inserido no System Prompt.
    /// </summary>
    public static string LoadSkillsToPrompt()
    {
        if (_baseDirectory == null)
            return string.Empty;

        // Only top-level .md files — ignora subdiretórios para evitar Skills/Skills/ duplicado
        var skillFiles = Directory.GetFiles(_baseDirectory, "*.md", SearchOption.TopDirectoryOnly);
        if (skillFiles.Length == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# Internal Skills");
        sb.AppendLine("You MUST strictly follow the pattern, rules, and examples defined in the matching skill below when the user's request matches its purpose.");
        sb.AppendLine("Do NOT deviate from the structure, constraints, or rules specified in the skill. Treat each skill as a mandatory template.");
        sb.AppendLine();

        foreach (var file in skillFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                sb.AppendLine("<skill>");
                sb.AppendLine(content.Trim());
                sb.AppendLine("</skill>");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SkillLoader] Error loading {file}: {ex.Message}");
            }
        }

        return sb.ToString();
    }
}
