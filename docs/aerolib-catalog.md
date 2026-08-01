<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Aerolib Catalog

```bash
# NID to export name
python scripts/aerolib_catalog.py lookup Zxa0VhQVTsk

# Export name to NID
python scripts/aerolib_catalog.py lookup sceKernelWaitSema

# Search export names
python scripts/aerolib_catalog.py search VideoOut --limit 20

# Export all NID/name pairs to artifacts/aerolib.txt
python scripts/aerolib_catalog.py export

# Audit unresolved and failing imports from a runtime log
python scripts/aerolib_catalog.py audit-log logs/game.log

# Save the audit as Markdown
python scripts/aerolib_catalog.py audit-log logs/game.log \
  --output diagnostics/game-abi-audit.md
```

The runtime audit compares diagnostics only against Touché PX5 source and its
local export-name catalog. It does not download or embed third-party SDK code.

Use public SDK projects as documentary references when investigating a result,
then implement and test the behavior independently. In particular, do not copy
GPLv3-covered source into Touché PX5 while the project remains distributable under
GPL-2.0-or-later.
