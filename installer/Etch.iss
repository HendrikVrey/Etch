; Etch - Inno Setup script
;
; Per-user, no elevation, no UAC prompt. Everything this writes lives under
; HKEY_CURRENT_USER and %LOCALAPPDATA%, which is the same posture the application
; itself holds (see src/Etch.App/Startup/FileAssociations.cs). Nothing here needs
; administrative rights, and nothing here affects another account on the machine.
;
; Build:  iscc installer\Etch.iss /DAppVersion=1.2.3
; Expects a published payload at:
;   publish\win-x64\Etch.exe
;   publish\win-arm64\Etch.exe
;
; One installer carries both architectures. It is a few megabytes larger than two
; downloads and it removes the only question a user cannot reliably answer about
; their own machine.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

; VersionInfoVersion must be strictly numeric (x.y.z[.w]); AppVersion may carry a
; pre-release suffix, and the tag glob v* lets one through. The workflow passes both.
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif

#define AppName        "Etch"
#define AppPublisher   "Hendrik Vrey"
#define AppUrl         "https://github.com/HendrikVrey/Etch"
#define AppExeName     "Etch.exe"

[Setup]
; Never change AppId. It is how Windows recognises an existing installation, and a
; new one turns every upgrade into a second entry in Apps & features.
AppId={{89A87B8F-DA61-48B0-B629-EAFF3D5A1D2A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}

; Per-user throughout. PrivilegesRequired=lowest is what stops the UAC prompt; the
; install directory and the uninstall entry follow from it.
; No PrivilegesRequiredOverridesAllowed. Setting it to "dialog" would offer an
; "Install for all users (requires admin)" option, and taking it elevates Setup - at
; which point {localappdata} and every Root: HKCU below resolve against the ADMIN's
; account. The payload would land in another profile, the associations and the verb in
; another user's registry, and the invoking user would get shortcuts pointing at a
; directory they cannot see. Per-user is the whole posture here; it must not be an
; option that can be clicked away.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; x64compatible matches Arm64 Windows too (it runs x64 under emulation), so this
; pair allows both and the [Files] entries below pick the right payload with
; IsArm64. Installing in 64-bit mode is not cosmetic: in 32-bit mode Windows would
; redirect Software\Classes writes into Wow6432Node, and the application - which is
; 64-bit - reads the unredirected path. The settings panel would then disagree with
; the choices made in this wizard.
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64

OutputDir=..\dist
OutputBaseFilename=Etch-Setup
SetupIconFile=..\assets\etch.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LicenseFile=..\LICENSE

; Etch holds its buffers open while it runs, so an upgrade over a running copy
; would fail on a locked file. The Restart Manager notices and asks.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Unchecked by default. FileAssociations.cs opens with the word "opt-in", and a user
; who clicks Next through a wizard has not opted in to anything. The verb below is
; left ticked because it only adds an entry to a menu; this one changes what happens
; when they double-click a file they already had a handler for.
Name: "assoc"; \
  Description: "Make {#AppName} the default for .txt, .json and .log files"; \
  Flags: unchecked
Name: "openwith"; \
  Description: "Add ""Open with {#AppName}"" when I right-click any file"
Name: "desktopicon"; \
  Description: "{cm:CreateDesktopIcon}"; \
  Flags: unchecked

[Files]
; Excludes the symbols: they are useful to keep in the release assets, not to ship
; inside every installation.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; \
  Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; \
  Check: not IsArm64
Source: "..\publish\win-arm64\*"; DestDir: "{app}"; \
  Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; \
  Check: IsArm64
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; ---------------------------------------------------------------------------
; The ProgIDs. Written ALWAYS, whether or not the association task is ticked.
;
; This is the half that cannot fail at INSTALL time: registering a ProgID and listing
; it under the extension's OpenWithProgids puts Etch in Windows' own "Open with"
; submenu whatever the user has chosen as their default, and whether or not the
; association task was ticked.
;
; It is not a permanent guarantee, and the distinction matters. Unticking one of these
; types later in Ctrl+, calls FileAssociations.Withdraw, which deletes Etch's ProgID
; key outright and removes the OpenWithProgids value with it - so Etch leaves the
; "Open with" submenu for that type too. That is a defensible reading of "stop opening
; these with Etch", but it means this section guarantees a starting state, not an
; invariant. The README says the same thing in the same words.
;
; The names and descriptions below MUST match FileAssociations.ProgIdFor and
; FileAssociations.Describe in src/Etch.App/Startup/FileAssociations.cs -
; "Etch" + extension, and "<label> (Etch)". Nothing can check that at build time,
; so the C# side carries the mirror-image comment. If you change one, change both.
; ---------------------------------------------------------------------------
Root: HKCU; Subkey: "Software\Classes\Etch.txt"; ValueType: string; ValueName: ""; ValueData: "Text files (Etch)"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Etch.txt\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\Etch.txt\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

Root: HKCU; Subkey: "Software\Classes\Etch.json"; ValueType: string; ValueName: ""; ValueData: "JSON files (Etch)"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Etch.json\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\Etch.json\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

Root: HKCU; Subkey: "Software\Classes\Etch.log"; ValueType: string; ValueName: ""; ValueData: "Log files (Etch)"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Etch.log\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\Etch.log\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

; ---------------------------------------------------------------------------
; OpenWithProgids. Also always.
;
; uninsdeletevalue, and NEVER uninsdeletekey. This key is a shared list that every
; application able to open the type adds itself to; deleting the key on uninstall
; would remove every other application's entry along with Etch's. The application's
; own FileAssociations.Withdraw is careful about exactly this, for exactly this
; reason.
; ---------------------------------------------------------------------------
Root: HKCU; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "Etch.txt"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.json\OpenWithProgids"; ValueType: string; ValueName: "Etch.json"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.log\OpenWithProgids"; ValueType: string; ValueName: "Etch.log"; ValueData: ""; Flags: uninsdeletevalue

; ---------------------------------------------------------------------------
; The "Open with Etch" verb, on the openwith task.
;
; Software\Classes\*\shell\Etch is Etch's own key under the "any file" class, so
; uninsdeletekey is correct here in a way it is not three entries above.
;
; On Windows 11 this lands under "Show more options" rather than on the short
; menu, because the short menu is reserved for shell extensions packaged as an
; MSIX-registered COM handler. That is Windows' rule, not a defect here.
; ---------------------------------------------------------------------------
Root: HKCU; Subkey: "Software\Classes\*\shell\Etch"; ValueType: string; ValueName: ""; ValueData: "Open with Etch"; Tasks: openwith; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Etch"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Open with Etch"; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\*\shell\Etch"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\*\shell\Etch\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: openwith

; The extension DEFAULTS are deliberately absent from this section. They are
; written in [Code] instead, because setting one has to preserve whatever it
; displaced - see RegisterDefault below.

[Code]

const
  SHCNE_ASSOCCHANGED = $08000000;
  SHCNF_IDLIST       = $0000;

  { Kept in step with FileAssociations.DisplacedValueName. }
  DisplacedValueName = 'EtchPreviousProgId';

procedure SHChangeNotify(wEventId: Integer; uFlags: Cardinal; dwItem1, dwItem2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

function ExtensionCount: Integer;
begin
  Result := 3;
end;

function ExtensionAt(Index: Integer): String;
begin
  case Index of
    0: Result := '.txt';
    1: Result := '.json';
  else
    Result := '.log';
  end;
end;

function ProgIdFor(Extension: String): String;
begin
  { Mirrors FileAssociations.ProgIdFor. }
  Result := 'Etch' + Extension;
end;

{
  Points an extension at Etch's ProgID, keeping whatever it displaced.

  This mirrors FileAssociations.Register, and it has to: if the installer
  overwrote the extension's default without stashing the old value, then
  unticking the box in Etch's own settings panel would call Withdraw, find
  nothing recorded, and DELETE the default outright - losing an association the
  user had before Etch was ever installed.

  Never writes UserChoice. That key is hash-protected, writing it is unsupported,
  and it is a thing malware does. On a machine that already has a default for the
  extension this procedure therefore correctly has no visible effect, and Etch's
  settings panel will say so.
}
procedure RegisterDefault(Extension: String);
var
  ExtensionKey, ProgId, Displaced, AlreadyStashed: String;
begin
  ExtensionKey := 'Software\Classes\' + Extension;
  ProgId := ProgIdFor(Extension);

  if not RegQueryStringValue(HKCU, ExtensionKey, '', Displaced) then
    Displaced := '';

  { Only when it names something else, and only when there is not already one
    recorded - reinstalling must not overwrite the original with Etch's own
    ProgID and turn the restore into a no-op. }
  if (Displaced <> '') and (CompareText(Displaced, ProgId) <> 0) then
    if not RegQueryStringValue(HKCU, 'Software\Classes\' + ProgId, DisplacedValueName, AlreadyStashed) then
      RegWriteStringValue(HKCU, 'Software\Classes\' + ProgId, DisplacedValueName, Displaced);

  RegWriteStringValue(HKCU, ExtensionKey, '', ProgId);
end;

{
  Puts back what Etch displaced, or removes the default if there was nothing.

  Mirrors FileAssociations.Withdraw, including the guard that matters most: the
  value is touched only while it still names Etch. If the user has since chosen
  another editor, that choice is theirs and uninstalling Etch must not undo it.
}
procedure UnregisterDefault(Extension: String);
var
  ExtensionKey, ProgId, Current, Displaced: String;
begin
  ExtensionKey := 'Software\Classes\' + Extension;
  ProgId := ProgIdFor(Extension);

  if not RegQueryStringValue(HKCU, ExtensionKey, '', Current) then
    Exit;

  if CompareText(Current, ProgId) <> 0 then
    Exit;

  if RegQueryStringValue(HKCU, 'Software\Classes\' + ProgId, DisplacedValueName, Displaced)
     and (Displaced <> '') then
    RegWriteStringValue(HKCU, ExtensionKey, '', Displaced)
  else
    RegDeleteValue(HKCU, ExtensionKey, '');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  if WizardIsTaskSelected('assoc') then
    for I := 0 to ExtensionCount - 1 do
      RegisterDefault(ExtensionAt(I));

  { Without this, Explorer keeps showing the old icon and the old handler until it
    is restarted, which looks exactly like the installer not having worked. }
  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  I: Integer;
begin
  { usUninstall, before the [Registry] entries are removed: UnregisterDefault has
    to read the stashed value off Etch's ProgID key, and uninsdeletekey is about
    to take that key away. }
  if CurUninstallStep <> usUninstall then
    Exit;

  for I := 0 to ExtensionCount - 1 do
    UnregisterDefault(ExtensionAt(I));

  {
    Unconditionally, and NOT left to the [Registry] section's uninsdeletekey.

    Those four entries carry "Tasks: openwith", so Inno records them in the uninstall
    log only when the box was ticked in the wizard. Etch's own settings panel writes the
    identical key through FileAssociations.SetOpenWithVerb at any later time - so
    installing with the box clear, ticking it in Ctrl+, and then uninstalling would leave
    "Open with Etch" on every file in Explorer, pointing at an executable that no longer
    exists, with nothing left that could ever remove it.

    Safe to delete outright, unlike the OpenWithProgids values above: this key is Etch's
    own, named after Etch, and nothing else writes into it.
  }
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\*\shell\Etch');
end;

procedure DeinitializeUninstall();
begin
  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
end;
