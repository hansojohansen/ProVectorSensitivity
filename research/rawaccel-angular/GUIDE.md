# Research driver guide: install, experiment, roll back

> ## ⚠ Read this first
> * This is a **research-only, test-signed, unofficial** Raw Accel driver. It is not OpenVector and
>   not official Raw Accel.
> * To load it, Windows must be in **test-signing mode**. That usually means **Secure Boot off**.
>   This lowers your PC's protection against boot-level malware for as long as it is on, so keep the
>   research period short.
> * **Never play anticheat-protected games while the research driver is installed or test-signing
>   is on.** That includes Valorant/LoL (Vanguard), FACEIT, ESEA, Call of Duty (Ricochet), EA games
>   (Javelin), and EAC- or BattlEye-protected games such as Apex, Fortnite, R6 and Rust. Vanguard and
>   FACEIT refuse to run in test mode. Others may flag your account. We know of no safe exception.
> * **Unplugging the mouse doesn't make it safe.** Raw Accel filters every mouse and touchpad, and
>   test-signing mode stays on until you turn it off.
> * **Have a working keyboard.** If the driver fails to load, all mice and touchpads stop working
>   until you repair it. Section C explains how.
> * Only the rollback in **section F**, done in that order, returns the PC to its normal state.

Written for: Windows 10/11 x64, administrator account, Logitech G Pro wireless. Plan about one hour
for setup and 15 minutes for rollback.

Commands marked **(admin)** run in an *elevated* PowerShell: Start → type `powershell` → Ctrl+Shift+Enter.

---

## A. Prepare and back up

