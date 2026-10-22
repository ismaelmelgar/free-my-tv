# Samsung: make the remote's Home button open Free My TV

*Experimental, unofficial, fork-only.* [Leer en español](samsung-home-button.es.md)

On Samsung TVs Free My TV is a normal app and Samsung's Smart Hub stays the home screen. This procedure changes the
TV's own Home assignment so that the **physical Home button on the remote opens Free My TV**. It doesn't root the
TV, doesn't install or remove anything, and keeps a copy of the original values so they can be written back.

The tool is in [`tizen/home-button/`](../tizen/home-button/).

## Status, honestly

| What | Status |
| --- | --- |
| Home button opens Free My TV | **Works** on one QN90B (see below), confirmed by pressing Home on the TV |
| Values read back after the change | Confirmed (`HOME_APPLIED`, helper exit 0) |
| `restore` (write the originals back) | Prepared, guarded, **not yet run** on real hardware |
| Survives standby / a full reboot | **Not verified yet** |
| Other models or firmware | Untested; the tool refuses anything that doesn't match what you pass it |

Don't treat it as finished until you have pressed Home from several apps and an HDMI input, checked you can still
reach Samsung's settings, and tried `restore` once.

## Tested hardware

- Samsung QN90B (2022), Tizen 6.5, ARMv7 (32-bit), firmware **T-PTMDEUC-1720.7**: works (this fork).
- Samsung Q70B, same firmware T-PTMDEUC-1720.7: tested by Arn0 with his own launcher
  ([arn0-tv](https://github.com/NelsonEU/arn0-tv), [setup guide](https://github.com/NelsonEU/arn0-tv/blob/main/docs/setup-tv.md)).

The tool only accepts Tizen 6.5 on ARMv7, and only the model and firmware you name on the command line.

## Requirements

- Free My TV installed on the TV ([CLAUDE.md](../CLAUDE.md), *Installing it on a Samsung TV*), app id
  `FreeMyTVio.FreeMyTV`.
- The TV in **Developer Mode** with this computer's IP as *Host PC IP* (Apps → `1 2 3 4 5`), reachable on SDB port
  26101, and the TV able to open a TCP connection back to this computer (allow incoming connections for Python in
  the firewall).
- Python 3 (it uses `tizen/install.py`'s SDB client, no Samsung tools needed).
- A .NET SDK (tested 7.0.203) with `NETStandard.Library.Ref` 2.1.0 to build the two small helpers. The TV runs them
  with its own `dotnet`.

## How it works

1. `home_button.py` pushes a short shell script into the SDK's temporary folder over SDB and triggers it through the
   package-name handling of `0 appinstall tpk`. The TV runs it as the **SDK user (uid 901)**, not root. This method
   was published in [samsung-tv-root](https://github.com/chris-ritsen/samsung-tv-root)
   (`src/samsung_tv_root/sdb.py`); its root exploit is **not** used here.
2. The script runs a helper with the TV's `dotnet` and sends the output back over a TCP connection to this computer,
   checked by the TV's IP and a random token. Script and helper delete themselves.
3. The helpers (adapted from Arn0's `Query.cs` and `Control.cs`) read and write the Home settings with the TV's own
   native libraries:
   - the default app of `http://tizen.org/appcontrol/operation/main` (`aul_get_default_app` /
     `app_operation_set_current_app`),
   - `db/appfw/home_appid` (vconf),
   - and they read, and keep unchanged, `db/appfw/startup_appid`, `db/appfw/booting_appid_backup`,
     `db/first_screen/type` and `db/menu/smart_hub/ontvsetting/auto_start`.

On the tested TV the original value of all of them was Smart Hub (`com.samsung.tv.csfs`) and `auto_start=1`; after
`apply` only the main operation and `home_appid` point at Free My TV.

The SDB appinstall service often answers with a refusal (`the TV refused shell:0 appinstall ...`) even though the
script ran. The tool ignores that answer and decides success only from the authenticated callback, the helper's
marker (`HOME_APPLIED=`, etc.) and `HELPER_EXIT=0`. No callback means an **unknown** state: run `query` before
anything else.

## Steps

From the repository root. Reports and baselines go to `./home-button-reports/` (git-ignored; they contain your TV's
IP and settings, keep them private and keep them safe: the baseline is your way back).

```sh
# 0. Build the helpers (once)
tizen/home-button/build.sh

# 1. Read-only: model, platform, uid, firmware, dotnet and libraries
python3 tizen/home-button/home_button.py info --tv <TV_IP>
#    take the modelName and the firmware (e.g. T-PTMDEUC-1720.7) from the report

# 2. Read the current Home settings and keep them as this TV's original values
python3 tizen/home-button/home_button.py query --tv <TV_IP> --model <MODEL> --firmware <FIRMWARE> --save-baseline
#    -> home-button-reports/baseline-<MODEL>-<token>.json  (refused if Home already points at Free My TV)
#    check launcher_boot_supported=1 in the report

# 3. Check the TV still matches that baseline (read-only)
python3 tizen/home-button/home_button.py verify --tv <TV_IP> --model <MODEL> --firmware <FIRMWARE> --baseline <BASELINE>

# 4. Point Home at Free My TV
python3 tizen/home-button/home_button.py apply --tv <TV_IP> --model <MODEL> --firmware <FIRMWARE> --baseline <BASELINE>

# 5. Whenever you want Smart Hub back
python3 tizen/home-button/home_button.py restore --tv <TV_IP> --model <MODEL> --firmware <FIRMWARE> --baseline <BASELINE>
```

`--app-id` (default `FreeMyTVio.FreeMyTV`) and `--package` (default: the part before the dot) let you target another
app. Exit code is 0 only when the TV confirmed the whole operation; still read the report.

### Guards (none of them should be removed to "make it work")

- Runner: the TV's `modelName` (network API) must equal `--model`; SDB must report Tizen 6.5 / armv7; the baseline
  must have been saved for the same model, firmware and app; all values are checked to be plain app-id-like tokens
  before they reach the TV's shell.
- Helpers: 32-bit process, uid 901, `--firmware` present in `/etc/prd_info.ini`.
- `apply`: the TV must match the baseline exactly, the app's package folder must exist and the TV must say the app
  is eligible to boot (`util_is_appbooting_supported_webapp_byappid`). Every write is read back; if anything fails
  it writes the baseline back and says so (or prints `URGENT` if that also failed).
- `restore`: refuses if any value is something other than the original or the target app (someone else changed it).
- A baseline that already names the target app is refused: it was captured after a change.

## Risks

- **Undocumented path.** It relies on how a retail TV's SDB appinstall service handles a package name, running code
  as uid 901 (`sdk`). Samsung can close it in any update, and it's not an official way to set Home.
- **Not official, no warranty.** You're changing system preferences of your TV.
- **Firmware-specific.** It was tested on one firmware. The guards stop it elsewhere; don't edit them out.
- **Never uninstall Free My TV while it is the Home app.** Run `restore` first. Reinstalling with a different
  certificate needs an uninstall too, so keep the same signing certificate.
- **Keep a way back** to Smart Hub and Samsung's settings: check that you can still open them (Free My TV's TV
  Settings tile, or the remote's own settings/menu key if yours has one), keep Developer Mode on, keep the baseline
  file and this tool.
- Firmware updates may reset or ignore the change, or close the method.

## Recovery

1. Run `query` to see the current values; don't repeat `apply` blindly (it refuses a TV that no longer matches the
   baseline anyway).
2. Run `restore` with the baseline saved in step 2.
3. If the tool can't reach the TV: open Samsung's settings from Free My TV's TV Settings tile (or the remote's
   settings key) and check the network and Developer Mode; Developer Mode can be turned on again from Apps → `1 2 3 4 5`. As a last resort a factory reset (Settings → General
   → Reset) returns Samsung's defaults; that erases your other settings too.
4. A run that stopped half way may leave `home-button-*.sh` / `.dll` in `/home/owner/share/tmp/sdk_tools/`; their
   names are in the report.

## Credits and references

- **Arn0** by Arnaud Etienne ([NelsonEU/arn0-tv](https://github.com/NelsonEU/arn0-tv), MIT): the native Home query
  and control helpers this is adapted from, and the first report on a Q70B with this firmware. His license is kept in
  [`tizen/home-button/LICENSE-arn0`](../tizen/home-button/LICENSE-arn0) and credited in each source file.
- [samsung-tv-root](https://github.com/chris-ritsen/samsung-tv-root) (chris-ritsen): the SDK-user execution method
  over SDB (root part not used).
- [CleanFrame](https://github.com/nthnclrk/CleanFrame/blob/main/docs/agent-tv-setup-guide.md): native Home assignment
  and recovery precautions on The Frame.
- [Couchroot](https://github.com/splitcipher/couchroot): Home/administration on QN90A and S90D.
- [Vetro](https://github.com/andrearicci93/vetro): service inventory with root (not used here; possible future
  reference for a reversible debloat, which this tool does not do).
- *Autorun Last App* in the TV's settings is a different thing: it opens the last app at power-on, it doesn't change
  the Home button.
