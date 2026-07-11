using Xunit;

namespace DivaModManager.Tests;

public sealed class UserErrorMessageTests
{
    [Fact]
    public void ConvertsEnglishSystemExceptionsToChineseUserMessages()
    {
        var access = UserErrorMessage.From(new UnauthorizedAccessException("Access is denied."));
        var accessWithChinesePath = UserErrorMessage.From(
            new UnauthorizedAccessException("Access to the path 'D:\\中文目录' is denied."));
        var io = UserErrorMessage.From(new IOException("The process cannot access the file."));

        Assert.Equal("没有权限访问所需的文件或目录。请检查权限后重试。", access);
        Assert.Equal("没有权限访问所需的文件或目录。请检查权限后重试。", accessWithChinesePath);
        Assert.Equal("文件读写失败。请确认文件未被其他程序占用，并检查路径与权限。", io);
        Assert.DoesNotContain("Access", access, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Access", accessWithChinesePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("process", io, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsActionableChineseDomainMessages()
    {
        const string message = "数据库在扫描后已被其他程序修改，请刷新歌曲列表后重试。";

        Assert.Equal(message, UserErrorMessage.From(new InvalidDataException(message)));
    }
}
