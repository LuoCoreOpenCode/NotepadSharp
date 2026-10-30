using System.Windows;

// 本文件只做一件事：用 ThemeInfo 特性告诉 WPF「主题资源字典放在哪里」。
// 它是项目模板生成的程序集级元数据（编译后写进 assembly），
// 决定自定义控件在回退查找样式时去哪个程序集、哪个位置的资源字典里找，
// 与业务逻辑无关，一般不需要改动。
//
// 本项目的资源划分情况：窗口/控件样式与 VS 停靠主题都放在自身程序集里，
// 没有额外的「主题专属字典程序集」，所以下面两处位置分别是 None 与 SourceAssembly。
[assembly: ThemeInfo(
    // 第一个参数：主题专属（theme-specific）字典的位置。None = 不存在这类字典，
    // 也就是不做「按当前主题再去子目录找字典」这一步，避免运行期无效的文件探测。
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    // 第二个参数：通用（generic.xaml 一类）字典的位置。SourceAssembly = 就在本程序集内查找，
    // 于是自定义控件的默认样式能从 NotepadSharp 自己的资源里取到。
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
