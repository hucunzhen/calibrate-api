; Inno Setup 6 — CalibOperatorCLI_Example 自包含 (win-x64) 安装包
; 使用前请先运行 publish-self-contained.bat 生成发布目录。
; 版本号由 git describe 提供，请用 compile-setup.ps1 编译（勿单独双击本脚本编译，否则为默认占位版本）。
;   powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\compile-setup.ps1
; 或手动:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.0.0.0 /DMyGitDescribe=v1.0.0 .\installer\CalibOperatorCLI_SelfContained.iss

#ifndef MyAppVersion
#define MyAppVersion "0.0.0.0"
#endif
#ifndef MyGitDescribe
#define MyGitDescribe "dev"
#endif

#define MyAppName "CalibOperator CLI Example"
#define MyAppExeName "CalibOperatorCLI_Example.exe"
; 相对本 .iss 所在目录（installer\）
#define PublishDir "..\XVCalibrate\CalibOperatorCLI_Example\bin\x64\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{F3E8B2C1-9D4A-4E7B-8C2A-1F9E3D7B6A2C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=CalibOperator
DefaultDirName={autopf64}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=dist
OutputBaseFilename=CalibOperatorCLI_Setup_{#MyGitDescribe}_win_x64_selfcontained
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
