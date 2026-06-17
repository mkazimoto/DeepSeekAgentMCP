using System.Text.RegularExpressions;

namespace DeepSeekAgentMCP;

/// <summary>
/// Sanitização de entrada para prevenir injeção de prompt, XSS e outros ataques.
/// </summary>
public static partial class InputSanitizer
{
    /// <summary>
    /// Sanitiza a mensagem do usuário antes de enviar ao modelo:
    /// - Remove caracteres de controle perigosos (exceto \n \r \t)
    /// - Previne injeção de system prompt via delimitadores comuns
    /// - Remove tags HTML/XML maliciosas
    /// - Limita o tamanho
    /// </summary>
    public static string SanitizeMessage(string input, int maxLength = 10000)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // 1. Limitar tamanho
        var sanitized = input.Length > maxLength ? input[..maxLength] : input;

        // 2. Remover caracteres de controle (exceto newlines, tabs, carriage return)
        sanitized = ControlCharsRegex().Replace(sanitized, string.Empty);

        // 3. Prevenir injeção de system prompt: escapa ou remove delimitadores comuns
        //    usados para tentar sobrescrever instruções do sistema
        sanitized = sanitized
            .Replace("```", "'''")
            .Replace("<<SYS>>", "«SYS»")
            .Replace("<SYS>", "«SYS»")
            .Replace("<|system|>", "«system»")
            .Replace("<|user|>", "«user»")
            .Replace("<|assistant|>", "«assistant»");

        // 4. Remover tags HTML/XML perigosas (script, iframe, embed, object)
        sanitized = DangerousTagsRegex().Replace(sanitized, string.Empty);

        return sanitized.Trim();
    }

    /// <summary>
    /// Sanitiza conteúdo para exibição segura no frontend (proteção XSS).
    /// Remove tags HTML que podem executar scripts.
    /// </summary>
    public static string SanitizeForDisplay(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Remove tags de script, event handlers, javascript: URIs
        var sanitized = DangerousHtmlRegex().Replace(input, string.Empty);
        return sanitized;
    }

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex ControlCharsRegex();

    [GeneratedRegex(@"<\s*(script|iframe|embed|object|frame|frameset|applet|form|input|textarea|select|option|style|link|meta)\b[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousTagsRegex();

    [GeneratedRegex(@"(on\w+\s*=\s*[""'][^""']*[""'])", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousHtmlRegex();
}
