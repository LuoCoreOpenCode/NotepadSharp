using System.ComponentModel;

namespace NotepadSharp.Models;

/// <summary>
/// 标签页模型：文档 + 标签头显示状态。标题模板直接绑定这里，脏标记/锁定变化时手动通知。
/// ——VS 停靠框架的 View.Title 放的就是本对象，于是标签头模板（标题、脏点、图钉、图标）
/// 的 DataContext 是它而非 <see cref="Document"/>：这里只暴露「显示用」的转发属性，
/// 让文档模型保持纯数据、不被界面绑定细节污染。
/// 由于 <see cref="Document"/> 不通知变化，状态改变必须由主窗口调用
/// <see cref="RefreshHeader"/> 主动推送，否则标签头会停留在旧文案。
/// </summary>
public sealed class DocumentLayoutItem : INotifyPropertyChanged
{
    /// <summary>
    /// 创建一个标签布局项，包住给定文档。
    /// </summary>
    /// <param name="document">该标签承载的文档模型（打开/新建流程刚建好的实例，标签期间不换）</param>
    public DocumentLayoutItem(Document document)
    {
        Document = document;
    }

    /// <summary>
    /// 本标签背后的文档模型（只读：标签生命周期内不换文档）。
    /// 主窗口用它在文档、标签项、编辑器三方之间做映射与反查；
    /// 需要读写路径、编码、脏标记等真实状态时都经由它，而不是这里的转发属性。
    /// </summary>
    public Document Document { get; }

    /// <summary>
    /// 标签头图标着色用的文件路径（转发 <see cref="Document.FilePath"/>）。
    /// 绑定在模板里接 <c>FileIconColorConverter</c>，按扩展名给出颜色；
    /// 另存为导致路径变化后，靠 <see cref="RefreshHeader"/> 重新通知才会换色。
    /// </summary>
    public string FilePath => Document.FilePath;

    /// <summary>
    /// 标签头显示的标题（转发 <see cref="Document.DisplayName"/>，含 " [hex]" 等后缀）。
    /// 同时是 <see cref="ToString"/> 的结果，供未套模板的场合兜底显示。</summary>
    public string Title => Document.DisplayName;

    /// <summary>
    /// 是否显示「未保存」脏点（转发 <see cref="Document.IsDirty"/>）。
    /// 置位方是编辑器的保存点回调，本类只负责在它变化后经 <see cref="RefreshHeader"/> 通知界面。</summary>
    public bool IsDirty => Document.IsDirty;

    /// <summary>
    /// 标签是否被锁定（转发 <see cref="Document.IsLocked"/>，决定图钉图标朝向与可否关闭）。
    /// 模板里的 DataTrigger 直接绑它，因此锁定/解锁后必须经 <see cref="RefreshHeader"/> 推送才会重算触发器。</summary>
    public bool IsLocked => Document.IsLocked;

    /// <summary>
    /// WPF 绑定订阅的属性变化通知；由 <see cref="Raise"/> 触发，声明为可空是因为无人绑定时它为空。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 把标签头关心的四个属性一次性通知一遍（标题、路径、脏点、锁定）。
    /// 主窗口在保存/另存为、改名、切换锁定、脏状态翻转之后调用它，
    /// 因为这些状态来自不做通知的 <see cref="Document"/>，不主动推送界面就不会刷新。
    /// </summary>
    public void RefreshHeader()
    {
        Raise(nameof(Title));
        Raise(nameof(FilePath));
        Raise(nameof(IsDirty));
        Raise(nameof(IsLocked));
    }

    /// <summary>
    /// 以标题作为对象的文本表示，方便调试输出与停靠框架取默认名。
    /// </summary>
    /// <returns><see cref="Title"/> 的当前值</returns>
    public override string ToString() => Title;

    /// <summary>
    /// 触发一次属性变化通知（没有订阅者时安全跳过）。
    /// </summary>
    /// <param name="name">属性名，一律用 <c>nameof(...)</c> 取，避免与属性名拼写不一致导致绑定不刷新</param>
    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
