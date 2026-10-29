# P2 的 UI 冒烟测试（注释三件套 / Tab 设置 / 视图开关 / URL 热点 / 缩进参考线持久化）：
# 真启动 NotepadSharp，用 UIA 点菜单（不注入键盘，不抢前台输入），
# 直接对 Scintilla 子窗口收发 SCI_ 消息来摆内容、读结果。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File dev/uitest/smoke.ps1 [-Shot out.png]
param(
    [string]$Exe = "D:\CodeRepository\Github\NotepadSharp\NotepadSharp\bin\Debug\net10.0-windows\NotepadSharp.exe",
    [string]$Shot = ""
)

$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 默认按本地代码页输出，这里的用例名全是中文，先切到 UTF-8 才看得清
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, StringBuilder l);

    // 跨进程 SendMessage 的 lParam 是「调用方进程」的指针，目标进程按自己地址空间解引用会读到垃圾，
    // 甚至把对面写坏（实测：之后本进程的 UIA 全部抛 ElementNotAvailableException / E_UNEXPECTED）。
    // SCI_GETTEXT/SCI_SETTEXT 带指针，所以缓冲区必须用 VirtualAllocEx 开在目标进程里。
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, int size, int allocType, int protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, int size, int freeType);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr baseAddr, byte[] buf, int size, out int read);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr baseAddr, byte[] buf, int size, out int written);

    static IntPtr proc; static IntPtr remote;
    public static void Attach(int pid) {
        proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid);   // VM_OPERATION|VM_READ|VM_WRITE
        remote = VirtualAllocEx(proc, IntPtr.Zero, 1 << 20, 0x1000 | 0x2000, 0x04); // COMMIT|RESERVE, READWRITE
    }
    public static void Detach() {
        if (remote != IntPtr.Zero) VirtualFreeEx(proc, remote, 0, 0x8000);          // MEM_RELEASE
        remote = IntPtr.Zero; proc = IntPtr.Zero;
    }
    // Scintilla 文档是 UTF-8；返回长度按 SCI_GETTEXTLENGTH 给的字节数取
    public static string GetRemoteText(IntPtr h, int byteLen) {
        byte[] inBuf = new byte[byteLen];
        SendMessage(h, 2182, new IntPtr(byteLen + 1), remote);      // SCI_GETTEXT
        int rd; ReadProcessMemory(proc, remote, inBuf, byteLen, out rd);
        return System.Text.Encoding.UTF8.GetString(inBuf, 0, byteLen);
    }
    public static void PutRemoteText(IntPtr h, string s) {
        byte[] data = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        int wr; WriteProcessMemory(proc, remote, data, data.Length, out wr);
        SendMessage(h, 2181, IntPtr.Zero, remote);                  // SCI_SETTEXT
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    const uint GW_CHILD = 5, GW_HWNDNEXT = 2;

    public static IntPtr FindScintilla(IntPtr parent) {
        for (IntPtr c = GetWindow(parent, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT)) {
            var sb = new StringBuilder(256);
            GetClassName(c, sb, 256);
            // WinForms 承载的原生类名是 WindowsForms10.Scintilla.app.0.<hash>，不是裸 "Scintilla"
            if (sb.ToString().Contains("Scintilla") && IsWindowVisible(c)) return c;
            IntPtr deep = FindScintilla(c);
            if (deep != IntPtr.Zero) return deep;
        }
        return IntPtr.Zero;
    }

    public static IntPtr FindTopWindow(int pid, string titlePart) {
        IntPtr best = IntPtr.Zero;
        for (IntPtr h = GetWindow(GetDesktopWindow(), GW_CHILD); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT)) {
            int p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) continue;
            var sb = new StringBuilder(512);
            // 标题用 GetWindowText 取
            if (GetWindowText(h, sb, 512) > 0 && sb.ToString().Contains(titlePart)) { best = h; break; }
        }
        return best;
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
}
'@

