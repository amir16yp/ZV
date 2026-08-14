param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,
    [string]$OutFile = "installer\ZVFiles.wxs"
)

$source = (Resolve-Path $SourceDir).Path.TrimEnd('\', '/')
$componentIndex = 1
$directoryIds = @{"" = "INSTALLFOLDER"}

function ConvertTo-WixId([string]$name) {
    return ($name -replace '[^A-Za-z0-9_]', '_') -replace '_+', '_'
}

function Get-DirectoryId([string]$relativeDir) {
    $key = $relativeDir.Trim('\', '/')
    if ($directoryIds.ContainsKey($key)) { return $directoryIds[$key] }
    $id = "dir_" + (ConvertTo-WixId ($key -replace '\\', '_'))
    $directoryIds[$key] = $id
    return $id
}

function New-WixTree([System.IO.DirectoryInfo]$dir, [string]$relativeDir) {
    $thisId = Get-DirectoryId $relativeDir
    $components = @()
    $subdirs = @()

    foreach ($file in $dir.GetFiles()) {
        $componentId = "cmp_{0:D6}" -f $script:componentIndex
        $fileId = "fil_{0:D6}" -f $script:componentIndex
        $components += "      <Component Id=`"$componentId`" Guid=`"*`">`n        <File Id=`"$fileId`" Source=`"$($file.FullName)`" KeyPath=`"yes`" />`n      </Component>"
        $script:componentIndex++
    }

    foreach ($sub in $dir.GetDirectories()) {
        $subRelative = if ($relativeDir -eq "") { $sub.Name } else { "$relativeDir\$($sub.Name)" }
        $subdirs += (New-WixTree $sub $subRelative)
    }

    if ($relativeDir -eq "") {
        return @(
            "  <Fragment>",
            "    <DirectoryRef Id=`"INSTALLFOLDER`">",
            ($components -join "`n"),
            ($subdirs -join "`n"),
            "    </DirectoryRef>",
            "  </Fragment>"
        ) -join "`n"
    }

    return @(
        "    <Directory Id=`"$thisId`" Name=`"$($dir.Name)`">",
        ($components -join "`n"),
        ($subdirs -join "`n"),
        "    </Directory>"
    ) -join "`n"
}

$root = Get-Item $source
$directoryFragment = New-WixTree $root ""

$componentRefs = @()
for ($i = 1; $i -lt $componentIndex; $i++) {
    $componentRefs += "      <ComponentRef Id=`"cmp_{0:D6}`" />" -f $i
}

$wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
$directoryFragment
  <Fragment>
    <ComponentGroup Id="ZVFiles">
$($componentRefs -join "`n")
    </ComponentGroup>
  </Fragment>
</Wix>
"@

$OutPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
$wxs | Out-File -FilePath $OutPath -Encoding utf8
Write-Host "Generated $OutPath with $($componentIndex - 1) file components."
