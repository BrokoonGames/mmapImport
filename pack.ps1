# 本地打包：生成 top.brokoon.mmapimport.icpx（官方三文件清单 + 图标）
$ErrorActionPreference = "Stop"
$out = "src/bin/Release/net6.0-windows10.0.19041.0"
$pkg = "package"
New-Item -ItemType Directory -Force $pkg | Out-Null
foreach ($f in @("manifest.json","InkCanvas.Plugin.MmapImport.dll","InkCanvas.Plugin.MmapImport.deps.json","icon.png")) {
  Copy-Item (Join-Path $out $f) $pkg -Force
}
$icpx = "top.brokoon.mmapimport.icpx"
if (Test-Path "$pkg/$icpx") { Remove-Item "$pkg/$icpx" }
Compress-Archive -Path "$pkg/*" -DestinationPath "$pkg/$icpx"
Write-Host "打包完成: $pkg/$icpx"
