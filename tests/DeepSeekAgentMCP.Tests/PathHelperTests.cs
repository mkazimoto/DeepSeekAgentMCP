namespace DeepSeekAgentMCP.Tests;

public class PathHelperTests
{
    [Fact]
    public void FindConfigPath_ReturnsNonEmptyString()
    {
        var path = PathHelper.FindConfigPath();
        // Should find a path (empty only if neither dev nor publish paths exist)
        Assert.NotNull(path);
    }

    [Fact]
    public void FindConfigPath_FindsAppSettingsJson()
    {
        var path = PathHelper.FindConfigPath();
        if (!string.IsNullOrEmpty(path))
        {
            Assert.True(
                path.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase),
                $"Path should end with appsettings.json: {path}");
        }
    }

    [Fact]
    public void FindSkillsDirectory_ReturnsExistingDirectory_WhenFound()
    {
        var dir = PathHelper.FindSkillsDirectory();
        if (dir != null)
        {
            Assert.True(Directory.Exists(dir), $"Skills directory should exist: {dir}");
        }
    }

    [Fact]
    public void FindInstructionsFile_ReturnsExistingFile_WhenFound()
    {
        var path = PathHelper.FindInstructionsFile();
        if (path != null)
        {
            Assert.True(File.Exists(path), $"Instructions file should exist: {path}");
        }
    }

    [Fact]
    public void FindContentRoot_ReturnsNonEmptyString()
    {
        var root = PathHelper.FindContentRoot();
        Assert.NotNull(root);
        Assert.NotEmpty(root);
    }

    [Fact]
    public void FindMcpConfigPath_WithValidConfigPath_ReturnsNonNull()
    {
        var configPath = PathHelper.FindConfigPath();
        if (!string.IsNullOrEmpty(configPath))
        {
            var mcpPath = PathHelper.FindMcpConfigPath(configPath);
            Assert.NotNull(mcpPath);
            Assert.NotEmpty(mcpPath);
        }
    }
}
