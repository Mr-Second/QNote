using QNote.Update;

namespace QNote.Tests;

/// <summary>
/// <see cref="ReleaseNotes.ExtractChangelog"/> against the real CI template
/// shape (.github/release-notes-template.md): anchors on 「## 更新内容」,
/// stops at the next section, de-markdowns bullets, caps the line count.
/// </summary>
public class ReleaseNotesTests
{
    private const string TemplateBody =
        """
        QNote 1.5.1 是 **1.5.0 的修复版**：修复启动检查更新的一个问题。1.5.0 的全部新功能均包含在内。

        ## 更新内容

        - **新增**：启动时检查更新（可在设置中关闭）
        - 编辑器新增**查找 / 替换**条（Ctrl+F / Ctrl+H / F3）
        - 修复跨便签复制图片后引用丢失（详见 [发布页](https://github.com/Mr-Second/QNote)）的问题
        - 以下为 1.5.0 新增：
        - 全新「关于 QNote」页

        ## 下载哪个变体？

        | 文件 | 说明 |
        |---|---|
        | `QNote_1.5.1_win-x64_native-aot.zip` | 推荐 |

        ## 从旧便携版升级

        数据在 data 目录。
        """;

    [Fact]
    public void TemplateBody_ExtractsCleanedBullets()
    {
        var result = ReleaseNotes.ExtractChangelog(TemplateBody);

        Assert.NotNull(result);
        var lines = result.Split(Environment.NewLine);
        Assert.Equal(5, lines.Length);
        Assert.Equal("新增：启动时检查更新（可在设置中关闭）", lines[0]);
        Assert.Equal("编辑器新增查找 / 替换条（Ctrl+F / Ctrl+H / F3）", lines[1]);
        // [text](url) collapses to text, trailing punctuation survives.
        Assert.Equal("修复跨便签复制图片后引用丢失（详见 发布页）的问题", lines[2]);
        Assert.Equal("以下为 1.5.0 新增：", lines[3]);
        Assert.Equal("全新「关于 QNote」页", lines[4]);
    }

    [Fact]
    public void CrlfLineEndings_AreHandled()
    {
        var body = TemplateBody.Replace("\n", "\r\n");
        Assert.Equal(ReleaseNotes.ExtractChangelog(TemplateBody), ReleaseNotes.ExtractChangelog(body));
    }

    [Fact]
    public void MissingSection_ReturnsNull()
    {
        Assert.Null(ReleaseNotes.ExtractChangelog("## 下载哪个变体？\n\n- something else"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void EmptyBody_ReturnsNull(string? body) => Assert.Null(ReleaseNotes.ExtractChangelog(body));

    [Fact]
    public void EmptySection_ReturnsNull()
    {
        var body = "intro\n\n## 更新内容\n\n## 下载哪个变体？\n";
        Assert.Null(ReleaseNotes.ExtractChangelog(body));
    }

    [Fact]
    public void TextBeforeSectionHeader_IsIgnored()
    {
        // The intro paragraph (non-bullet lines) before the heading must not leak in.
        var body = "QNote 1.5.1 修复版。\n\n## 更新内容\n\n- 第一条\n";
        Assert.Equal("第一条", ReleaseNotes.ExtractChangelog(body));
    }

    [Fact]
    public void LineCount_IsCapped()
    {
        var body = "## 更新内容\n" + string.Concat(Enumerable.Range(1, 30).Select(i => $"- 条目 {i}\n"));

        var result = ReleaseNotes.ExtractChangelog(TemplateBody, maxLines: 3);
        Assert.Equal(3, result!.Split(Environment.NewLine).Length);

        var capped = ReleaseNotes.ExtractChangelog(body, maxLines: 5);
        Assert.Equal(5, capped!.Split(Environment.NewLine).Length);
        Assert.Equal("条目 5", capped.Split(Environment.NewLine)[^1]);
    }

    [Fact]
    public void MalformedLink_IsLeftAsIs()
    {
        var body = "## 更新内容\n\n- broken [link and **bold** stay\n";
        Assert.Equal("broken [link and bold stay", ReleaseNotes.ExtractChangelog(body));
    }
}
