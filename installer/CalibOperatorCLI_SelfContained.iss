; Inno Setup — 工业视觉工具 自包含 (win-x64) 安装包
; 一键（git 版本 + publish + 安装包）:
;   双击: installer\build-self-contained-installer.bat
;   或: powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\build-self-contained-installer.ps1
; 仅打包（需已有 publish 目录）: .\installer\compile-setup.ps1
; 勿单独双击本 .iss，否则为默认占位版本（未传 /DMyAppVersion、/DMyGitDescribe）。
; 多版本共存: build-self-contained-installer.ps1 传入 /DMyAppInstanceGuid /DMyInstallSuffix（由 git describe 推导）。
; 手动 ISCC / 非默认安装路径: 设置用户环境变量 ISCC 为 ISCC.exe 完整路径，或:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.0.0.0 /DMyGitDescribe=v1.0.0 .\installer\CalibOperatorCLI_SelfContained.iss

#ifndef MyAppVersion
#define MyAppVersion "0.0.0.0"
#endif
#ifndef MyGitDescribe
#define MyGitDescribe "dev"
#endif
#ifndef MyAppInstanceGuid
#define MyAppInstanceGuid "F3E8B2C1-9D4A-4E7B-8C2A-1F9E3D7B6A2C"
#endif
#ifndef MyInstallSuffix
#define MyInstallSuffix "dev"
#endif

#define MyAppName "工业视觉工具"
; Per-user install ({autopf64} -> LocalAppData\Programs when PrivilegesRequired=lowest): use ASCII dir to match exe and avoid stale folder names from upgrades.
#define MyAppDirName "IndustrialVisionTools"
#define MyAppExeName "IndustrialVisionTools.exe"
; 相对本 .iss 所在目录（installer\）
#define PublishDir "..\XVCalibrate\IndustrialVisionTools\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; AppId is a plain string (may include preprocessor); do not use {{GUID}} — Inno treats {{ as a constant escape and misparses dynamic GUIDs.
AppId={#MyAppInstanceGuid}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=CalibOperator
DefaultDirName={autopf64}\{#MyAppDirName}_{#MyInstallSuffix}
DefaultGroupName={#MyAppName} ({#MyInstallSuffix})
UsePreviousAppDir=yes
OutputDir=dist
OutputBaseFilename=IndustrialVisionTools_Setup_{#MyGitDescribe}_win_x64_selfcontained
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
; 向导语言：使用安装器自带的 Default.isl（英文）。若需简体中文向导，请安装 Inno 官方「ChineseSimplified」翻译包后，将下行改为:
;   Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
; 翻译包: https://jrsoftware.org/files/istrans/

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName} ({#MyInstallSuffix})"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
