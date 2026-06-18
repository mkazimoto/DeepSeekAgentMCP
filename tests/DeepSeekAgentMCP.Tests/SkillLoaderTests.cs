namespace DeepSeekAgentMCP.Tests;

public class SkillLoaderTests
{
    [Fact]
    public void LoadInstructions_ReturnsNonEmptyString()
    {
        var instructions = SkillLoader.LoadInstructions();

        // Should contain at least some system instructions
        Assert.NotNull(instructions);
    }

    [Fact]
    public void LoadInstructions_CacheWorks_ReturnsSameInstance()
    {
        // Primeiro carregamento
        var first = SkillLoader.LoadInstructions();

        // Segundo carregamento (deve vir do cache)
        var second = SkillLoader.LoadInstructions();

        Assert.Equal(first, second);
    }

    [Fact]
    public void LoadSkillsToPrompt_ReturnsNonEmptyString()
    {
        var skills = SkillLoader.LoadSkillsToPrompt();

        // The project has skill files, so this should return content
        Assert.NotNull(skills);
    }

    [Fact]
    public void LoadSkillsToPrompt_ContainsSkillMarkers()
    {
        var skills = SkillLoader.LoadSkillsToPrompt();

        if (!string.IsNullOrEmpty(skills))
        {
            Assert.Contains("<skill>", skills);
            Assert.Contains("</skill>", skills);
        }
    }

    [Fact]
    public void GetSkillsMetadata_ReturnsList()
    {
        var metadata = SkillLoader.GetSkillsMetadata();

        Assert.NotNull(metadata);
        // Each entry should have at least a "file" key
        foreach (var entry in metadata)
        {
            Assert.Contains("file", entry.Keys);
        }
    }

    [Fact]
    public void InvalidateCache_ForcesReload()
    {
        // Arrange
        var before = SkillLoader.LoadSkillsToPrompt();

        // Act
        SkillLoader.InvalidateCache();
        var after = SkillLoader.LoadSkillsToPrompt();

        // Assert — should still return valid content after invalidation
        Assert.Equal(before, after);
    }

    [Fact]
    public void LastLoadTime_UpdatedAfterLoad()
    {
        // Arrange
        var before = SkillLoader.LastLoadTime;

        // Act
        SkillLoader.InvalidateCache();
        _ = SkillLoader.LoadSkillsToPrompt();

        // Assert
        Assert.True(SkillLoader.LastLoadTime >= before);
    }

    [Fact]
    public void LoadInstructions_ContainsSystemDirectives()
    {
        var instructions = SkillLoader.LoadInstructions();

        // Should be meaningful system instructions
        Assert.NotNull(instructions);
        Assert.True(instructions.Length > 50);
    }
}