# Scintilla.h 消息号
$SCI_SETTEXT = 2181; $SCI_GETTEXT = 2182; $SCI_GETTEXTLENGTH = 2183
$SCI_SETSEL = 2160; $SCI_GETUSETABS = 2125; $SCI_GETTABWIDTH = 2121
$SCI_GETEOLMODE = 2030          # SC_EOL_CRLF=0 SC_EOL_CR=1 SC_EOL_LF=2
$SCI_GETCURRENTPOS = 2008
$WM_CHAR = 0x0102; $WM_KEYDOWN = 0x0100; $WM_KEYUP = 0x0101

function Get-SciText($h) {
    $len = [W]::SendMessage($h, $SCI_GETTEXTLENGTH, [IntPtr]0, [IntPtr]0).ToInt64()
    return [W]::GetRemoteText($h, [int]$len)
}
function Set-SciText($h, [string]$s) {
    [W]::PutRemoteText($h, $s)
}
function Sci($h, [int]$msg) { [W]::SendMessage($h, $msg, [IntPtr]0, [IntPtr]0).ToInt64() }
function Show($s) { $s.Replace("`r", "\r").Replace("`n", "\n").Replace("`t", "\t") }

$script:fail = 0
function Check([string]$name, $expected, $actual) {
    if ("$expected" -eq "$actual") { Write-Host "PASS $name" }
    else {
        $script:fail++
        $e = "$expected".Replace("`r", "\r").Replace("`n", "\n").Replace("`t", "\t")
        $a = "$actual".Replace("`r", "\r").Replace("`n", "\n").Replace("`t", "\t")
        Write-Host "FAIL $name`n  期望 <$e>`n  实际 <$a>"
    }
}

# ==================== UIA：点菜单 ====================
$ScopeDesc = [System.Windows.Automation.TreeScope]::Descendants
$ScopeChild = [System.Windows.Automation.TreeScope]::Children
$CondMenuItem = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)

function Find-Menu($root, [string]$name) {
    foreach ($it in $root.FindAll($ScopeDesc, $CondMenuItem)) {
        if ($it.Current.Name -like "*$name*") { return $it }
    }
    return $null
}

function Invoke-Menu($root, [string[]]$names) {
    for ($i = 0; $i -lt ($names.Count - 1); $i++) {
        $parent = Find-Menu $root $names[$i]
        if (-not $parent) { throw "找不到上级菜单：$($names[$i])" }
        $ec = $null
        if (-not $parent.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$ec)) {
            throw "$($names[$i]) 不能展开"
        }
        $ec.Expand(); Start-Sleep -Milliseconds 350
    }
    $leaf = Find-Menu $root $names[$names.Count - 1]
    if (-not $leaf) { throw "找不到菜单项：$($names[$names.Count - 1])" }
    $inv = $null
    if (-not $leaf.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$inv)) {
        throw "$($names[$names.Count - 1]) 不能 Invoke"
    }
    $inv.Invoke(); Start-Sleep -Milliseconds 350
}

function Find-ChildByType($el, $controlType) {
    return $el.FindAll($ScopeDesc, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)))
}

function Click-Button($dlg, [string]$name) {
    foreach ($b in Find-ChildByType $dlg ([System.Windows.Automation.ControlType]::Button)) {
        if ($b.Current.Name -like "*$name*") {
            $inv = $null
            [void]$b.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$inv)
            $inv.Invoke(); Start-Sleep -Milliseconds 400; return
        }
    }
    throw "对话框里没有按钮：$name"
}

# UIA 连不上时抛 ElementNotAvailableException（内部 2 秒超时）。窗口句柄出现得很早，
# 那时应用还在初始化，消息循环没空应答，所以要重试到能连上为止。
function Get-Root($hwnd) {
    for ($i = 0; $i -lt 40; $i++) {
        try { return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) }
        catch { Start-Sleep -Milliseconds 500 }
    }
    throw "UIA 连不上窗口 $hwnd"
}

