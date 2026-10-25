using System.ComponentModel;

namespace NotepadSharp.Models;

/// <summary>
/// 标签页模型：文档 + 标签头显示状态。标题模板直接绑定这里，脏标记/锁定变化时手动通知。
/// </summary>
public sealed class DocumentLayoutItem : INotifyPropertyChanged
{
    public DocumentLayoutItem(Document document)
    {
        Document = document;
    }

    public Document Document { get; }

    public string FilePath => Document.FilePath;

    public string Title => Document.DisplayName;

    public bool IsDirty => Document.IsDirty;

    public bool IsLocked => Document.IsLocked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshHeader()
    {
        Raise(nameof(Title));
        Raise(nameof(FilePath));
        Raise(nameof(IsDirty));
        Raise(nameof(IsLocked));
    }

    public override string ToString() => Title;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
