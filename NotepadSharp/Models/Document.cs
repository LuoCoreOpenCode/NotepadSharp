using NotepadSharp.Services;

namespace NotepadSharp.Models;

/// <summary>
/// 表示一个打开的文件/文档
/// </summary>
public class Document
{
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>当前文档编码（代码页，0 表示系统 ANSI）</summary>
    public int CodePage { get; set; } = EncodingService.CpUtf8;

    /// <summary>保存时是否写入 BOM</summary>
    public bool HasBom { get; set; }

    /// <summary>文档内容是否被改动（与保存点比较）</summary>
    public bool IsDirty { get; set; }

    /// <summary>上次保存/载入时磁盘上的修改时间与大小，用于外部变更判断</summary>
    public DateTime? DiskTimestamp { get; set; }
    public long DiskLength { get; set; }

    public bool IsReadOnly { get; set; }

    /// <summary>标签锁定：不允许关闭与切换编码</summary>
    public bool IsLocked { get; set; }

    public bool IsBigFile { get; set; }

    /// <summary>以十六进制视图方式打开的二进制文件</summary>
    public bool IsHexView { get; set; }

    public string EncodingName => EncodingService.GetDisplayName(CodePage, HasBom);

    public Document()
    {
        DisplayName = "新建文件";
    }

    public override string ToString() => DisplayName;
}