# ==================== 启动 ====================
$tmp = Join-Path $env:TEMP ("uitest-" + [guid]::NewGuid().ToString("N") + ".txt")
Set-Content -Path $tmp -Value "placeholder" -NoNewline

# 后半段要正常关窗（触发 SaveSettings 落盘）来验缩进参考线持久化。为了让「出厂值」断言成立、
# 又不在用户机器上留测试痕迹，启动前先把真实设置挪走，跑完（含异常中止）再原样放回。
$settingsPath = Join-Path $env:APPDATA "NotepadSharp\settings.json"
$settingsBak = "$settingsPath.uitestbak"
if (Test-Path $settingsBak) { Move-Item $settingsBak $settingsPath -Force }   # 上次跑崩留下的
$hadSettings = Test-Path $settingsPath
if ($hadSettings) { Move-Item $settingsPath $settingsBak -Force }
function Restore-Settings {
    if ($hadSettings) {
        if (Test-Path $settingsBak) { Move-Item $settingsBak $settingsPath -Force }
    } else {
        Remove-Item $settingsPath -ErrorAction SilentlyContinue
    }
    Remove-Item $settingsBak -ErrorAction SilentlyContinue
}
function Get-SettingProp([string]$name) {
    if (-not (Test-Path $settingsPath)) { return $null }
    return (Get-Content $settingsPath -Raw | ConvertFrom-Json).$name
}
function Close-MainWindow($rootEl) {
    $wp = $null
    if (-not $rootEl.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$wp)) {
        throw "主窗口不支持 WindowPattern，关不掉"
    }
    $wp.Close()
}

$p2 = $null
$p = Start-Process -FilePath $Exe -ArgumentList "`"$tmp`"" -PassThru
trap {
    if ($p) { try { $p.Kill() } catch { } }
    if ($p2) { try { $p2.Kill() } catch { } }
    Restore-Settings
    if ($tmp) { Remove-Item $tmp -ErrorAction SilentlyContinue }
    Write-Host "中止：$($_.Exception.Message)"
    break
}
Write-Host "进程 $($p.Id) 已启动，等窗口…"
[W]::Attach($p.Id)      # 在被测进程里开一块缓冲区，供带指针的 SCI_ 消息用
$hMain = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $hMain -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hMain = $p.MainWindowHandle }
}
if ($hMain -eq [IntPtr]::Zero) { throw "主窗口没出来" }
[W]::ShowWindow($hMain, 3) | Out-Null      # 最大化，保证编辑器句柄真的建好
Start-Sleep -Milliseconds 1500

$hSci = [W]::FindScintilla($hMain)
if ($hSci -eq [IntPtr]::Zero) { throw "没找到可见的 Scintilla 子窗口" }
Write-Host "编辑器 HWND = $hSci，基线内容 = <$(Show (Get-SciText $hSci))>"

$root = Get-Root $hMain

# ==================== 纯文本（ND2 的 L_TXT → 行注释符 #） ====================
# 无选区时 doBlockComment 只覆盖光标那一行（selStartLine == selEndLine），前缀插在缩进之后不是列 0
Set-SciText $hSci "aaa`nbbb`n"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]0) | Out-Null
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "txt 无选区：只注释光标所在行" "# aaa`nbbb`n" (Get-SciText $hSci)

Set-SciText $hSci "# aaa`n# bbb`n"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]0) | Out-Null
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "txt 无选区：再按只解开光标所在行" "aaa`n# bbb`n" (Get-SciText $hSci)

Set-SciText $hSci "  aaa"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]0) | Out-Null
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "txt 前缀落在缩进之后（LINEINDENTPOSITION）" "  # aaa" (Get-SciText $hSci)

# 选区行范围：ND2:2094-2097 —— 选区尾正好落在下一行行首时那一行不算
Set-SciText $hSci "aaa`nbbb`n"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]4) | Out-Null
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "txt 选区止于下一行行首：那一行不加" "# aaa`nbbb`n" (Get-SciText $hSci)

