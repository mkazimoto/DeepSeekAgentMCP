namespace DeepSeekAgentMCP.Tests;

public class InputSanitizerTests
{
    [Fact]
    public void SanitizeMessage_NullInput_ReturnsEmpty()
    {
        var result = InputSanitizer.SanitizeMessage(null!);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SanitizeMessage_EmptyInput_ReturnsEmpty()
    {
        var result = InputSanitizer.SanitizeMessage("   ");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SanitizeMessage_RemovesControlCharacters()
    {
        var input = "Hello\x00World\x1FTest";
        var result = InputSanitizer.SanitizeMessage(input);
        Assert.Equal("HelloWorldTest", result);
    }

    [Fact]
    public void SanitizeMessage_PreservesNewlinesAndTabs()
    {
        var input = "Line1\nLine2\tTabbed";
        var result = InputSanitizer.SanitizeMessage(input);
        Assert.Equal("Line1\nLine2\tTabbed", result);
    }

    [Fact]
    public void SanitizeMessage_ReplacesSystemPromptDelimiters()
    {
        var input = "```code``` <<SYS>>inject<</SYS>> <|system|>hack<|system|>";
        var result = InputSanitizer.SanitizeMessage(input);
        Assert.DoesNotContain("```", result);
        Assert.DoesNotContain("<<SYS>>", result);
        Assert.DoesNotContain("<SYS>", result);
        Assert.DoesNotContain("<|system|>", result);
        Assert.DoesNotContain("<|user|>", result);
        Assert.DoesNotContain("<|assistant|>", result);
    }

    [Fact]
    public void SanitizeMessage_RemovesDangerousHtmlTags()
    {
        var input = "Hello <script>alert('xss')</script> World <iframe src='evil'></iframe>";
        var result = InputSanitizer.SanitizeMessage(input);
        Assert.DoesNotContain("<script>", result);
        Assert.DoesNotContain("<iframe>", result);
        Assert.DoesNotContain("alert", result);
    }

    [Fact]
    public void SanitizeMessage_TruncatesLongInput()
    {
        var input = new string('A', 20000);
        var result = InputSanitizer.SanitizeMessage(input, maxLength: 10000);
        Assert.Equal(10000, result.Length);
    }

    [Fact]
    public void SanitizeForDisplay_RemovesEventHandlers()
    {
        var input = "Click <div onclick='alert(1)'>here</div>";
        var result = InputSanitizer.SanitizeForDisplay(input);
        Assert.DoesNotContain("onclick", result);
    }

    [Fact]
    public void SanitizeForDisplay_NullInput_ReturnsEmpty()
    {
        var result = InputSanitizer.SanitizeForDisplay(null!);
        Assert.Equal(string.Empty, result);
    }
}
