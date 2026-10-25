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

[Setup]
; AppId 一旦发布就不能改，卸载程序靠它认得自己
AppId={{7E1C4B0A-9F2D-4C63-8A55-2D7B41E0F9C6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Luo
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
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
// .NET 10 桌面运行时是框架依赖包的唯一外部前提，缺了程序起不来，装之前就提示清楚。
// dotnet 的安装信息写在 32 位视图（WOW6432Node）里，所以两个视图都要看。
function RuntimeMajorInView(Root: Integer; Path: string; MinorRequired: Integer): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Major: Integer;
  Dot: Integer;
begin
  Result := False;
  if not RegGetValueNames(Root, Path, Names) then Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    // 键值形如 "10.0.2"，也有区间写法 "8.0.0-8.0.5"，取首段主版本即可
    Dot := Pos('.', Names[I]);
    if Dot <= 1 then Continue;
    Major := StrToIntDef(Copy(Names[I], 1, Dot - 1), 0);
    if Major >= MinorRequired then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function DesktopRuntimePresent(MinorRequired: Integer): Boolean;
begin
  Result := RuntimeMajorInView(HKLM32,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', MinorRequired) or
    RuntimeMajorInView(HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', MinorRequired);
end;

function InstallWasSilent: Boolean;
begin
  Result := WizardSilent;
end;

procedure InitializeSetupWarning(const Msg: string);
begin
  // 静默安装（自动更新走的就是这条路）里弹框会把安装程序永久挂住，只记日志
  Log(Msg);
  if not WizardSilent then MsgBox(Msg, mbInformation, MB_OK);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if DesktopRuntimePresent(10) then Exit;
  InitializeSetupWarning('NotepadSharp 需要 .NET 10 桌面运行时（Microsoft Windows Desktop Runtime 10.x）。' + #13#10#13#10 +
    '安装程序会继续，但程序可能无法启动。' + #13#10 +
    '下载地址：https://dotnet.microsoft.com/download/dotnet/10.0');
end;