Set-SciText $hSci "aaa`nbbb`n"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]5) | Out-Null
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "txt 选区伸进第二行：两行都加" "# aaa`n# bbb`n" (Get-SciText $hSci)

Set-SciText $hSci "# aaa`n# bbb`n"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]12) | Out-Null
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "txt 整段选中：两行都解开" "aaa`nbbb`n" (Get-SciText $hSci)

Set-SciText $hSci "aaa`nbbb`n"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]0) | Out-Null
Invoke-Menu $root @("编辑", "区块注释")
Check "txt 无成对符号：区块注释转 doBlockComment(comment)" "# aaa`nbbb`n" (Get-SciText $hSci)

Set-SciText $hSci "# aaa`n# bbb`n"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]0) | Out-Null
Invoke-Menu $root @("编辑", "清除区块注释")
Check "txt 清除区块注释：转 doBlockComment(uncomment) 同样只动一行" "aaa`n# bbb`n" (Get-SciText $hSci)

# ==================== 回车自动缩进（ND2 setAutoIndent(true) → maintainIndentation） ====================
Check "出厂 EolMode = CRLF（ND2 scintillaeditview.cpp:1141）" 0 (Sci $hSci $SCI_GETEOLMODE)
# 只发 WM_CHAR 会被 Scintilla 当普通字符插一个裸 CR，必须走 WM_KEYDOWN→WM_CHAR→WM_KEYUP 才是真回车
function Press-Enter {
    [void][W]::SendMessage($hSci, $WM_KEYDOWN, [IntPtr]13, [IntPtr]0x001C0001)
    [void][W]::SendMessage($hSci, $WM_CHAR, [IntPtr]13, [IntPtr]0)
    [void][W]::SendMessage($hSci, $WM_KEYUP, [IntPtr]13, [IntPtr]0xC01E0001)
}
Set-SciText $hSci "    foo"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]7, [IntPtr]7) | Out-Null
Press-Enter
$afterEnter = (Get-SciText $hSci).Replace("`r`n", "`n")
Check "回车继承上一行缩进（出厂 UseTabs=真 TAB，缩进 4 存成一个制表符）" "    foo`n`t" $afterEnter
Check "缩进补齐后光标跟着后移（ND2 autoIndentLine 的 selectionStartMove）" 10 (Sci $hSci $SCI_GETCURRENTPOS)

Set-SciText $hSci "    abc`n`ndef"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]8, [IntPtr]8) | Out-Null
Press-Enter
Check "上一行是空行时继续往上找有内容的行（qsciscintilla.cpp:820-831）" "    abc`n`n`t`ndef" ((Get-SciText $hSci).Replace("`r`n", "`n"))

# ==================== 切到 HTML：没有行注释符 → Ctrl+Q 走单行区块注释 ====================
Invoke-Menu $root @("语法", "HTML")

Set-SciText $hSci "aaa`n"
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "html 行注释即单行区块注释" "<!-- aaa -->`n" (Get-SciText $hSci)

Set-SciText $hSci "<!-- aaa -->`n"
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "html 再按一次把区块拆开" "aaa`n" (Get-SciText $hSci)

Set-SciText $hSci "x <!-- y --> z"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]4, [IntPtr]8) | Out-Null
Invoke-Menu $root @("编辑", "清除区块注释")
Check "html 流式注释：选区夹住一对时拆开两侧（含挨着的空格）" "x y z" (Get-SciText $hSci)

Set-SciText $hSci "aaa"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]3) | Out-Null
Invoke-Menu $root @("编辑", "区块注释")
Check "html 区块注释包住选区" "<!-- aaa -->" (Get-SciText $hSci)

# ==================== Tab 设置 ====================
Check "出厂 TabWidth = ND2 tablens 种子 4" 4 (Sci $hSci $SCI_GETTABWIDTH)
Check "出厂 UseTabs = ND2 tabnouse 种子 0（仍打真 TAB）" 1 (Sci $hSci $SCI_GETUSETABS)

