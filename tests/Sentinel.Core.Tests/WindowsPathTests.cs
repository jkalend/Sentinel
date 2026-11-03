using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Tests;

public class WindowsPathTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "powershell.exe")]
    [InlineData("C:/Windows/System32/notepad.exe", "notepad.exe")]
    [InlineData("relative.exe", "relative.exe")]
    public void File_name_splits_windows_and_posix_separators(string path, string expected)
    {
        Assert.Equal(expected, WindowsPath.FileName(path));
    }

    [Theory]
    [InlineData(@"C:\dir.with.dot\file", "")]
    [InlineData(@"C:\dir.with.dot\file.exe", ".exe")]
    public void Extension_uses_the_file_name(string path, string expected)
    {
        Assert.Equal(expected, WindowsPath.Extension(path));
    }
}
