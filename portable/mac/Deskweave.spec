# Deskweave.app build definition. Run it through mac/build.sh on a Mac.
import sys
from pathlib import Path

KIT = Path(SPECPATH).parent
sys.path.insert(0, str(KIT))
from deskweave import __version__

a = Analysis([str(KIT / "mac" / "app.py")], pathex=[str(KIT)], datas=[(str(KIT / "web"), "web")])
exe = EXE(PYZ(a.pure), a.scripts, exclude_binaries=True, name="Deskweave", console=False)
coll = COLLECT(exe, a.binaries, a.datas, name="Deskweave")
if sys.platform == "darwin":
    app = BUNDLE(coll, name="Deskweave.app", icon=str(KIT / "mac" / "Deskweave.ico"),
                 bundle_identifier="app.deskweave.mac", version=__version__,
                 info_plist={"NSHighResolutionCapable": True})