Invoke-Menu $root @("设置", "Tab 设置")
$hDlg = [IntPtr]::Zero
for ($i = 0; $i -lt 10 -and $hDlg -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 300
    $hDlg = [W]::FindTopWindow($p.Id, "Tab 设置")
}
if ($hDlg -eq [IntPtr]::Zero) { throw "Tab 设置窗口没出来" }
$dlg = [System.Windows.Automation.AutomationElement]::FromHandle($hDlg)

$boxes = Find-ChildByType $dlg ([System.Windows.Automation.ControlType]::CheckBox)
if ($boxes.Count -lt 1) { throw "Tab 设置里没有复选框" }
$tog = $null
[void]$boxes[0].TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tog)
$tog.Toggle(); Start-Sleep -Milliseconds 250
Click-Button $dlg "确定"
Check "勾上「使用空格替换tab」并关窗后落到编辑器" 0 (Sci $hSci $SCI_GETUSETABS)

# 改回默认，别把测试痕迹留在 settings.json
Invoke-Menu $root @("设置", "Tab 设置")
$hDlg = [W]::FindTopWindow($p.Id, "Tab 设置")
if ($hDlg -eq [IntPtr]::Zero) { throw "第二次打开 Tab 设置失败" }
$dlg = [System.Windows.Automation.AutomationElement]::FromHandle($hDlg)
$boxes = Find-ChildByType $dlg ([System.Windows.Automation.ControlType]::CheckBox)
$tog = $null
[void]$boxes[0].TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tog)
if ($tog.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
    $tog.Toggle(); Start-Sleep -Milliseconds 250
}
Click-Button $dlg "确定"
Check "复选框回填的是当前值（第二次打开时是勾着的）" 1 (Sci $hSci $SCI_GETUSETABS)

# ==================== 视图开关：blank 位域三态 / 自动换行 / 缩进参考线 / 工具栏 ====================
$SCI_GETVIEWWS = 2020
$SCI_GETVIEWEOL = 2355
$SCI_GETWRAPMODE = 2269
$SCI_GETINDENTATIONGUIDES = 2133
$SCI_INDICATORALLONFOR = 2506
$SCI_INDICATORSTART = 2508
$SCI_INDICATOREND = 2509

# indicator 号、位置都在 w/l 里，零参数版 Sci 不够用
function Sci2($h, [int]$msg, [int]$w, [int]$l) {
    return [W]::SendMessage($h, $msg, [IntPtr]$w, [IntPtr]$l).ToInt32()
}

Check "出厂 blank=0：空格和行尾都不显示" "0/0" "$(Sci $hSci $SCI_GETVIEWWS)/$(Sci $hSci $SCI_GETVIEWEOL)"

Invoke-Menu $root @("视图", "显示符号", "显示空格")
Check "「显示空格」只置 1 位（blank=1）" "1/0" "$(Sci $hSci $SCI_GETVIEWWS)/$(Sci $hSci $SCI_GETVIEWEOL)"
Invoke-Menu $root @("视图", "显示符号", "显示行尾")
Check "再开行尾 → blank=3 两位都亮" "1/1" "$(Sci $hSci $SCI_GETVIEWWS)/$(Sci $hSci $SCI_GETVIEWEOL)"
Invoke-Menu $root @("视图", "显示符号", "显示所有")
Check "blank 已是 3 时点「显示所有」整体清零" "0/0" "$(Sci $hSci $SCI_GETVIEWWS)/$(Sci $hSci $SCI_GETVIEWEOL)"
Invoke-Menu $root @("视图", "显示符号", "显示所有")
Check "blank 为 0 时点「显示所有」直接全开" "1/1" "$(Sci $hSci $SCI_GETVIEWWS)/$(Sci $hSci $SCI_GETVIEWEOL)"
Invoke-Menu $root @("视图", "显示符号", "显示空格")
Check "全开状态下关「显示空格」只清 1 位（blank=2）" "0/1" "$(Sci $hSci $SCI_GETVIEWWS)/$(Sci $hSci $SCI_GETVIEWEOL)"
Invoke-Menu $root @("视图", "显示符号", "显示行尾")
Check "再关行尾回 blank=0" "0/0" "$(Sci $hSci $SCI_GETVIEWWS)/$(Sci $hSci $SCI_GETVIEWEOL)"

