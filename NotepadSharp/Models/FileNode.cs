namespace NotepadSharp.Models;

/// <summary>
/// 文件树节点
/// </summary>
public class FileNode
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public List<FileNode> Children { get; set; } = new();

    public FileNode(string fullPath, string name, bool isDirectory)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
    }

    public override string ToString() => Name;
}
