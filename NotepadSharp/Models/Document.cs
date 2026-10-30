using NotepadSharp.Services;

namespace NotepadSharp.Models;

/// <summary>
/// 表示一个打开的文件/文档
/// ——一个标签页对应一个实例，是「文档侧」的纯数据模型（正文本身存放在对应的编辑器控件里）：
/// 路径、显示名、编码、脏标记、磁盘快照与各类行为标志都集中在这里，
/// 主窗口的打开/保存/另存为/编码转换/外部变更检测，以及停靠标签
/// <see cref="DocumentLayoutItem"/> 都以它为唯一事实来源；对应 ND2 中一个编辑实例的元数据部分。
/// 注意：本类不实现 INotifyPropertyChanged，属性变化后需调用方主动刷新标签头与状态栏。
/// </summary>
public class Document
{
    /// <summary>
    /// 文档对应的磁盘绝对路径；空字符串（<see cref="string.Empty"/>）表示尚未落盘的新建文档。
    /// 由打开/另存为流程置位（另存为成功后改写为新路径）；
    /// 读取方包括保存、外部变更监控（FileSystemWatcher 回调按路径反查文档）、最近文件、
    /// 文件树跟随等，它们都先判空再工作；路径比较采用大小写不敏感方式，与 Windows 语义一致。
    /// </summary>
    public string FilePath { get; set; } = string.Empty;
    /// <summary>
    /// 标签页、对话框与提示文案里显示的名字：默认「新建文件」，打开后置为文件名（不含目录）。
    /// 十六进制视图会额外拼上 " [hex]" 后缀，批量新建时用「新建 N」编号区分；
    /// 只影响展示，不参与任何路径运算，因此可被重命名逻辑安全改写。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>当前文档编码（代码页，0 表示系统 ANSI）
    /// 默认 UTF-8（<see cref="EncodingService.CpUtf8"/>）；打开时取编码嗅探结果写入，
    /// 「编码」菜单的转换与「以指定编码重新载入」会改写它，保存时据此挑选编码器写盘；
    /// 十六进制视图固定用 <see cref="EncodingService.CpAnsi"/>，因为转储文本不参与转码。</summary>
    public int CodePage { get; set; } = EncodingService.CpUtf8;

    /// <summary>保存时是否写入 BOM
    /// 打开时由编码检测结果写入，「在文件头插入/删除 BOM」菜单项翻转它；
    /// 保存写盘与状态栏编码名（见 <see cref="EncodingName"/>）都依赖它。
    /// 只有本身带 BOM 的编码（UTF-8/UTF-16/UTF-32）取 true 才有实际意义。</summary>
    public bool HasBom { get; set; }

    /// <summary>文档内容是否被改动（与保存点比较）
    /// 由编辑器的「保存点变化」回调写入（未到保存点即为 true），载入、重新载入、
    /// 保存成功、撤销回到保存点都会让它回落到 false；
    /// 读取方为关闭确认弹窗、标签头脏点（经 <see cref="DocumentLayoutItem.IsDirty"/> 绑定）
    /// 以及外部变更提示的措辞。</summary>
    public bool IsDirty { get; set; }

    /// <summary>上次保存/载入时磁盘上的修改时间与大小，用于外部变更判断
    /// 本属性是「磁盘快照」的时间部分：打开、保存成功、以及用户对「外部已修改」选「否」之后刷新；
    /// 文件监控回调把它与当前磁盘值比对，一致就说明这次变化是本程序自己保存造成的，不必打扰用户。
    /// null 表示尚未建立快照（例如未落盘的新建文档、或十六进制视图未记录）。</summary>
    public DateTime? DiskTimestamp { get; set; }
    /// <summary>磁盘快照的大小部分：上次保存/载入时文件的字节数（用 long 承载超大文件）。
    /// 与 <see cref="DiskTimestamp"/> 一起参与外部变更判定：同一秒内的覆盖写入可能时间戳不变，
    /// 加上长度比对能识别这类「假相同」。</summary>
    public long DiskLength { get; set; }

    /// <summary>后台标签收到外部变更，等切过去再问（ND2 的 Modify_Outside 标志）
    /// 文件监控命中非活动文档时置位，避免在用户看不见的标签上弹窗；
    /// 切换标签后由「补问」逻辑消费：先清标志再复查快照，
    /// 挂起期间内容若被改回原样就不再弹出提示。</summary>
    public bool ExternalChangePending { get; set; }

    /// <summary>只读标志：为 true 时编辑器拒绝输入，写入类命令（保存、行操作、转码）同样被拒绝。
    /// 打开时按文件的只读属性初始化，十六进制视图强制为 true；
    /// 「文档→只读」菜单项翻转它，并同步编辑器状态、菜单勾选与状态栏文本。</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>标签锁定：不允许关闭与切换编码
    /// 由锁定标签命令（Ctrl+Alt+T）翻转，是「防手滑」的用户级开关，与磁盘只读属性无关；
    /// 变更后需刷新标签头，让图钉图标切换朝向（经 <see cref="DocumentLayoutItem.IsLocked"/> 绑定展示）。</summary>
    public bool IsLocked { get; set; }

    /// <summary>大文件标志：文件体积超过设置项 BigFileThresholdMb 对应的阈值时为 true。
    /// 打开流程确实按该阈值弹「载入会明显变慢」的确认，但判定用的是局部变量，
    /// 目前尚未回写本字段，因此它现在是留给各功能（全文高亮、逐行批处理等）降级的预留语义标记。</summary>
    public bool IsBigFile { get; set; }

    /// <summary>以十六进制视图方式打开的二进制文件
    /// 打开流程识别为已知二进制格式、或用户在「文本/十六进制」三选一里选了十六进制时置位；
    /// 内容是转储文本而非原文，故保存、编码转换与多数编辑命令都据此拒绝或跳过。</summary>
    public bool IsHexView { get; set; }

    /// <summary>供界面展示的编码名称（如「UTF-8」「ANSI」「CP936」，带 BOM 时追加后缀）。
    /// 只读计算属性：每次读取都转发编码服务的取名逻辑，
    /// 因此 <see cref="CodePage"/> 或 <see cref="HasBom"/> 改变后无需额外同步即可得到新名字。</summary>
    public string EncodingName => EncodingService.GetDisplayName(CodePage, HasBom);

    /// <summary>
    /// 构造一个尚未落盘的新建文档：显示名为「新建文件」，UTF-8 且不带 BOM，
    /// 无路径、无磁盘快照、各行为标志全为 false。
    /// 主窗口的新建流程会在此基础上把显示名改写为「新建 N」。
    /// </summary>
    public Document()
    {
        DisplayName = "新建文件";
    }

    /// <summary>
    /// 以显示名作为对象的文本表示：便于调试输出，也让未显式指定模板的列表控件能直接显示标签标题。
    /// </summary>
    /// <returns><see cref="DisplayName"/> 的当前值</returns>
    public override string ToString() => DisplayName;
}