Check "出厂不换行（SC_WRAP_NONE=0）" 0 (Sci $hSci $SCI_GETWRAPMODE)
Invoke-Menu $root @("视图", "自动换行")
Check "自动换行用 WrapCharacter（ND2 ccnotepad.cpp:6704）" 2 (Sci $hSci $SCI_GETWRAPMODE)
Invoke-Menu $root @("视图", "自动换行")
Check "再点回 SC_WRAP_NONE" 0 (Sci $hSci $SCI_GETWRAPMODE)

Check "出厂缩进参考线=SC_IV_NONE 0（ND2 s_indent 种子 0）" 0 (Sci $hSci $SCI_GETINDENTATIONGUIDES)
Invoke-Menu $root @("视图", "显示缩进参考线")
Check "打开缩进参考线：非 Python 走 SC_IV_LOOKBOTH=3" 3 (Sci $hSci $SCI_GETINDENTATIONGUIDES)
Invoke-Menu $root @("语法", "Python")
Check "切成 Python 后自动换成 SC_IV_LOOKFORWARD=2（ND2 scintillaeditview.cpp:997）" 2 (Sci $hSci $SCI_GETINDENTATIONGUIDES)
Invoke-Menu $root @("视图", "显示缩进参考线")
Check "关掉参考线回 SC_IV_NONE" 0 (Sci $hSci $SCI_GETINDENTATIONGUIDES)

# 托管位置是 UTF-16 码元位：中文行上做行操作，若谁又混进 UTF-8 字节换算，插进去的位置就会偏
Set-SciText $hSci "中文 aaa`nbbb"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]0) | Out-Null
Invoke-Menu $root @("编辑", "添加/删除当行注释")
Check "中文行注释：前缀落行首，中文不按字节错位" "# 中文 aaa`nbbb" (Get-SciText $hSci)

$rTop = New-Object W+RECT
[void][W]::GetWindowRect($hSci, [ref]$rTop)
Invoke-Menu $root @("视图", "显示工具栏")
Start-Sleep -Milliseconds 600
$rHide = New-Object W+RECT
[void][W]::GetWindowRect($hSci, [ref]$rHide)
Check "隐藏工具栏后编辑器整体上移" "true" ($rHide.T -lt $rTop.T)
Invoke-Menu $root @("视图", "显示工具栏")
Start-Sleep -Milliseconds 600
$rBack = New-Object W+RECT
[void][W]::GetWindowRect($hSci, [ref]$rBack)
Check "再点一次工具栏回来，编辑器回原位" $rTop.T $rBack.T

