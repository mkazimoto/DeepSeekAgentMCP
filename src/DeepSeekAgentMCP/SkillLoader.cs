using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeekAgentMCP;

/// <summary>
/// Carrega skills internas do diretório Skills/ e as transforma em
/// conteúdo para o System Prompt do agente.
/// Suporta cache com invalidação via FileSystemWatcher e frontmatter YAML.
/// </summary>
public static partial class SkillLoader
{
    private static readonly string? _baseDirectory;
    private static string? _cachedSkillsPrompt;
    private static DateTime _lastCacheLoad = DateTime.MinValue;
    private static FileSystemWatcher? _fileWatcher;
    private static readonly ReaderWriterLockSlim _cacheLock = new();

    /// <summary>
    /// Timestamp do último carregamento do cache de skills.
    /// </summary>
    public static DateTime LastLoadTime => _lastCacheLoad;

    static SkillLoader()
    {
        _baseDirectory = PathHelper.FindSkillsDirectory();
        SetupFileWatcher();
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
    /// Utiliza cache com invalidação automática via FileSystemWatcher.
    /// </summary>
    public static string LoadSkillsToPrompt()
    {
        if (_baseDirectory == null)
            return string.Empty;

        // Retorna cache se disponível (leitura concorrente)
        _cacheLock.EnterReadLock();
        try
        {
            if (_cachedSkillsPrompt != null)
                return _cachedSkillsPrompt;
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }

        // Only top-level .md files — ignora subdiretórios para evitar Skills/Skills/ duplicado
        var skillFiles = Directory.GetFiles(_baseDirectory, "*.md", SearchOption.TopDirectoryOnly);
        if (skillFiles.Length == 0)
            return string.Empty;

        // Valida subdiretórios inesperados
        ValidateNoSubdirectories();

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
                var fileName = Path.GetFileNameWithoutExtension(file);
                var (metadata, content) = ParseFrontmatter(file);

                sb.AppendLine("<skill>");

                // Se tem frontmatter, usa o name/description como cabeçalho
                if (metadata.TryGetValue("name", out var name))
                {
                    sb.AppendLine($"**Skill: {name}**");
                    if (metadata.TryGetValue("description", out var desc))
                        sb.AppendLine($"> {desc}");
                    if (metadata.TryGetValue("version", out var ver))
                        sb.AppendLine($"> *Versão {ver}*");
                    sb.AppendLine();
                }

                sb.AppendLine(content.Trim());
                sb.AppendLine("</skill>");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SkillLoader] Error loading {file}: {ex.Message}");
            }
        }

        var result = sb.ToString();

        // Atualiza cache (escrita exclusiva)
        _cacheLock.EnterWriteLock();
        try
        {
            _cachedSkillsPrompt = result;
            _lastCacheLoad = DateTime.UtcNow;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }

        return result;
    }

    /// <summary>
    /// Invalida o cache de skills forçando recarga na próxima chamada.
    /// </summary>
    public static void InvalidateCache()
    {
        _cacheLock.EnterWriteLock();
        try
        {
            _cachedSkillsPrompt = null;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
        Console.WriteLine("[SkillLoader] Cache invalidated.");
    }

    /// <summary>
    /// Retorna metadados das skills carregadas (para diagnóstico / API).
    /// </summary>
    public static List<Dictionary<string, string>> GetSkillsMetadata()
    {
        var result = new List<Dictionary<string, string>>();
        if (_baseDirectory == null) return result;

        var skillFiles = Directory.GetFiles(_baseDirectory, "*.md", SearchOption.TopDirectoryOnly);
        foreach (var file in skillFiles)
        {
            try
            {
                var (metadata, _) = ParseFrontmatter(file);
                metadata["file"] = Path.GetFileName(file);
                result.Add(metadata);
            }
            catch { /* skip files with errors */ }
        }

        return result;
    }

    /// <summary>
    /// Configura FileSystemWatcher para invalidar cache automaticamente
    /// quando arquivos de skill forem alterados, criados ou removidos.
    /// </summary>
    private static void SetupFileWatcher()
    {
        if (_baseDirectory == null || !Directory.Exists(_baseDirectory))
            return;

        try
        {
            _fileWatcher = new FileSystemWatcher(_baseDirectory, "*.md")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _fileWatcher.Changed += OnSkillFileChanged;
            _fileWatcher.Created += OnSkillFileChanged;
            _fileWatcher.Deleted += OnSkillFileChanged;
            _fileWatcher.Renamed += OnSkillFileChanged;

            Console.WriteLine($"[SkillLoader] FileWatcher active on: {_baseDirectory}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SkillLoader] Failed to setup FileWatcher: {ex.Message}");
        }
    }

    private static void OnSkillFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: múltiplos eventos podem ser disparados para um mesmo arquivo
        InvalidateCache();
    }

    /// <summary>
    /// Valida que não há subdiretórios inesperados dentro de Skills/.
    /// </summary>
    private static void ValidateNoSubdirectories()
    {
        if (_baseDirectory == null) return;

        var subdirs = Directory.GetDirectories(_baseDirectory);
        if (subdirs.Length > 0)
        {
            Console.Error.WriteLine($"[SkillLoader] WARNING: Found {subdirs.Length} subdirectories inside Skills/ that will be ignored:");
            foreach (var dir in subdirs)
                Console.Error.WriteLine($"  - {dir}");
        }
    }

    /// <summary>
    /// Parseia frontmatter YAML simples entre marcadores --- no início do arquivo.
    /// Formato suportado: chave: valor (uma por linha).
    /// </summary>
    private static (Dictionary<string, string> Metadata, string Content) ParseFrontmatter(string filePath)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var content = File.ReadAllText(filePath);

        // Frontmatter: começa com --- na primeira linha, termina com ---
        if (!content.StartsWith("---"))
        {
            // Sem frontmatter — usa o título como name
            var titleMatch = TitleRegex().Match(content);
            if (titleMatch.Success)
                metadata["name"] = titleMatch.Groups[1].Value.Trim();
            return (metadata, content);
        }

        var endOfFrontmatter = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endOfFrontmatter == -1)
        {
            // --- inicial sem fechamento — trata como conteúdo normal
            return (metadata, content);
        }

        var frontmatterLines = content[3..endOfFrontmatter].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var mainContent = content[(endOfFrontmatter + 4)..].Trim();

        foreach (var line in frontmatterLines)
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(key))
                    metadata[key] = value;
            }
        }

        return (metadata, mainContent);
    }

    [GeneratedRegex(@"^#\s+Skill:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleRegex();
}