1. **Download, before you change anything:**
   * The research artifact from GitHub Actions (`RESEARCH-ONLY-test-signed-rawaccel-angular.zip`).
   * The **official Raw Accel v1.7.1** release zip from
     <https://github.com/RawAccelOfficial/rawaccel/releases>. Extract it so that `installer.exe` is at
     `C:\RawAccel-official\installer.exe`, next to its `driver\` folder. If the zip has a top-level
     folder, move its contents up.
     You need its signed installer for the rollback, so keep it even if you never used Raw Accel.
2. **Get your BitLocker recovery key.** Do this even if you don't think BitLocker is on, because
   Windows 11 often enables device encryption automatically.
   * **(admin)** Run `manage-bde -status C:`. If it shows "Protection On", run
     `manage-bde -protectors -get C:`.
   * Write down the 48-digit *Numerical Password*, or confirm it is listed at
     <https://account.microsoft.com/devices/recoverykey>.
   * Keep it **off this PC** (on paper or your phone).
3. **Record the current state** so you can compare later. **(admin)**:
   ```powershell
   New-Item -ItemType Directory -Force C:\OpenVector-research | Out-Null
   & { "== date"; Get-Date
       "== secure boot"; try { Confirm-SecureBootUEFI } catch { "n/a: $_" }
       "== bcdedit"; bcdedit /enum '{current}'
       "== bitlocker"; manage-bde -status C:
       "== memory integrity (1 = on)"; (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -ErrorAction SilentlyContinue).Enabled
       "== rawaccel.sys"; Get-Item C:\Windows\System32\drivers\rawaccel.sys -ErrorAction SilentlyContinue | % { $_.VersionInfo.FileDescription; (Get-AuthenticodeSignature $_.FullName).SignerCertificate.Subject }
     } *> C:\OpenVector-research\state-before.txt
   ```
4. **Create a restore point (admin):**
   `Checkpoint-Computer -Description "Before OpenVector research driver" -RestorePointType MODIFY_SETTINGS`.
   If it reports that System Protection is off, turn it on for C: under *System Properties → System
   Protection*, then retry.
5. **Back up your Raw Accel settings,** if you use Raw Accel. Copy `settings.json` from your Raw Accel
   folder to `C:\OpenVector-research\settings-backup.json`.
6. **Turn off anything that rewrites Raw Accel settings at sign-in.** That means the OpenVector toggle
   "Re-apply profile at Windows sign-in" and Raw Accel GUI auto-start. The stock writer can't talk to
   the research driver, so it would only show an error dialog.
7. **Remove kernel anticheats for the research period,** because they load at boot and can block or
   flag test-signed drivers:
   * Uninstall **Riot Vanguard** (Settings → Apps → Riot Vanguard → Uninstall). Valorant/LoL
     reinstall it the next time you launch them, *after* rollback.
   * If installed, uninstall **FACEIT Anti-Cheat** and the **ESEA client**.
   * Close any game launchers.
8. **Check Smart App Control** (Windows 11: Windows Security → App & browser control →
   Smart App Control). If it is **On**, **stop here** and tell us. The research `installer.exe` and
   `writer.exe` are unsigned and will be blocked. Don't turn Smart App Control off just for this: on
   many Windows builds it can't be turned back on without resetting Windows. **[uncertain on newer
   builds]**
9. **Set up the mouse in G HUB for the experiment:**
   * Pick one fixed DPI, **1600 or higher** (e.g. 1600), with no DPI-shift profile.
   * Set **report rate to 1000 Hz**.
   * Write both down. They must stay identical in every condition.
   * The 1000 Hz recommendation comes from per-packet angle quantisation, which is much smaller at
     1 kHz than at 8 kHz for slow movements (README "Trade-off").

## B. Suspend BitLocker, disable Secure Boot, turn on test-signing

1. **Suspend BitLocker (admin):** `manage-bde -protectors -disable C: -RebootCount 0`.
   * `-RebootCount 0` keeps it suspended until you resume it in F.6.
   * `manage-bde -status C:` should now show "Protection Off". The disk stays encrypted; only the
     TPM check is paused.
   * Do this before touching Secure Boot. Otherwise the next boot asks for the recovery key.
2. **Disable Secure Boot:**
   * Go to Settings → System → Recovery → *Advanced startup: Restart now* → Troubleshoot → Advanced
     options → **UEFI Firmware Settings** → Restart.
   * In the firmware, find **Secure Boot**. It is usually under Boot or Security, and on some ASUS
     boards under "OS Type: Other OS".
   * Set it to **Disabled**. **Don't** delete or clear the Secure Boot keys, and don't switch from
     UEFI to CSM/Legacy.
   * Save and exit.
3. Back in Windows, **(admin)** run `Confirm-SecureBootUEFI`. It must print `False`.
4. **(admin)** Run `bcdedit /set testsigning on`. It must print "The operation completed successfully".
   If you get "The value is protected by Secure Boot policy", Secure Boot is still on: repeat step 2.
5. **Reboot.** "Test Mode" now shows in the bottom-right corner of the desktop.

Leave **Memory integrity** (Core isolation) as it is for now. Section C.6 covers the case where it
blocks the driver.

## C. Install the research driver

1. Extract the artifact zip to `C:\OpenVector-research\driver-pkg\`. **(admin)**:
   ```powershell
   cd C:\OpenVector-research\driver-pkg
   Get-ChildItem -Recurse | Unblock-File
   Get-Content README-RESEARCH-ONLY.txt
   ```
2. **Verify the files.** Compare the hash with `SHA256SUMS.txt`, then check the signature:
   ```powershell
   (Get-FileHash .\driver\rawaccel.sys -Algorithm SHA256).Hash.ToLower()
   Select-String rawaccel.sys .\SHA256SUMS.txt
   Get-AuthenticodeSignature .\driver\rawaccel.sys | Format-List Status, SignerCertificate
   ```
   * The two hashes must match.
   * The signer must be `CN=OpenVector RESEARCH test signing - NOT TRUSTED - NOT FOR GAMES`.
   * A status of `UnknownError` or `NotTrusted` is **expected**: the certificate is deliberately not
     trusted anywhere.
3. **Don't import `OpenVector-RESEARCH-test-cert.cer`.** In test-signing mode the kernel loads
   test-signed drivers without trusting their root. Only do the fallback in C.6 if loading fails.
4. **Install.** In the same admin PowerShell (still in `driver-pkg`), run `.\installer.exe`. It
   looks for `driver\rawaccel.sys` relative to the current folder. Read the banner, press **Y**,
   and wait for "Install complete". This replaces any existing Raw Accel driver file.
5. **Reboot**, then check that it is the research driver and that it loaded:
   ```powershell
   (Get-Item C:\Windows\System32\drivers\rawaccel.sys).VersionInfo.FileDescription   # contains "OpenVector RESEARCH build"
   driverquery /v | Select-String rawaccel                                             # State: Running
   ```
   The mouse should work exactly as before. No settings are active yet.
6. **If the mouse or touchpad doesn't work after the reboot,** the driver didn't load. Use the
   keyboard (Win+R, Tab, arrow keys, Enter):
   1. Open an admin PowerShell: Win → type `powershell` → Ctrl+Shift+Enter → Alt+Y.
   2. Look at the reason in Device Manager (`devmgmt.msc` → Mice). **Code 52** means a signature
      problem.
   3. Check that `bcdedit /enum '{current}'` shows `testsigning Yes`. If it doesn't, run
      `bcdedit /set testsigning on` and reboot.
   4. If test-signing is on and it still fails, possible causes are **Memory integrity**, which may
      block this test-signed driver **[unverified]**, or a missing trusted test root. Retry with
      one of these:
      * Windows Security → Device security → Core isolation → **Memory integrity Off**, then reboot.
        Turn it back on in F.4.
      * Or import the test certificate **(admin)**, then reboot. Remove it in F.4.
        ```powershell
        Import-Certificate .\OpenVector-RESEARCH-test-cert.cer -CertStoreLocation Cert:\LocalMachine\Root
        Import-Certificate .\OpenVector-RESEARCH-test-cert.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
        ```
   5. **To abort at any point,** **(admin)** run `cd C:\RawAccel-official; .\installer.exe`, reboot,
      and go to F.
      The official signed driver loads in any mode.

## D. Apply a gain table

The research `writer.exe` (in `driver-pkg\`) is the only writer that talks to the research driver.
Settings are lost at every reboot, so re-run the writer after rebooting.

1. **Base settings.** Use your backup (`settings-backup.json`; any Raw Accel 1.7.x file works). If you
   have none, run `.\writer.exe` once with no arguments: it creates a default `settings.json` and
   shows a usage dialog.
   * If that dialog says **"reinstallation required"**, the stock driver is still loaded. Go back
     to C.5.
   * Leave the device "DPI (normalizes input speed …)" at **0**. The table's speed axis is then in
     plain counts/ms.
2. **Make one settings file per condition.** They all start from the same base. The table tool needs
   Python 3 (`winget install Python.Python.3.12`), or OpenVector can write these files later.
   ```powershell
   cd C:\OpenVector-research\driver-pkg
   copy ..\settings-backup.json .\settings-base.json   # or the generated settings.json
   # A = identity (no table, your normal base sensitivity)
   python tools\make_angular_table.py --off --base settings-base.json --out settings-A.json
   # C = full map: g(theta) samples exported by OpenVector (D14 format: theta[] rad, gain[])
   python tools\make_angular_table.py --samples-json gmap.json --angles 64 --base settings-base.json --out settings-C.json
   ```
   **B = stock 5-DOF projection.** This is the normal Raw Accel `settings.json` that OpenVector's
   projection produces from the same g(θ). It uses only stock keys, and the research driver handles
   them exactly like the official one; the tests show bit-identical results. Use it as
   `settings-B.json`.
   * B and C must use the same base sensitivity, so that only the directional shape differs.
   * If C's table is far from 1 on average, the mean sensitivity changes. Ask the Calibration side to
     normalise both.
3. **Apply a file and check the exit code** (0 = applied; the write takes about 1 s):
   ```powershell
   (Start-Process .\writer.exe -ArgumentList 'settings-C.json' -Wait -PassThru).ExitCode
   ```
   * Any problem shows a dialog and exits with code 1. "Bad settings" lists the table errors, e.g. a
     gain outside 0.5–2 or a count that isn't angles × speeds.
4. **Sanity check that the table really acts.** Apply an exaggerated table and feel it: right is
   1.3×, left is 0.7×.
   ```powershell
   python tools\make_angular_table.py --fourier "a0=1,a1=0.3" --angles 64 --base settings-base.json --out settings-sanity.json
   (Start-Process .\writer.exe -ArgumentList 'settings-sanity.json' -Wait -PassThru).ExitCode
   ```
   * Moving right should feel faster than moving left.
   * The OpenVector trainer's raw-input view (D7, `setExtraInfo`) shows the counts before and after
     the driver.
   * Afterwards, apply `settings-A.json`.

## E. Run the experiment

* Use **only the OpenVector trainer** (or offline games without anticheat), with the G HUB DPI and
  polling rate from A.9.
* **Blind A/B/C (D11).** Conditions run in interleaved blocks, and you must not know which one is
  active. The trainer's research mode switches the files for you. Until that exists, apply a random
  condition per block like this:
  ```powershell
  # run once per block; the mapping goes to a log you don't open until the end
  $c = Get-Random -InputObject 'A','B','C'
  $code = (Start-Process .\writer.exe -ArgumentList "settings-$c.json" -Wait -PassThru).ExitCode
  Add-Content blind-log.txt ("{0:u} block -> {1} (exit {2})" -f (Get-Date), $c, $code)
  Clear-Host
  ```
  Better still, have someone else run it, or use a balanced order (each condition equally often).
  Example: 9 blocks of about 5 minutes, 3 per condition, with short breaks.
* The main metric is Fitts throughput; the trainer also reports per-direction error. Keep everything
  else fixed: in-game/trainer sensitivity, DPI, polling rate, chair and mousepad.
* After a reboot no table is active. Re-apply your current block's file before continuing.

## F. Full rollback (do the steps in this order)

> **Why the order matters:** if you turn test-signing off while the research driver is still
> installed, Windows refuses to load it. Because Raw Accel filters the whole mouse class, every mouse
> and touchpad then stops working. If that happens anyway, use the keyboard: run
> `bcdedit /set testsigning on` (admin) and reboot, or run the official installer and reboot.

1. **Reinstall the official signed driver (admin):** run `cd C:\RawAccel-official; .\installer.exe`.
   This overwrites the research `rawaccel.sys`.
   * If you don't want Raw Accel at all, run the official `.\uninstaller.exe` instead.
2. **Reboot** (you're still in Test Mode, which is fine). Then verify:
   ```powershell
   $f = 'C:\Windows\System32\drivers\rawaccel.sys'
   (Get-Item $f).VersionInfo.FileDescription                       # must NOT contain "RESEARCH"
   Get-AuthenticodeSignature $f | Format-List Status, SignerCertificate
   ```
   * You should see `Status: Valid`, signed by Microsoft (attestation-signed).
   * After using the uninstaller, the file must be gone.
3. **Turn test-signing off (admin):** run `bcdedit /set testsigning off`, then reboot. The "Test Mode"
   text must be gone, and `bcdedit /enum '{current}'` must show no `testsigning Yes`.
4. **Undo the optional C.6 steps,** if you used them:
   * Turn Memory integrity back on (Core isolation), then reboot.
   * Remove the test certificate **(admin)**:
     ```powershell
     Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher |
       Where-Object Subject -like '*OpenVector RESEARCH test signing*' | Remove-Item
     ```
5. **Re-enable Secure Boot:** go to UEFI Firmware Settings (as in B.2), set Secure Boot to
   **Enabled**, save and exit. Back in Windows, **(admin)** `Confirm-SecureBootUEFI` must print `True`.
   * If Windows asks for the BitLocker recovery key on this boot, enter the key from A.2.
6. **Resume BitLocker (admin):** run `manage-bde -protectors -enable C:`. Then
   `manage-bde -status C:` must show "Protection On".
7. **Restore your normal setup:**
   * Apply your normal settings with the **official** writer/GUI (`settings-backup.json`).
   * Turn back on what you turned off in A.6.
   * Run A.3's state command again into `state-after.txt` and compare it with `state-before.txt`.
     Secure Boot, testsigning, BitLocker, Memory integrity and the rawaccel.sys signer must all match.
8. **Only now** reinstall or launch anticheat games. Valorant reinstalls Vanguard and needs a reboot.
   If you see a VAN error about Secure Boot/TPM, re-check steps 3 and 5.

Keep `C:\OpenVector-research\` (logs, blind log, settings) for the analysis, but delete `driver-pkg\`
once you're done so the research driver can't be installed by accident.
