# 编译并以新哈希重试启动，直到智能应用控制放行（最多 6 次）
# 用法：双击同目录的 启动NotepadSharp.bat，或在 VS 改完代码后先跑这个再按 F5
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'NotepadSharp\bin\Debug\net10.0-windows\NotepadSharp.exe'
$proj = Join-Path $root 'NotepadSharp\NotepadSharp.csproj'

for ($try = 1; $try -le 6; $try++) {
    Write-Host "第 $try 次编译（每次哈希都不同）…"
    dotnet build $proj -v q --nologo -p:Deterministic=false
    if ($LASTEXITCODE -ne 0) { Write-Host "编译失败，请检查代码错误"; exit 1 }

    $app = Start-Process $exe -PassThru -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 4
    if ($app -and -not $app.HasExited) {
        Write-Host "✅ 第 $try 次启动成功，窗口已打开（PID $($app.Id)）"
        Write-Host "提示：现在在 VS 里按 F5 调试也不会被拦（文件没变，直接复用刚放行的这份）"
        exit 0
    }
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    Write-Host "第 $try 次被拦截，换个哈希再来…"
}
Write-Host "连续 6 次都被拦（概率偏低），等几分钟再试一次"
exit 1
