# Remove existing NetCoreCheck executables.
Foreach ($netCoreCheckFile in "NetCoreCheck.exe","NetCoreCheck_x64.exe","NetCoreCheck_x86.exe")
{
    If (Test-Path $netCoreCheckFile)
    {
        Remove-Item -Force $netCoreCheckFile
    }
}

# Download newest nuget packages containing NetCoreCheck.exe.
$netCoreCheckX64URL = "https://www.nuget.org/api/v2/package/Microsoft.NET.Tools.NETCoreCheck.x64"
$netCoreCheckX64Output = ".\Microsoft.NET.Tools.NETCoreCheck.x64.Latest.nupkg"
$netCoreCheckX86URL = "https://www.nuget.org/api/v2/package/Microsoft.NET.Tools.NETCoreCheck.x86"
$netCoreCheckX86Output = ".\Microsoft.NET.Tools.NETCoreCheck.x86.Latest.nupkg"
Invoke-WebRequest -Uri $netCoreCheckX64URL -OutFile $netCoreCheckX64Output
Invoke-WebRequest -Uri $netCoreCheckX86URL -OutFile $netCoreCheckX86Output

# Extract and rename executable files from nupkg.
Start-Process -Wait -NoNewWindow -FilePath "C:\Program Files\7-Zip\7z.exe" -ArgumentList "e Microsoft.NET.Tools.NETCoreCheck.x64.Latest.nupkg win-x64\NetCoreCheck.exe"
Rename-Item NetCoreCheck.exe NetCoreCheck_x64.exe
Start-Process -Wait -NoNewWindow -FilePath "C:\Program Files\7-Zip\7z.exe" -ArgumentList "e Microsoft.NET.Tools.NETCoreCheck.x86.Latest.nupkg win-x86\NetCoreCheck.exe"
Rename-Item NetCoreCheck.exe NetCoreCheck_x86.exe
