namespace NotepadSharp.Models;

/// <summary>
/// 文件树节点
/// ——左侧资源树里的一行，既代表目录也代表文件，由主窗口一次性递归扫描目录建出整棵树。
/// 纯数据（不做属性通知）：磁盘内容变化后不是原地更新，而是重建节点再替换，
/// 因此界面上的图标/工具提示等只依赖层级数据模板的一次性绑定。
/// </summary>
public class FileNode
{
    /// <summary>
    /// 完整路径：目录节点是目录全路径，文件节点是文件全路径（大小写不敏感）。
    /// 打开、重命名、删除、在资源管理器中显示等操作都直接拿它去访问磁盘；
    /// 模板里把它当工具提示，<c>FileIconColorConverter</c> 也从它取扩展名定图标颜色。
    /// </summary>
    public string FullPath { get; set; } = string.Empty;
    /// <summary>
    /// 显示名（路径的最后一段，不含目录分隔符）；盘符根目录取不到名字时由建节点的逻辑退回用完整路径填充。
    /// 树里每一行的文字就是它，同时也是 <see cref="ToString"/> 的结果。
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// 是否为目录节点。建节点时由 <c>Directory.GetDirectories</c> / <c>GetFiles</c> 的来源决定，之后不再变；
    /// 模板的 DataTrigger 用它切换文件夹图标与展开行为，着色逻辑则据此优先返回文件夹色。
    /// </summary>
    public bool IsDirectory { get; set; }
    /// <summary>
    /// 子节点集合（仅目录节点有意义）：建父节点时递归填充，目录在前、文件在后，各自按路径名不区分大小写排序。
    /// 文件节点保持为空集合；模板的 <c>ItemsSource</c> 绑定的就是它。
    /// </summary>
    public List<FileNode> Children { get; set; } = new();

    /// <summary>
    /// 构造一个文件树节点（目录或文件由 <paramref name="isDirectory"/> 决定）。
    /// </summary>
    /// <param name="fullPath">磁盘完整路径，作为后续所有文件操作的依据</param>
    /// <param name="name">树中显示的名字，通常是路径最后一段</param>
    /// <param name="isDirectory">true 表示目录（可展开、图标为文件夹）；false 表示文件</param>
    public FileNode(string fullPath, string name, bool isDirectory)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
    }

    /// <summary>
    /// 以显示名作为节点的文本表示：未显式设置 <c>DisplayMemberPath</c>/模板时树也能显示，调试输出同样可读。
    /// </summary>
    /// <returns><see cref="Name"/> 的当前值</returns>
    public override string ToString() => Name;
}