# ==================== URL 热点（showweb） ====================
# 文本 27 字节：中(3)+空格(1)+https://example.com(19)+空格(1)+尾(3)，URL 落在字节 4..22
Set-SciText $hSci "中 https://example.com 尾"
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]0, [IntPtr]0) | Out-Null
Start-Sleep -Milliseconds 600
Check "出厂显示网站=关：有 URL 也不画热点" "false" (((Sci2 $hSci $SCI_INDICATORALLONFOR 5 0) -band 0x800) -ne 0)
Invoke-Menu $root @("视图", "显示网站（不推荐开启）")
# 开关本身不动编辑器，靠一次选区变化把 UPDATEUI 逼出来，热点就是在 UPDATEUI 里画的
[W]::SendMessage($hSci, $SCI_SETSEL, [IntPtr]27, [IntPtr]0) | Out-Null
Start-Sleep -Milliseconds 900
Check "热点起止按 UTF-8 字节算，中文前缀不跑偏" "4/23" "$(Sci2 $hSci $SCI_INDICATORSTART 11 5)/$(Sci2 $hSci $SCI_INDICATOREND 11 5)"
Check "URL 区间内挂着 11 号 indicator" "true" (((Sci2 $hSci $SCI_INDICATORALLONFOR 5 0) -band 0x800) -ne 0)
Check "URL 之外的字符不挂 11 号" "false" (((Sci2 $hSci $SCI_INDICATORALLONFOR 0 0) -band 0x800) -ne 0)
Invoke-Menu $root @("视图", "显示网站（不推荐开启）")
Start-Sleep -Milliseconds 400
Check "关掉显示网站后残影还在（ND2 切开关不重算，照抄）" "4/23" "$(Sci2 $hSci $SCI_INDICATORSTART 11 5)/$(Sci2 $hSci $SCI_INDICATOREND 11 5)"

# ==================== 缩进参考线持久化（ND2 indent 键，重开按设置恢复） ====================
Invoke-Menu $root @("视图", "显示缩进参考线")
Check "关窗前再打开缩进参考线（Python 下是 LOOKFORWARD）" 2 (Sci $hSci $SCI_GETINDENTATIONGUIDES)
# 保存清脏标记：直接发 SCI_SETSAVEPOINT 不触发 SCN_SAVEPOINTREACHED（实测），
# 关窗会弹「未保存的文件」把测试卡死；走真实的保存命令，顺带也算覆盖了这一路
Invoke-Menu $root @("文件", "保存")
Start-Sleep -Milliseconds 400
Close-MainWindow $root
if (-not $p.WaitForExit(15000)) { throw "第一个实例没退干净" }
Check "正常关窗后 indent 落到 settings.json" "True" (Get-SettingProp "ShowIndentGuide")

# 重开：新实例读设置，新文档按设置把参考线打开（ND2 ccnotepad.cpp:4314-4317）
$p2 = Start-Process -FilePath $Exe -ArgumentList "`"$tmp`"" -PassThru
[W]::Attach($p2.Id)
$hMain2 = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $hMain2 -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 500
    $p2.Refresh()
    if ($p2.MainWindowHandle -ne [IntPtr]::Zero) { $hMain2 = $p2.MainWindowHandle }
}
if ($hMain2 -eq [IntPtr]::Zero) { throw "第二个实例主窗口没出来" }
[W]::ShowWindow($hMain2, 3) | Out-Null      # 最大化，保证编辑器句柄真的建好
Start-Sleep -Milliseconds 2000
$hSci2 = [W]::FindScintilla($hMain2)
if ($hSci2 -eq [IntPtr]::Zero) { throw "第二个实例没找到 Scintilla 子窗口" }
Check "重开后按设置恢复参考线（txt 走 LOOKBOTH=3）" 3 (Sci $hSci2 $SCI_GETINDENTATIONGUIDES)

$root2 = Get-Root $hMain2
Invoke-Menu $root2 @("视图", "显示缩进参考线")
Check "重开后关掉回 SC_IV_NONE" 0 (Sci $hSci2 $SCI_GETINDENTATIONGUIDES)
Close-MainWindow $root2
if (-not $p2.WaitForExit(15000)) { throw "第二个实例没退干净" }
Check "再关窗后 indent 落盘为假" "False" (Get-SettingProp "ShowIndentGuide")

# ==================== 收尾 ====================
if ($Shot -ne "") {
    $r = New-Object W+RECT
    [void][W]::GetWindowRect($hMain, [ref]$r)
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $bmp.Save($Shot)
    Write-Host "截图：$Shot"
}

try { $p.Kill() } catch { }
Restore-Settings
Remove-Item $tmp -ErrorAction SilentlyContinue
Write-Host $(if ($script:fail -eq 0) { "全部通过" } else { "$($script:fail) 条失败" })
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
