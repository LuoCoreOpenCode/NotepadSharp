; NotepadSharp 安装包脚本。不要直接跑这个文件，用仓库根目录的 build.ps1，
; 它会把版本号和 publish 目录通过 /D 传进来。
#ifndef MyAppVersion
  #error 请先用 build.ps1 编译（需要 /DMyAppVersion=x.y.z）
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define MyAppName "NotepadSharp"
#define MyAppExeName "NotepadSharp.exe"
; 全项目统一图标来源：NotepadSharp\Resources\Icons\NotepadSharp.ico
; （csproj 的 ApplicationIcon 已经把同一份图标嵌进 exe，所以快捷方式、右键菜单、
;   文件关联的 DefaultIcon 一律指向 exe 即可，无需再单独安装图标文件）
#define MyAppIcon "..\NotepadSharp\Resources\Icons\NotepadSharp.ico"

[Setup]
; AppId 一旦发布就不能改，卸载程序靠它认得自己
AppId={{7E1C4B0A-9F2D-4C63-8A55-2D7B41E0F9C6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Luo
DefaultDirName={autopf}\{#MyAppName}
; 目录选择页必须始终显示：全新安装给默认值；覆盖升级时预填上一次的目录（见 [Code]）。
; UsePreviousAppDir 必须关掉——开着的话 Inno 检测到已安装会直接跳过目录页，用户就没法改目录了
DisableDirPage=no
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
; 安装向导/卸载入口也使用同一个图标
SetupIconFile={#MyAppIcon}
OutputDir={#OutputDir}
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 允许「仅当前用户」安装，装到 %LOCALAPPDATA%\Programs，避免没有管理员权限时卡住
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
; 更新时自动关掉正在运行的实例，装完再拉起来
CloseApplications=yes
RestartApplications=yes

[Languages]
; Inno Setup 6 自带的 Languages\ 里没有简体中文，这份是从 jrsoftware/issrc 拉下来内联进仓库的，
; 放在脚本同级的 Languages\ 下，换机器编译不用再装翻译包。只留一种语言，向导里就不会出现语言下拉框
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"
Name: "editmenu"; Description: "添加右键菜单「用 NotepadSharp 编辑」"; GroupDescription: "关联方式："
Name: "fileassoc"; Description: "注册为文本类文件的「打开方式」候选"; GroupDescription: "关联方式："

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 让 notepadsharp.exe 从任意目录带文件名调用都能解析
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; \
  ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; \
  ValueType: string; ValueName: "Path"; ValueData: "{app}"

; 右键「用 NotepadSharp 编辑」：所有文件类型。父键带 uninsdeletekey 会连 command 子键一起清掉
Root: HKA; Subkey: "Software\Classes\*\shell\{#MyAppName}"; ValueType: string; \
  ValueName: ""; ValueData: "用 NotepadSharp 编辑"; Tasks: editmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\*\shell\{#MyAppName}"; ValueType: string; \
  ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: editmenu
Root: HKA; Subkey: "Software\Classes\*\shell\{#MyAppName}\command"; ValueType: string; \
  ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: editmenu

; ProgID + 各扩展名的 OpenWithProgids，只把自己登记成候选，不抢默认程序。
; 扩展名那一行必须用 uninsdeletevalue：整键删掉会连别的软件的关联一起毁掉
; 关联图标统一走 exe 内嵌图标（来源 NotepadSharp\Resources\Icons\NotepadSharp.ico），
; 以后新增任何扩展名关联都照这个写法，不要再引用别的图标文件
Root: HKA; Subkey: "Software\Classes\{#MyAppName}.Text"; ValueType: string; \
  ValueName: ""; ValueData: "{#MyAppName} 文本文件"; Tasks: fileassoc; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#MyAppName}.Text\DefaultIcon"; ValueType: string; \
  ValueName: ""; ValueData: """{app}\{#MyAppExeName}"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#MyAppName}.Text\shell\open\command"; ValueType: string; \
  ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; \
  ValueName: "{#MyAppName}.Text"; ValueData: ""; Tasks: fileassoc; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.log\OpenWithProgids"; ValueType: string; \
  ValueName: "{#MyAppName}.Text"; ValueData: ""; Tasks: fileassoc; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; \
  ValueName: "{#MyAppName}.Text"; ValueData: ""; Tasks: fileassoc; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.json\OpenWithProgids"; ValueType: string; \
  ValueName: "{#MyAppName}.Text"; ValueData: ""; Tasks: fileassoc; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.csv\OpenWithProgids"; ValueType: string; \
  ValueName: "{#MyAppName}.Text"; ValueData: ""; Tasks: fileassoc; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; \
  WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent
; 自动更新走 /VERYSILENT，没有勾选框可点，装完直接把程序拉回来
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Flags: nowait; Check: InstallWasSilent

; 卸载刻意保留 %LOCALAPPDATA%\NotepadSharp 里的用户配置：机器级安装时删某个用户的
; 配置目录属于越权，而且换机器时配置本来也带不走

[Code]
// 自包含发布已经把 .NET 运行时打进安装包，用户机器不需要预装任何框架，
// 也就不再需要装前检查运行时的逻辑了。

var
  PreviousDir: String;   // 上一次安装的目录；全新安装时为空串

// 从卸载注册表键里读上一次的安装目录（InstallLocation）。
// 机器级安装在 HKLM（64/32 位视图都试），用户级在 HKCU。
function GetPreviousInstallDir: String;
var
  Key: String;
begin
  Result := '';
  Key := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7E1C4B0A-9F2D-4C63-8A55-2D7B41E0F9C6}_is1';
  if not RegQueryStringValue(HKLM64, Key, 'InstallLocation', Result) then
  begin
    if not RegQueryStringValue(HKLM32, Key, 'InstallLocation', Result) then
    begin
      RegQueryStringValue(HKCU, Key, 'InstallLocation', Result);
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  PreviousDir := RemoveBackslashUnlessRoot(GetPreviousInstallDir);
  Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // UsePreviousAppDir 关掉之后目录页每次都会显示（升级时 Inno 默认会跳过它）。
  // 这里把默认值换成上一次的安装目录：交互安装时用户看得见、随时可以改；
  // 静默安装（自动更新走的路）不显示页面，但这段代码同样会执行，保证仍装回原位置。
  if (CurPageID = wpSelectDir) and (PreviousDir <> '') then
    WizardForm.DirEdit.Text := PreviousDir;
end;

function InstallWasSilent: Boolean;
begin
  Result := WizardSilent;
end;
