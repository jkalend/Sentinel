param(
    [string]$VmDir = "G:\VM\Sentinel-QEMU",
    [string]$IsoDir = "G:\ISO",
    [string]$WindowsIso = "Windows11EnterpriseEval-26H2-x64-en-us.iso"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath (Join-Path $VmDir "sentinel-test.iso"))) {
    throw "sentinel-test.iso is missing from $VmDir"
}
if (-not (Test-Path -LiteralPath (Join-Path $VmDir "sentinel.qcow2"))) {
    throw "sentinel.qcow2 is missing from $VmDir"
}
if (-not (Test-Path -LiteralPath (Join-Path $VmDir "sentinel-out.img"))) {
    throw "sentinel-out.img is missing from $VmDir"
}
if (-not (Test-Path -LiteralPath (Join-Path $IsoDir $WindowsIso))) {
    throw "Windows ISO is missing from $IsoDir"
}

docker rm -f sentinel-qemu-1 2>$null | Out-Null
Remove-Item -LiteralPath (Join-Path $VmDir "serial.log") -ErrorAction SilentlyContinue

docker run -d --name sentinel-qemu-1 `
    --device /dev/kvm `
    -p 127.0.0.1:5903:5903 `
    -p 127.0.0.1:4447:4447 `
    -v "${VmDir}:/vm" `
    -v "${IsoDir}:/iso:ro" `
    sentinel-qemu `
    qemu-system-x86_64 `
    -machine pc `
    -accel kvm `
    -cpu host `
    -smp 4 `
    -m 6144 `
    -drive file=/vm/sentinel.qcow2,format=qcow2,if=ide,index=0 `
    -drive "file=/iso/$WindowsIso",media=cdrom,readonly=on,if=ide,index=1 `
    -drive file=/vm/sentinel-test.iso,media=cdrom,readonly=on,if=ide,index=2 `
    -drive file=/vm/sentinel-out-part.img,format=raw,if=none,id=out `
    -device ich9-ahci,id=ahci `
    -device ide-hd,drive=out,bus=ahci.0 `
    -boot order=d,menu=off `
    -display none `
    -vnc :3 `
    -serial file:/vm/serial.log `
    -monitor tcp:127.0.0.1:4447,server,nowait `
    -name Sentinel-Windows11

Write-Output "started sentinel-qemu-1"
